using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Comet.Core;

/// <summary>
/// Enumerates serial devices on macOS.
///
/// Two things the Windows build got for free that need doing by hand here:
///
/// 1. SerialPort.GetPortNames() returns the /dev/tty.* dial-in devices. Opening
///    those blocks until carrier detect is asserted, which for most USB adapters
///    never happens, so the app appears to hang on Open Port. The call-out
///    devices (/dev/cu.*) are the correct ones for a terminal program.
///
/// 2. Friendly names came from the WMI query Win32_PnPEntity. The equivalent
///    here is the IOKit registry, read through the ioreg command line tool:
///    each IOSerialBSDClient node carries the /dev path, and its USB ancestor
///    carries the product and vendor strings.
/// </summary>
public static class MacSerialPortEnumerator
{
    private static readonly Regex NodeLine = new(@"^(?<indent>[\s|+\-]*)\+-o\s", RegexOptions.Compiled);
    private static readonly Regex KeyValue = new("^\\s*[|\\s]*\"(?<key>[^\"]+)\"\\s*=\\s*\"(?<value>[^\"]*)\"", RegexOptions.Compiled);

    /// <summary>
    /// Returns every call-out serial device plus any vendor-class USB interfaces
    /// libusb can reach, USB devices first, then natural sort.
    /// Never throws: on any failure it falls back to a bare /dev/cu.* directory scan.
    /// </summary>
    public static IReadOnlyList<SerialPortInfo> GetPorts()
    {
        var devices = ScanDevDirectory();
        Dictionary<string, IoRegEntry> details;

        try
        {
            details = ReadIoRegistry();
        }
        catch (Exception)
        {
            details = new Dictionary<string, IoRegEntry>();
        }

        var result = new List<SerialPortInfo>();
        foreach (var device in devices)
        {
            string shortName = device.StartsWith("/dev/cu.", StringComparison.Ordinal)
                ? device.Substring("/dev/cu.".Length)
                : device;

            if (details.TryGetValue(device, out var entry))
            {
                // Paired Bluetooth devices: opening one makes the Mac try to connect
                // to a speaker or tablet, which is never what COMET is for.
                if (entry.IsBluetooth) continue;

                // Built-in ports appear in ioreg with no USB ancestor and therefore
                // no product name, so fall back to a hand written description.
                string description = string.IsNullOrEmpty(entry.Description)
                    ? DescribeKnownPort(shortName)
                    : entry.Description;
                result.Add(new SerialPortInfo(device, shortName, description, entry.IsUsb));
            }
            else
            {
                // ioreg unavailable: the one Bluetooth port with a fixed name still goes.
                if (shortName.Equals("Bluetooth-Incoming-Port", StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(new SerialPortInfo(device, shortName, DescribeKnownPort(shortName), false));
            }
        }

        result.AddRange(UsbBulkEnumerator.GetPorts());

        IComparer<string> comparer = new NaturalComparer();
        result.Sort((a, b) =>
        {
            // USB adapters are what people actually plug in, so float them to the top;
            // the Bluetooth and debug-console pseudo ports sink to the bottom.
            // Raw USB interfaces follow the USB serial ports.
            if (a.IsUsb != b.IsUsb) return a.IsUsb ? -1 : 1;
            if (a.Kind != b.Kind) return a.Kind.CompareTo(b.Kind);
            return comparer.Compare(a.ShortName, b.ShortName);
        });
        return result;
    }

    private static List<string> ScanDevDirectory()
    {
        var devices = new List<string>();
        try
        {
            foreach (var path in Directory.GetFiles("/dev", "cu.*"))
            {
                devices.Add(path);
            }
        }
        catch (Exception)
        {
            // /dev unreadable is not recoverable, but an empty list is better than a crash.
        }
        return devices;
    }

    /// <summary>
    /// Gives the built-in pseudo devices a description so the dropdown is not
    /// full of bare names with no explanation.
    /// </summary>
    private static string DescribeKnownPort(string shortName)
    {
        if (shortName.StartsWith("Bluetooth", StringComparison.OrdinalIgnoreCase))
            return "Bluetooth serial port";
        if (shortName.Equals("debug-console", StringComparison.OrdinalIgnoreCase))
            return "System debug console";
        return string.Empty;
    }

    private readonly struct IoRegEntry
    {
        public IoRegEntry(string description, bool isUsb, bool isBluetooth)
        {
            Description = description;
            IsUsb = isUsb;
            IsBluetooth = isBluetooth;
        }

        public string Description { get; }
        public bool IsUsb { get; }

        /// <summary>Created by macOS's Bluetooth serial driver for a paired device.</summary>
        public bool IsBluetooth { get; }
    }

    // The driver behind /dev/cu.* ports for paired Bluetooth Classic devices.
    // Older macOS releases used kernel classes with Bluetooth in the name instead.
    private const string BluetoothSerialDriver = "com.apple.IOUserBluetoothSerialDriver";

    /// <summary>
    /// Walks the ioreg tree and maps each IOCalloutDevice path to the product name
    /// of its nearest USB ancestor. The tree is printed depth first with indentation,
    /// so tracking a stack of (indent, name) pairs and popping entries that are no
    /// longer ancestors gives the correct association even with several devices attached.
    /// </summary>
    private static Dictionary<string, IoRegEntry> ReadIoRegistry()
    {
        var map = new Dictionary<string, IoRegEntry>(StringComparer.Ordinal);
        string output = RunIoReg();
        if (string.IsNullOrEmpty(output)) return map;

        // Stack of USB ancestors currently in scope: indent depth -> best known name.
        var ancestors = new List<(int Indent, string Name)>();
        // Indents of Bluetooth serial driver nodes currently in scope.
        var bluetooth = new List<int>();
        int currentNodeIndent = 0;

        foreach (var line in output.Split('\n'))
        {
            var nodeMatch = NodeLine.Match(line);
            if (nodeMatch.Success)
            {
                currentNodeIndent = nodeMatch.Groups["indent"].Value.Length;
                // Drop ancestors we have walked back out of.
                for (int i = ancestors.Count - 1; i >= 0; i--)
                {
                    if (ancestors[i].Indent >= currentNodeIndent)
                        ancestors.RemoveAt(i);
                }
                bluetooth.RemoveAll(indent => indent >= currentNodeIndent);
                if (line.Contains("<class IOBluetooth", StringComparison.Ordinal))
                    bluetooth.Add(currentNodeIndent);
                continue;
            }

            var kvMatch = KeyValue.Match(line);
            if (!kvMatch.Success) continue;

            string key = kvMatch.Groups["key"].Value;
            string value = kvMatch.Groups["value"].Value;

            switch (key)
            {
                case "USB Product Name":
                    // Belongs to the node currently being printed.
                    ancestors.Add((currentNodeIndent, value));
                    break;

                case "Product Name":
                    // Driver nodes below a USB device carry generic names such as
                    // "USB ACM", so only use this when nothing above has a real name.
                    if (ancestors.Count == 0)
                        ancestors.Add((currentNodeIndent, value));
                    break;

                case "USB Vendor Name":
                    // Only used when no product name showed up for this node.
                    if (!ancestors.Any(a => a.Indent == currentNodeIndent))
                        ancestors.Add((currentNodeIndent, value));
                    break;

                case "IOUserServerName":
                    if (value == BluetoothSerialDriver) bluetooth.Add(currentNodeIndent);
                    break;

                case "IOCalloutDevice":
                    string name = ancestors.Count > 0 ? ancestors[^1].Name : string.Empty;
                    map[value] = new IoRegEntry(name, ancestors.Count > 0, bluetooth.Count > 0);
                    break;
            }
        }

        return map;
    }

    private static string RunIoReg()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/usr/sbin/ioreg",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-w0");
        psi.ArgumentList.Add("-l");
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add("IOService");

        using var process = Process.Start(psi);
        if (process == null) return string.Empty;

        string output = process.StandardOutput.ReadToEnd();
        // ioreg on a busy machine still returns promptly; the cap is a safety net only.
        if (!process.WaitForExit(5000))
        {
            try { process.Kill(true); } catch (Exception) { }
            return string.Empty;
        }
        return output;
    }
}
