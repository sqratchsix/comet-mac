using System.Globalization;
using System.Runtime.InteropServices;

namespace Comet.Core;

/// <summary>
/// Finds vendor-specific USB interfaces with a bulk IN and bulk OUT endpoint and
/// offers them as ports. This is how cellular modules such as the Quectel RM520N
/// present their AT command ports: Linux binds them with its option driver,
/// macOS binds nothing, so they never appear under /dev.
/// </summary>
public static class UsbBulkEnumerator
{
    public const string PathPrefix = "usb:";

    private const byte VendorSpecificClass = 0xFF;
    private const int QuectelVendorId = 0x2C7C;

    /// <summary>
    /// Quectel keeps the same layout on its LTE and 5G modules (EC2x, EG, RM5xx):
    /// interface 0 is Qualcomm DIAG, 1 is GNSS NMEA, 2 is AT, 3 is AT/PPP.
    /// </summary>
    private static readonly string[] QuectelRoles = { "DM", "NMEA", "AT", "Modem" };

    private static readonly string[] QuectelRoleDescriptions =
    {
        "Qualcomm diagnostics (binary)",
        "GNSS NMEA output",
        "AT commands",
        "AT commands and PPP data",
    };

    /// <summary>
    /// Returns an empty list when libusb is not installed, and never throws.
    /// </summary>
    public static IReadOnlyList<SerialPortInfo> GetPorts()
    {
        var result = new List<SerialPortInfo>();
        try
        {
            if (!LibUsb.IsAvailable) return result;
            ScanDevices(result);
        }
        catch (Exception)
        {
            // A misbehaving device must not stop the serial ports from listing.
        }
        return result;
    }

    private static void ScanDevices(List<SerialPortInfo> result)
    {
        nint count = LibUsb.libusb_get_device_list(LibUsb.Context, out IntPtr list);
        if (count < 0) return;
        try
        {
            for (int i = 0; i < count; i++)
            {
                IntPtr device = Marshal.ReadIntPtr(list, i * IntPtr.Size);
                try
                {
                    AddDevice(device, result);
                }
                catch (Exception)
                {
                    // Skip this device and keep going.
                }
            }
        }
        finally
        {
            LibUsb.libusb_free_device_list(list, 1);
        }
    }

    private static void AddDevice(IntPtr device, List<SerialPortInfo> result)
    {
        var descriptor = new byte[18];
        if (LibUsb.libusb_get_device_descriptor(device, descriptor) != 0) return;
        int vendorId = descriptor[8] | (descriptor[9] << 8);
        int productId = descriptor[10] | (descriptor[11] << 8);
        byte productIndex = descriptor[15];

        var candidates = FindBulkInterfaces(device);
        if (candidates.Count == 0) return;

        // Opening a device on macOS does not claim it, so reading its strings is harmless.
        string product = string.Empty;
        var names = new Dictionary<int, string>();
        var driven = new HashSet<int>();
        if (LibUsb.libusb_open(device, out IntPtr handle) == 0)
        {
            try
            {
                product = LibUsb.GetString(handle, productIndex);
                foreach (var c in candidates)
                {
                    names[c.Number] = LibUsb.GetString(handle, c.NameIndex);
                    // A vendor interface macOS already drives, such as a CH340 or
                    // CP210x bridge, is a /dev/cu port already; listing it again
                    // here would offer the same device twice.
                    if (LibUsb.libusb_kernel_driver_active(handle, c.Number) == 1) driven.Add(c.Number);
                }
            }
            finally
            {
                LibUsb.libusb_close(handle);
            }
        }
        if (string.IsNullOrEmpty(product)) product = $"{vendorId:x4}:{productId:x4}";

        string location = GetLocation(device);
        foreach (var c in candidates)
        {
            if (driven.Contains(c.Number)) continue;
            string interfaceName = names.GetValueOrDefault(c.Number, string.Empty);

            // Debug probes such as the XIAO's CMSIS-DAP bridge expose a vendor bulk
            // interface too. Typing text at it is never what anyone wants.
            if (interfaceName.Contains("CMSIS-DAP", StringComparison.OrdinalIgnoreCase)) continue;

            string role;
            string roleDescription;
            if (vendorId == QuectelVendorId && c.Number < QuectelRoles.Length)
            {
                role = QuectelRoles[c.Number];
                roleDescription = QuectelRoleDescriptions[c.Number];
            }
            else
            {
                role = string.IsNullOrEmpty(interfaceName) ? $"if{c.Number}" : interfaceName;
                roleDescription = "vendor bulk interface";
            }

            string path = BuildPath(vendorId, productId, location, c.Number);
            result.Add(new SerialPortInfo(
                path,
                $"{product} {role}",
                $"USB interface {c.Number}, {roleDescription}",
                isUsb: true,
                kind: PortKind.UsbBulk));
        }
    }

    private readonly record struct BulkInterface(int Number, byte NameIndex);

    /// <summary>
    /// Walks libusb's parsed configuration descriptor. Offsets are those of the
    /// structs in libusb.h on a 64-bit build.
    /// </summary>
    private static List<BulkInterface> FindBulkInterfaces(IntPtr device)
    {
        var found = new List<BulkInterface>();
        if (LibUsb.libusb_get_active_config_descriptor(device, out IntPtr config) != 0) return found;
        try
        {
            int interfaceCount = Marshal.ReadByte(config, 4);
            IntPtr interfaces = Marshal.ReadIntPtr(config, 16);
            for (int i = 0; i < interfaceCount; i++)
            {
                IntPtr iface = interfaces + i * 16;
                IntPtr altsettings = Marshal.ReadIntPtr(iface, 0);
                int altCount = Marshal.ReadInt32(iface, 8);
                if (altCount < 1) continue;

                // Only the default alternate setting matters for these interfaces.
                IntPtr alt = altsettings;
                int number = Marshal.ReadByte(alt, 2);
                int endpointCount = Marshal.ReadByte(alt, 4);
                byte interfaceClass = Marshal.ReadByte(alt, 5);
                byte nameIndex = Marshal.ReadByte(alt, 8);
                if (interfaceClass != VendorSpecificClass) continue;

                if (FindBulkEndpoints(Marshal.ReadIntPtr(alt, 16), endpointCount, out _, out _, out _))
                    found.Add(new BulkInterface(number, nameIndex));
            }
        }
        finally
        {
            LibUsb.libusb_free_config_descriptor(config);
        }
        return found;
    }

    /// <summary>Picks the first bulk IN and bulk OUT endpoint from an endpoint array.</summary>
    internal static bool FindBulkEndpoints(IntPtr endpoints, int count, out byte inAddress, out byte outAddress,
        out int maxPacket)
    {
        inAddress = 0;
        outAddress = 0;
        maxPacket = 64;
        for (int e = 0; e < count; e++)
        {
            IntPtr endpoint = endpoints + e * 32;
            byte address = Marshal.ReadByte(endpoint, 2);
            byte attributes = Marshal.ReadByte(endpoint, 3);
            if ((attributes & 0x03) != 0x02) continue;

            if ((address & 0x80) != 0)
            {
                if (inAddress == 0) inAddress = address;
            }
            else if (outAddress == 0)
            {
                outAddress = address;
                maxPacket = Math.Max(1, (int)(ushort)Marshal.ReadInt16(endpoint, 4));
            }
        }
        return inAddress != 0 && outAddress != 0;
    }

    /// <summary>Bus and port chain, e.g. "1-3.2.1", stable while the cable stays in the same socket.</summary>
    internal static string GetLocation(IntPtr device)
    {
        var ports = new byte[8];
        int depth = LibUsb.libusb_get_port_numbers(device, ports, ports.Length);
        byte bus = LibUsb.libusb_get_bus_number(device);
        string chain = depth > 0 ? string.Join(".", ports.Take(depth)) : "0";
        return $"{bus}-{chain}";
    }

    /// <summary>usb:2c7c:0801@1-3.2.1:2</summary>
    public static string BuildPath(int vendorId, int productId, string location, int interfaceNumber) =>
        $"{PathPrefix}{vendorId:x4}:{productId:x4}@{location}:{interfaceNumber}";

    public static bool IsUsbPath(string path) => path.StartsWith(PathPrefix, StringComparison.Ordinal);

    public static bool TryParsePath(string path, out int vendorId, out int productId, out string location,
        out int interfaceNumber)
    {
        vendorId = productId = interfaceNumber = 0;
        location = string.Empty;
        if (!IsUsbPath(path)) return false;

        string rest = path.Substring(PathPrefix.Length);
        int at = rest.IndexOf('@');
        int lastColon = rest.LastIndexOf(':');
        if (at < 0 || lastColon < at) return false;

        string[] ids = rest.Substring(0, at).Split(':');
        location = rest.Substring(at + 1, lastColon - at - 1);
        return ids.Length == 2
               && int.TryParse(ids[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vendorId)
               && int.TryParse(ids[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out productId)
               && int.TryParse(rest.AsSpan(lastColon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture,
                   out interfaceNumber)
               && location.Length > 0;
    }
}
