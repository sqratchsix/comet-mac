using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Comet.Core;

namespace Comet.Desktop.ViewModels;

/// <summary>
/// Everything the main window does, minus the drawing. This is the macOS
/// counterpart of SerialWindow.cs from the Windows build: the same behaviour,
/// with the WinForms control manipulation replaced by bindable state.
/// </summary>
public partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly SerialPortManager portManager = new();
    private readonly DataFormatter formatter = new();

    // Typed command recall, driven by the up and down arrows in the input box.
    private readonly List<string> lastCommandList = new();
    private int lastCommandIndex;

    /// <summary>Raised when text should be written to the terminal view.</summary>
    public event EventHandler<TerminalTextEventArgs>? TerminalTextAppended;

    /// <summary>Raised when the terminal view should be emptied.</summary>
    public event EventHandler? TerminalCleared;

    /// <summary>Raised when focus should return to the command input box.</summary>
    public event EventHandler? InputFocusRequested;

    public MainViewModel()
    {
        portManager.DataReceived += (_, data) => AppendToTerminal(data, true);
        RefreshPorts();
    }

    #region Port configuration

    public ObservableCollection<SerialPortInfo> Ports { get; } = new();

    [ObservableProperty]
    public partial SerialPortInfo? SelectedPort { get; set; }

    /// <summary>
    /// Editable so unusual rates (a 3 Mbaud debug UART, for instance) can be typed
    /// in. The Windows build's list is preserved, with the faster rates modern USB
    /// bridges support added on top.
    /// </summary>
    public ObservableCollection<string> BaudRates { get; } = new(new[]
    {
        "921600", "460800", "230400", "115200", "57600", "38400",
        "19200", "9600", "4800", "2400", "1200"
    });

    [ObservableProperty]
    public partial string SelectedBaudRate { get; set; } = "115200";

    public Parity[] ParityOptions { get; } = Enum.GetValues<Parity>();

    [ObservableProperty]
    public partial Parity SelectedParity { get; set; } = Parity.None;

    public int[] DataBitsOptions { get; } = { 5, 6, 7, 8 };

    [ObservableProperty]
    public partial int SelectedDataBits { get; set; } = 8;

    public StopBits[] StopBitsOptions { get; } = { StopBits.One, StopBits.OnePointFive, StopBits.Two };

    [ObservableProperty]
    public partial StopBits SelectedStopBits { get; set; } = StopBits.One;

    /// <summary>Write timeout in milliseconds.</summary>
    [ObservableProperty]
    public partial string TimeoutText { get; set; } = "500";

    /// <summary>
    /// How long the inbound line must be quiet before the buffered response is
    /// written to the terminal. Keeps a multi-packet reply on one line.
    /// </summary>
    [ObservableProperty]
    public partial string ReadTimeoutText { get; set; } = "20";

    [ObservableProperty]
    public partial bool IsPortOpen { get; set; }

    public string OpenCloseLabel => IsPortOpen ? "Close Port" : "Open Port";

    /// <summary>Port settings can only be changed while the port is closed.</summary>
    public bool CanEditPortSettings => !IsPortOpen;

    partial void OnIsPortOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(OpenCloseLabel));
        OnPropertyChanged(nameof(CanEditPortSettings));
    }

    partial void OnSelectedPortChanged(SerialPortInfo? value)
    {
        UpdateTitle();
    }

    #endregion

    #region Terminal options

    [ObservableProperty]
    public partial bool IsHexMode { get; set; }

    partial void OnIsHexModeChanged(bool value)
    {
        portManager.DataType = value ? SerialDataType.Hex : SerialDataType.Ascii;
    }

    /// <summary>Append CR to each sent command.</summary>
    [ObservableProperty]
    public partial bool WriteCr { get; set; } = true;

    /// <summary>Append LF to each sent command.</summary>
    [ObservableProperty]
    public partial bool WriteNl { get; set; } = true;

    partial void OnWriteCrChanged(bool value) => UpdateLineEnding();
    partial void OnWriteNlChanged(bool value) => UpdateLineEnding();

    /// <summary>Break received messages onto their own lines in the terminal.</summary>
    [ObservableProperty]
    public partial bool AddNewLineToTerminal { get; set; } = true;

    /// <summary>Insert spaces between bytes in HEX mode.</summary>
    [ObservableProperty]
    public partial bool ByteSpaces { get; set; } = true;

    [ObservableProperty]
    public partial bool TimestampEnabled { get; set; }

    [ObservableProperty]
    public partial string TimestampFormat { get; set; } = "HH:mm:ss:fff";

    [ObservableProperty]
    public partial bool DtrEnabled { get; set; }

    [ObservableProperty]
    public partial bool RtsEnabled { get; set; }

    partial void OnDtrEnabledChanged(bool value) => portManager.Connection?.SetDtr(value);
    partial void OnRtsEnabledChanged(bool value) => portManager.Connection?.SetRts(value);

    #endregion

    #region Command input and history

    [ObservableProperty]
    public partial string InputText { get; set; } = "";

    /// <summary>Adds each sent command to the history pane, as the check box beside the input did.</summary>
    [ObservableProperty]
    public partial bool RecordHistory { get; set; } = true;

    /// <summary>Show the command on each button rather than its description.</summary>
    [ObservableProperty]
    public partial bool ShowCommandOnButtons { get; set; } = true;

    public ObservableCollection<CommandEntry> History { get; } = new();

    partial void OnShowCommandOnButtonsChanged(bool value)
    {
        foreach (var entry in History) entry.ShowCommand = value;
    }

    [ObservableProperty]
    public partial string? CurrentHistoryFile { get; set; }

    #endregion

    #region Status

    [ObservableProperty]
    public partial string StatusText { get; set; } = "No Connection";

    [ObservableProperty]
    public partial string Title { get; set; } = "COMET";

    private void UpdateTitle()
    {
        if (IsPortOpen)
        {
            Title = "COMET - " + portManager.GetConnectionInfo(0);
            StatusText = portManager.GetConnectionInfo(1);
        }
        else if (SelectedPort != null)
        {
            Title = "COMET - " + SelectedPort.DisplayName;
            StatusText = "No Connection";
        }
        else
        {
            Title = "COMET";
            StatusText = "No Connection";
        }
    }

    #endregion

    #region Port commands

    [RelayCommand]
    public void RefreshPorts()
    {
        string? previous = SelectedPort?.DeviceName;
        Ports.Clear();
        foreach (var port in portManager.GetAvailablePorts()) Ports.Add(port);

        SelectedPort = Ports.FirstOrDefault(p => p.DeviceName == previous) ?? Ports.FirstOrDefault();
    }

    [RelayCommand]
    public void TogglePort()
    {
        if (IsPortOpen) ClosePort();
        else OpenPort();
    }

    public bool OpenPort()
    {
        if (SelectedPort == null) return false;

        if (!int.TryParse(SelectedBaudRate, out int baud) || baud <= 0)
        {
            AppendSystemMessage($"Invalid baud rate: {SelectedBaudRate}");
            return false;
        }

        int timeout = int.TryParse(TimeoutText, out int t) ? t : 500;
        int readTimeout = int.TryParse(ReadTimeoutText, out int rt) ? rt : 20;

        portManager.SetPortConfiguration(SelectedPort.DeviceName, baud, SelectedParity, SelectedDataBits, SelectedStopBits);
        portManager.SetTimeouts(timeout, readTimeout);
        portManager.DataType = IsHexMode ? SerialDataType.Hex : SerialDataType.Ascii;

        if (portManager.OpenPort())
        {
            IsPortOpen = true;
            portManager.SetRtsDtr(RtsEnabled, DtrEnabled);
            UpdateLineEnding();
            UpdateTitle();
            InputFocusRequested?.Invoke(this, EventArgs.Empty);
            return true;
        }

        AppendSystemMessage($"Could not open {SelectedPort.DeviceName}. " +
                            "Another program may be holding the port, or it may need permission.");
        UpdateTitle();
        return false;
    }

    /// <summary>
    /// Tries each port in turn until one opens, matching openPortActionFirstTime
    /// in the Windows build. Used once at startup.
    /// </summary>
    public bool OpenFirstAvailablePort()
    {
        foreach (var port in Ports.Where(p => p.IsUsb))
        {
            SelectedPort = port;
            if (OpenPort()) return true;
        }
        return false;
    }

    public bool ClosePort()
    {
        bool closed = portManager.ClosePort();
        IsPortOpen = false;
        UpdateTitle();
        return closed;
    }

    [RelayCommand]
    private void SendBreak() => portManager.Connection?.SerialBreak();

    [RelayCommand]
    private void ResetBuffer() => portManager.Connection?.ResetBuffer();

    private void UpdateLineEnding()
    {
        var connection = portManager.Connection;
        if (connection == null) return;

        string ending = "";
        if (WriteCr) ending += "\r";
        if (WriteNl) ending += "\n";
        connection.LineEnding = ending;
    }

    #endregion

    #region Sending

    [RelayCommand]
    public void Send()
    {
        string data = InputText;
        if (string.IsNullOrEmpty(data)) return;

        if (!IsPortOpen)
        {
            AppendSystemMessage("Port is not open.");
            return;
        }

        string dataSent = portManager.SendData(data, WriteCr || WriteNl);
        if (string.IsNullOrEmpty(dataSent)) return;

        AppendToTerminal(dataSent, false);

        if (RecordHistory) AddHistoryEntry(dataSent);
        AddLastCommand(dataSent);

        InputText = "";
        InputFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sends a command from a history button without touching the input box.</summary>
    [RelayCommand]
    public void SendEntry(CommandEntry entry)
    {
        if (entry.EntryType != CommandEntryType.SerialCommand) return;

        if (!IsPortOpen)
        {
            AppendSystemMessage("Port is not open.");
            return;
        }

        string dataSent = portManager.SendData(entry.Command, WriteCr || WriteNl);
        if (!string.IsNullOrEmpty(dataSent)) AppendToTerminal(dataSent, false);
    }

    private void AddLastCommand(string command)
    {
        if (string.IsNullOrEmpty(command)) return;
        lastCommandList.Add(command);
        lastCommandIndex = lastCommandList.Count;
    }

    /// <summary>Up arrow in the input box: step back through typed commands.</summary>
    public void RecallPreviousCommand()
    {
        if (lastCommandIndex > 0)
        {
            lastCommandIndex--;
            InputText = lastCommandList[lastCommandIndex];
        }
    }

    /// <summary>Down arrow in the input box: step forward, ending on an empty box.</summary>
    public void RecallNextCommand()
    {
        if (lastCommandIndex <= lastCommandList.Count - 2)
        {
            lastCommandIndex++;
            InputText = lastCommandList[lastCommandIndex];
        }
        else
        {
            lastCommandIndex = lastCommandList.Count;
            InputText = "";
        }
    }

    #endregion

    #region Terminal

    private void AppendToTerminal(string text, bool isReceived)
    {
        if (string.IsNullOrEmpty(text)) return;

        formatter.ByteSpacesEnabled = ByteSpaces;
        formatter.TimestampEnabled = TimestampEnabled;
        formatter.TimestampFormat = TimestampFormat;
        text = formatter.FormatReceivedData(text, IsHexMode ? SerialDataType.Hex : SerialDataType.Ascii);

        // Sent commands always land on their own line. Received data does too
        // unless the user has turned that off, so a device that streams without
        // line endings can be read as a continuous flow.
        bool needsNewline = !text.EndsWith("\n", StringComparison.Ordinal);
        if (needsNewline && (!isReceived || AddNewLineToTerminal)) text += Environment.NewLine;

        TerminalTextAppended?.Invoke(this, new TerminalTextEventArgs(text,
            isReceived ? TerminalTextKind.Received : TerminalTextKind.Sent));
    }

    private void AppendSystemMessage(string message)
    {
        TerminalTextAppended?.Invoke(this, new TerminalTextEventArgs(
            message + Environment.NewLine, TerminalTextKind.System));
    }

    [RelayCommand]
    private void ClearTerminal()
    {
        TerminalCleared?.Invoke(this, EventArgs.Empty);
        InputFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    #endregion

    #region History pane

    private void AddHistoryEntry(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return;

        // Repeating a command moves the existing button rather than stacking duplicates.
        var existing = History.FirstOrDefault(e =>
            e.EntryType == CommandEntryType.SerialCommand &&
            string.Equals(e.Command, command, StringComparison.Ordinal));
        if (existing != null)
        {
            History.Move(History.IndexOf(existing), History.Count - 1);
            return;
        }

        History.Add(new CommandEntry(command, command) { ShowCommand = ShowCommandOnButtons });
    }

    [RelayCommand]
    private void ClearHistory()
    {
        History.Clear();
        CurrentHistoryFile = null;
    }

    [RelayCommand]
    public void RemoveEntry(CommandEntry entry) => History.Remove(entry);

    /// <summary>Copies a history button's command into the input box for editing.</summary>
    [RelayCommand]
    public void EditEntry(CommandEntry entry)
    {
        InputText = entry.Command;
        InputFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ToggleButtonLabels()
    {
        ShowCommandOnButtons = !ShowCommandOnButtons;
    }

    /// <summary>
    /// Loads a history or command file. Entries keep the order they appear in the
    /// file, so a hand written command list reads top to bottom as authored.
    /// </summary>
    public void LoadHistoryFile(string path, bool replaceExisting = true)
    {
        try
        {
            var entries = HistoryFile.Load(path);
            if (replaceExisting) History.Clear();
            foreach (var entry in entries)
            {
                entry.ShowCommand = ShowCommandOnButtons;
                History.Add(entry);
            }
            CurrentHistoryFile = path;
            AppendSystemMessage($"Loaded {entries.Count} command(s) from {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            AppendSystemMessage($"Could not load {path}: {ex.Message}");
        }
    }

    public void SaveHistoryFile(string path)
    {
        try
        {
            HistoryFile.Save(path, History);
            CurrentHistoryFile = path;
            AppendSystemMessage($"Saved {History.Count} command(s) to {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            AppendSystemMessage($"Could not save {path}: {ex.Message}");
        }
    }

    #endregion

    public void Dispose()
    {
        portManager.Dispose();
    }
}

public enum TerminalTextKind
{
    Received,
    Sent,
    System
}

public sealed class TerminalTextEventArgs : EventArgs
{
    public TerminalTextEventArgs(string text, TerminalTextKind kind)
    {
        Text = text;
        Kind = kind;
    }

    public string Text { get; }
    public TerminalTextKind Kind { get; }
}
