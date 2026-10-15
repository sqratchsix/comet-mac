using System.IO.Ports;

namespace Comet.Core;

/// <summary>
/// Wraps port discovery, configuration and open/close. Ported from
/// Comet1.SerialPortManager, with the WMI Win32_PnPEntity query replaced by
/// <see cref="MacSerialPortEnumerator"/>.
/// </summary>
public sealed class SerialPortManager : IDisposable
{
    private ActiveSerialPort? currentConnection;
    private string currentPortName = "";
    private int currentBaudRate = 9600;
    private Parity currentParity = Parity.None;
    private int currentDataBits = 8;
    private StopBits currentStopBits = StopBits.One;
    private int timeoutMs = 500;
    private int portReadTimeout = 20;
    private SerialDataType dataType = SerialDataType.Ascii;
    private bool portOpen;

    private IReadOnlyList<SerialPortInfo> knownPorts = Array.Empty<SerialPortInfo>();

    public event EventHandler<string>? DataReceived;
    public event EventHandler<string>? DataSent;

    public bool IsPortOpen => portOpen;
    public string CurrentPortName => currentPortName;
    public int CurrentBaudRate => currentBaudRate;
    public ActiveSerialPort? Connection => currentConnection;

    /// <summary>Reason the last OpenPort call failed, or empty.</summary>
    public string LastOpenError { get; private set; } = string.Empty;

    public SerialDataType DataType
    {
        get => dataType;
        set
        {
            dataType = value;
            if (currentConnection != null) currentConnection.DataType = value;
        }
    }

    public IReadOnlyList<SerialPortInfo> GetAvailablePorts()
    {
        knownPorts = MacSerialPortEnumerator.GetPorts();
        return knownPorts;
    }

    public string GetPortDescription(string deviceName)
    {
        if (string.IsNullOrEmpty(deviceName)) return "Unknown Port";
        var match = knownPorts.FirstOrDefault(p => p.DeviceName == deviceName);
        return match?.DisplayName ?? deviceName;
    }

    public void SetPortConfiguration(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits)
    {
        currentPortName = portName;
        currentBaudRate = baudRate;
        currentParity = parity;
        currentDataBits = dataBits;
        currentStopBits = stopBits;
    }

    public void SetTimeouts(int timeoutMilliseconds, int readTimeout)
    {
        timeoutMs = timeoutMilliseconds;
        portReadTimeout = readTimeout;
        currentConnection?.SetPortTimeout(timeoutMs, timeoutMs);
        currentConnection?.ChangeReadTimeout(readTimeout);
    }

    public bool OpenPortBasic()
    {
        try
        {
            DisposeConnection();
            currentConnection = new ActiveSerialPort();
            if (currentConnection.CreateBasicSerialPort(currentPortName, currentBaudRate, dataType))
            {
                currentConnection.DataReadyToRead += OnDataReadyToRead;
                return portOpen = true;
            }
            return portOpen = false;
        }
        catch (Exception)
        {
            return portOpen;
        }
    }

    public bool OpenPort()
    {
        LastOpenError = string.Empty;
        try
        {
            DisposeConnection();
            currentConnection = new ActiveSerialPort();
            if (currentConnection.OpenSerialPort(currentPortName, currentBaudRate, currentParity,
                    currentDataBits, currentStopBits, timeoutMs, dataType, portReadTimeout))
            {
                currentConnection.DataReadyToRead += OnDataReadyToRead;
                return portOpen = true;
            }
            LastOpenError = currentConnection.LastError;
            return portOpen = false;
        }
        catch (Exception)
        {
            return portOpen;
        }
    }

    public bool ClosePort()
    {
        try
        {
            currentConnection?.CloseSerialPort();
            portOpen = false;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public string SendData(string data, bool endline)
    {
        if (currentConnection == null) return string.Empty;
        string dataSent = currentConnection.SendData(data, endline);
        if (!string.IsNullOrEmpty(dataSent)) DataSent?.Invoke(this, dataSent);
        return dataSent;
    }

    public string GetConnectionInfo(int infoType) => currentConnection?.GetConnectionInfo(infoType) ?? string.Empty;

    public void SetRtsDtr(bool rtsEnabled, bool dtrEnabled)
    {
        currentConnection?.SetRts(rtsEnabled);
        currentConnection?.SetDtr(dtrEnabled);
    }

    private void OnDataReadyToRead(object? sender, EventArgs e)
    {
        if (currentConnection == null) return;
        string data = currentConnection.ReadData();
        if (!string.IsNullOrEmpty(data)) DataReceived?.Invoke(this, data);
    }

    private void DisposeConnection()
    {
        if (currentConnection == null) return;
        currentConnection.DataReadyToRead -= OnDataReadyToRead;
        currentConnection.Dispose();
        currentConnection = null;
    }

    public void Dispose() => DisposeConnection();
}
