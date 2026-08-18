namespace Comet.Core;

/// <summary>
/// A serial device the app can open, plus the friendly name shown in the UI.
/// Replaces the Win32_PnPEntity WMI lookup the Windows build used to turn
/// "COM3" into "USB Serial Port (COM3)".
/// </summary>
public sealed class SerialPortInfo
{
    /// <summary>Device path passed to SerialPort, e.g. /dev/cu.usbserial-0001.</summary>
    public string DeviceName { get; }

    /// <summary>Short name shown in the port dropdown, e.g. usbserial-0001.</summary>
    public string ShortName { get; }

    /// <summary>Human readable device description, e.g. "CP2102 USB to UART Bridge Controller".</summary>
    public string Description { get; }

    /// <summary>True for USB-attached devices, which sort ahead of built-in ports.</summary>
    public bool IsUsb { get; }

    public SerialPortInfo(string deviceName, string shortName, string description, bool isUsb)
    {
        DeviceName = deviceName;
        ShortName = shortName;
        Description = description;
        IsUsb = isUsb;
    }

    /// <summary>Text shown in the combo box.</summary>
    public string DisplayName =>
        string.IsNullOrEmpty(Description) || Description == ShortName
            ? ShortName
            : $"{ShortName}  –  {Description}";

    public override string ToString() => DisplayName;
}
