using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;

namespace Comet.Core;

/// <summary>
/// Owns one open serial connection. Ported from Comet1.ActiveSerialPort.
///
/// The one structural change from the Windows version is how bytes arrive.
/// The original hung off SerialPort.DataReceived, which on Unix fires through a
/// polling loop that drops events under load and is documented as unreliable.
/// This version runs a dedicated read loop on the BaseStream and keeps the
/// original's "coalesce until the line goes quiet" behaviour with an idle timer,
/// so the terminal still receives whole responses rather than character fragments.
///
/// Bytes are buffered raw and only decoded when read, which means switching
/// between ASCII and HEX no longer mangles data already in flight.
///
/// A usb: port name opens a <see cref="UsbBulkStream"/> instead of a SerialPort.
/// The read loop and sending work the same; baud, control lines and break do not
/// apply and are ignored.
/// </summary>
public sealed class ActiveSerialPort : IDisposable
{
    private const int ReadBufferSize = 4096;

    private SerialPort? activeSerial;
    private UsbBulkStream? usbStream;
    private CancellationTokenSource? readCancellation;
    private Task? readLoop;
    private System.Timers.Timer? idleTimer;

    private readonly List<byte> pendingBytes = new();
    private readonly object pendingLock = new();

    private Parity parity = Parity.None;
    private StopBits stopBits = StopBits.One;
    private int portTimeoutWriteMs = 2000;
    private int portTimeoutReadMs = 2000;
    private int portReadTimeoutMs = 20;
    private string lineEnd = "\r\n";

    public string PortName { get; set; } = "";
    public int BaudRate { get; set; } = 115200;
    public int DataBits { get; set; } = 8;
    public bool IsOpen { get; private set; }
    public SerialDataType DataType { get; set; } = SerialDataType.Ascii;
    public SerialPort? Port => activeSerial;
    public bool IsUsbBulk => usbStream != null;

    /// <summary>Why the last open failed, for the terminal. Empty after a successful open.</summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>Raised once the inbound line has been quiet for the read timeout.</summary>
    public event EventHandler? DataReadyToRead;

    public string LineEnding
    {
        get => lineEnd;
        set => lineEnd = value;
    }

    /// <summary>
    /// Latin-1 rather than the ASCII default of the Windows build: it round-trips
    /// all 256 byte values, so binary-ish output is shown instead of turning into
    /// question marks. For the 0x00-0x7F range the two are identical.
    /// </summary>
    private static readonly Encoding WireEncoding = Encoding.Latin1;

    public bool CreateBasicSerialPort(string portName, int baudRate, SerialDataType dataType)
    {
        PortName = portName;
        BaudRate = baudRate;
        DataType = dataType;
        return CreateSerialPort();
    }

    public bool OpenSerialPort(string portName, int baudRate, Parity portParity, int dataBits,
        StopBits portStopBits, int timeoutMs, SerialDataType dataType, int readTimeout)
    {
        DataType = dataType;
        PortName = portName;
        BaudRate = baudRate;
        parity = portParity;
        DataBits = dataBits;
        stopBits = portStopBits;
        portTimeoutReadMs = timeoutMs;
        portTimeoutWriteMs = timeoutMs;
        portReadTimeoutMs = readTimeout;
        return CreateSerialPort();
    }

    public bool CreateSerialPort()
    {
        LastError = string.Empty;
        if (UsbBulkEnumerator.IsUsbPath(PortName)) return CreateUsbBulkPort();

        try
        {
            activeSerial = new SerialPort(PortName, BaudRate, parity, DataBits, stopBits)
            {
                Encoding = WireEncoding,
                Handshake = Handshake.None
            };
            activeSerial.Open();
            UpdateReadWriteTimeout();
            IsOpen = activeSerial.IsOpen;

            if (IsOpen)
            {
                StartReadLoop(activeSerial.BaseStream);
            }
            return IsOpen;
        }
        catch (Exception ex)
        {
            // Matches the original: a port that will not open is reported as closed,
            // the caller moves on to the next candidate port.
            LastError = ex.Message;
            IsOpen = false;
            CleanUp();
            return false;
        }
    }

    private bool CreateUsbBulkPort()
    {
        try
        {
            usbStream = UsbBulkStream.Open(PortName);
            usbStream.WriteTimeout = portTimeoutWriteMs;
            IsOpen = true;
            StartReadLoop(usbStream);
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            IsOpen = false;
            CleanUp();
            return false;
        }
    }

    private void StartReadLoop(Stream stream)
    {
        idleTimer = new System.Timers.Timer(Math.Max(1, portReadTimeoutMs)) { AutoReset = false };
        idleTimer.Elapsed += (_, _) => DataReadyToRead?.Invoke(this, EventArgs.Empty);

        readCancellation = new CancellationTokenSource();
        var token = readCancellation.Token;

        readLoop = Task.Run(async () =>
        {
            var buffer = new byte[ReadBufferSize];
            while (!token.IsCancellationRequested)
            {
                try
                {
                    int count = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token)
                        .ConfigureAwait(false);
                    if (count <= 0)
                    {
                        // Device went away (unplugged adapter). Stop quietly.
                        break;
                    }

                    lock (pendingLock)
                    {
                        pendingBytes.AddRange(buffer.AsSpan(0, count).ToArray());
                    }

                    // Restart the quiet timer on every chunk, exactly as the
                    // Windows build restarted its System.Timers.Timer per event.
                    idleTimer?.Stop();
                    idleTimer?.Start();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (TimeoutException)
                {
                    // BaseStream reads should not time out, but ignore it if they do.
                }
                catch (Exception)
                {
                    // Unplugged mid-read, or the port was closed underneath us.
                    break;
                }
            }
        }, token);
    }

    public int CloseSerialPort()
    {
        try
        {
            CleanUp();
            IsOpen = false;
            return 1;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private void CleanUp()
    {
        try { readCancellation?.Cancel(); } catch (Exception) { }

        try { idleTimer?.Stop(); idleTimer?.Dispose(); } catch (Exception) { }
        idleTimer = null;

        try
        {
            if (activeSerial != null)
            {
                if (activeSerial.IsOpen) activeSerial.Close();
                activeSerial.Dispose();
            }
        }
        catch (Exception) { }
        activeSerial = null;

        try { usbStream?.Dispose(); } catch (Exception) { }
        usbStream = null;

        try { readCancellation?.Dispose(); } catch (Exception) { }
        readCancellation = null;
        readLoop = null;
    }

    public void SetPortTimeout(int writeTimeout, int readTimeout)
    {
        portTimeoutReadMs = readTimeout;
        portTimeoutWriteMs = writeTimeout;
        if (IsOpen) UpdateReadWriteTimeout();
    }

    private void UpdateReadWriteTimeout()
    {
        if (usbStream != null) usbStream.WriteTimeout = portTimeoutWriteMs;
        if (activeSerial == null) return;
        activeSerial.WriteTimeout = portTimeoutWriteMs;
        activeSerial.ReadTimeout = portTimeoutReadMs;
    }

    public void ChangeReadTimeout(int newReadTimeout)
    {
        portReadTimeoutMs = newReadTimeout;
        if (idleTimer != null) idleTimer.Interval = Math.Max(1, portReadTimeoutMs);
    }

    public int GetReadTimeout() => portReadTimeoutMs;
    public int GetWriteTimeout() => portTimeoutWriteMs;

    public string GetConnectionInfo(int returnFormat)
    {
        if (!IsOpen) return "No Connection";
        if (IsUsbBulk) return PortName;
        return returnFormat == 1
            ? $"{PortName}:  {BaudRate},{parity},{DataBits},{stopBits}"
            : $"{PortName}  ({BaudRate})";
    }

    /// <summary>
    /// Drains everything buffered since the last call, formatted for the current data type.
    /// </summary>
    public string ReadData()
    {
        byte[] data;
        lock (pendingLock)
        {
            if (pendingBytes.Count == 0) return string.Empty;
            data = pendingBytes.ToArray();
            pendingBytes.Clear();
        }

        return DataType switch
        {
            SerialDataType.Ascii => WireEncoding.GetString(data),
            SerialDataType.Hex => BitConverter.ToString(data).Replace("-", ""),
            _ => WireEncoding.GetString(data)
        };
    }

    public string SendData(string dataToSend, bool endline)
    {
        if (!IsOpen || (activeSerial == null && usbStream == null) || string.IsNullOrEmpty(dataToSend))
            return string.Empty;

        try
        {
            switch (DataType)
            {
                case SerialDataType.Ascii:
                    string payload = endline ? dataToSend + lineEnd : dataToSend;
                    WriteBytes(WireEncoding.GetBytes(payload));
                    return dataToSend;

                case SerialDataType.Hex:
                    string formatted = ValidateStringIsHex(dataToSend);
                    byte[] bytes = ConvertHexStringToBytes(formatted);
                    WriteBytes(bytes);
                    return formatted;

                default:
                    return string.Empty;
            }
        }
        catch (Exception)
        {
            return "ERROR";
        }
    }

    /// <summary>
    /// SerialPort.Write(string) encodes with the port's Latin-1 encoding, so encoding
    /// here first sends the same bytes on either transport.
    /// </summary>
    private void WriteBytes(byte[] bytes)
    {
        if (usbStream != null) usbStream.Write(bytes, 0, bytes.Length);
        else activeSerial?.Write(bytes, 0, bytes.Length);
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        if (usbStream != null) usbStream.Write(buffer, offset, count);
        else activeSerial?.Write(buffer, offset, count);
    }

    public int ReadByte() => activeSerial?.ReadByte() ?? -1;

    public void SerialBreak()
    {
        // Hold TX low for longer than one frame but less than two, as the original did.
        int bitsToBreak = DataBits + 2 + 2;
        int timeToBreakMs = 1;
        try
        {
            int calculated = bitsToBreak / Math.Max(1, BaudRate);
            if (calculated > 0) timeToBreakMs = calculated;
        }
        catch (Exception) { }

        SerialBreakToggle(timeToBreakMs);
    }

    public void SerialBreakToggle(int timeToBreakMs)
    {
        if (activeSerial == null) return;
        try
        {
            activeSerial.BreakState = true;
            Thread.Sleep(timeToBreakMs);
            activeSerial.BreakState = false;
        }
        catch (Exception) { }
    }

    public void SetBreak(bool breakState)
    {
        try { if (activeSerial != null) activeSerial.BreakState = breakState; }
        catch (Exception) { }
    }

    public bool GetDtr()
    {
        try { return activeSerial?.DtrEnable ?? false; }
        catch (Exception) { return false; }
    }

    public void SetDtr(bool value)
    {
        try { if (activeSerial != null) activeSerial.DtrEnable = value; }
        catch (Exception) { }
    }

    public bool GetRts()
    {
        try { return activeSerial?.RtsEnable ?? false; }
        catch (Exception) { return false; }
    }

    public void SetRts(bool value)
    {
        try { if (activeSerial != null) activeSerial.RtsEnable = value; }
        catch (Exception) { }
    }

    /// <summary>Discards anything read but not yet handed to the terminal.</summary>
    public void ResetBuffer()
    {
        lock (pendingLock) pendingBytes.Clear();
        try { activeSerial?.DiscardInBuffer(); } catch (Exception) { }
        usbStream?.DiscardBuffered();
    }

    private static byte[] ConvertHexStringToBytes(string hexValues)
    {
        return Enumerable.Range(0, hexValues.Length)
            .Where(x => x % 2 == 0)
            .Select(x => Convert.ToByte(hexValues.Substring(x, 2), 16))
            .ToArray();
    }

    /// <summary>
    /// Strips whitespace and hyphens, left-pads to an even length, and confirms
    /// every character is a hex digit. Ported unchanged in behaviour.
    /// </summary>
    public static string ValidateStringIsHex(string inputHexString)
    {
        string output = Regex.Replace(inputHexString, @"(\s|-)", "");
        if (output.Length % 2 != 0) output = output.PadLeft(output.Length + 1, '0');
        return Regex.IsMatch(output, @"\A\b[0-9a-fA-F]+\b\Z") ? output : "DEADC0DE";
    }

    public void Dispose() => CleanUp();
}
