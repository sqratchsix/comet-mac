using System.Reflection;
using System.Runtime.InteropServices;

namespace Comet.Core;

/// <summary>
/// The handful of libusb-1.0 calls COMET needs to talk to vendor-class USB
/// interfaces. macOS has no driver that turns those into /dev/cu devices, but
/// nothing claims them either, so libusb can open them from user space without
/// a kext or DriverKit extension.
///
/// libusb is loaded at run time: first from the app bundle, where
/// scripts/build-app.sh copies it, then from Homebrew and MacPorts. When none is
/// found, <see cref="IsAvailable"/> is false and COMET lists only serial ports.
/// </summary>
internal static partial class LibUsb
{
    private const string Library = "libusb-1.0";

    public const int ErrorTimeout = -7;
    public const int ErrorNoDevice = -4;

    private static readonly string[] Candidates =
    {
        Path.Combine(AppContext.BaseDirectory, "libusb-1.0.0.dylib"),
        "/opt/homebrew/lib/libusb-1.0.0.dylib",
        "/usr/local/lib/libusb-1.0.0.dylib",
        "/opt/local/lib/libusb-1.0.0.dylib",
    };

    private static readonly object InitLock = new();
    private static IntPtr context;
    private static bool? available;

    static LibUsb()
    {
        NativeLibrary.SetDllImportResolver(typeof(LibUsb).Assembly, Resolve);
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Library) return IntPtr.Zero;
        foreach (var path in Candidates)
        {
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle)) return handle;
        }
        return IntPtr.Zero;
    }

    /// <summary>True once libusb has loaded and initialised a context.</summary>
    public static bool IsAvailable
    {
        get
        {
            lock (InitLock)
            {
                if (available.HasValue) return available.Value;
                try
                {
                    available = libusb_init(out context) == 0;
                }
                catch (Exception)
                {
                    // DllNotFoundException when no libusb is installed.
                    available = false;
                }
                return available.Value;
            }
        }
    }

    /// <summary>The shared context. Only valid after <see cref="IsAvailable"/> returned true.</summary>
    public static IntPtr Context => context;

    [LibraryImport(Library)]
    private static partial int libusb_init(out IntPtr ctx);

    [LibraryImport(Library)]
    public static partial nint libusb_get_device_list(IntPtr ctx, out IntPtr list);

    [LibraryImport(Library)]
    public static partial void libusb_free_device_list(IntPtr list, int unrefDevices);

    [LibraryImport(Library)]
    public static partial int libusb_get_device_descriptor(IntPtr dev, [Out] byte[] desc);

    [LibraryImport(Library)]
    public static partial int libusb_get_active_config_descriptor(IntPtr dev, out IntPtr config);

    [LibraryImport(Library)]
    public static partial void libusb_free_config_descriptor(IntPtr config);

    [LibraryImport(Library)]
    public static partial byte libusb_get_bus_number(IntPtr dev);

    [LibraryImport(Library)]
    public static partial int libusb_get_port_numbers(IntPtr dev, [Out] byte[] ports, int length);

    [LibraryImport(Library)]
    public static partial int libusb_open(IntPtr dev, out IntPtr handle);

    [LibraryImport(Library)]
    public static partial void libusb_close(IntPtr handle);

    [LibraryImport(Library)]
    public static partial int libusb_get_string_descriptor_ascii(IntPtr handle, byte index, [Out] byte[] data, int length);

    [LibraryImport(Library)]
    public static partial int libusb_claim_interface(IntPtr handle, int interfaceNumber);

    /// <summary>1 when a macOS driver owns the interface, 0 when it is free, negative on error.</summary>
    [LibraryImport(Library)]
    public static partial int libusb_kernel_driver_active(IntPtr handle, int interfaceNumber);

    [LibraryImport(Library)]
    public static partial int libusb_release_interface(IntPtr handle, int interfaceNumber);

    [LibraryImport(Library)]
    public static partial int libusb_bulk_transfer(IntPtr handle, byte endpoint, byte[] data, int length,
        out int transferred, uint timeoutMs);

    /// <summary>Reads a string descriptor, or returns an empty string if there is none.</summary>
    public static string GetString(IntPtr handle, byte index)
    {
        if (index == 0 || handle == IntPtr.Zero) return string.Empty;
        var buffer = new byte[256];
        int length = libusb_get_string_descriptor_ascii(handle, index, buffer, buffer.Length);
        return length > 0 ? System.Text.Encoding.ASCII.GetString(buffer, 0, length).Trim() : string.Empty;
    }
}
