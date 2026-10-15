using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
        portManager.DataReceived += (_, data) =>
        {
            CaptureReceived(data);
            AppendToTerminal(data, true);
        };
        // The footer's command count and the empty-pane message both track this.
        History.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HistorySummary));
            OnPropertyChanged(nameof(HistoryCount));
        };
        RefreshPorts();
        RefreshQuickLoadSlots();
    }

    /// <summary>Bound by the history pane to swap in its placeholder text.</summary>
    public int HistoryCount => History.Count;

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

    public string OpenCloseLabel => IsPortOpen ? "CLOSE PORT" : "OPEN PORT";

    public string OpenCloseMenuLabel => IsPortOpen ? "Close Port" : "Open Port";

    /// <summary>Port settings can only be changed while the port is closed.</summary>
    public bool CanEditPortSettings => !IsPortOpen;

    /// <summary>A raw USB interface has no baud rate, framing or control lines.</summary>
    public bool HasSerialLine => SelectedPort?.IsUsbBulk != true;

    /// <summary>Baud, data, stop and parity: closed port and a real serial line.</summary>
    public bool CanEditLineSettings => CanEditPortSettings && HasSerialLine;

    partial void OnIsPortOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(OpenCloseLabel));
        OnPropertyChanged(nameof(OpenCloseMenuLabel));
        OnPropertyChanged(nameof(CanEditPortSettings));
        OnPropertyChanged(nameof(CanEditLineSettings));
        RefreshSummaries();
    }

    partial void OnSelectedPortChanged(SerialPortInfo? value)
    {
        OnPropertyChanged(nameof(HasSerialLine));
        OnPropertyChanged(nameof(CanEditLineSettings));
        UpdateTitle();
        RefreshSummaries();
    }

    partial void OnSelectedBaudRateChanged(string value) => RefreshSummaries();
    partial void OnSelectedParityChanged(Parity value) => RefreshSummaries();
    partial void OnSelectedDataBitsChanged(int value) => RefreshSummaries();
    partial void OnSelectedStopBitsChanged(StopBits value) => RefreshSummaries();
    partial void OnCurrentHistoryFileChanged(string? value)
    {
        RefreshSummaries();
        RefreshQuickLoadSlots();
    }

    #endregion

    #region Terminal options

    [ObservableProperty]
    public partial bool IsHexMode { get; set; }

    partial void OnIsHexModeChanged(bool value)
    {
        portManager.DataType = value ? SerialDataType.Hex : SerialDataType.Ascii;
        OnPropertyChanged(nameof(IsAsciiMode));
        RefreshSummaries();
    }

    /// <summary>
    /// The other half of the ASCII / HEX pair. A negated binding on the radio
    /// button would not write back, so the inverse is a real property.
    /// </summary>
    public bool IsAsciiMode
    {
        get => !IsHexMode;
        set
        {
            if (value) IsHexMode = false;
        }
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

    /// <summary>The presets the Windows build offered in its terminal menu.</summary>
    public string[] TimestampFormats { get; } = { "HH:mm:ss:fff", "HH:mm:ss", "HH:mm", "yyyyMMddHHmmssffff" };

    public string TimestampMenuLabel => TimestampEnabled ? "Hide Timestamps" : "Show Timestamps";

    partial void OnTimestampEnabledChanged(bool value) => OnPropertyChanged(nameof(TimestampMenuLabel));

    [RelayCommand]
    private void ToggleTimestamps() => TimestampEnabled = !TimestampEnabled;

    /// <summary>Picking a format from the menu also turns timestamps on, as on Windows.</summary>
    [RelayCommand]
    private void SetTimestampFormat(string format)
    {
        TimestampFormat = format;
        TimestampEnabled = true;
    }

    // On by default, as in screen and most macOS terminals. USB CDC bridges such as
    // the XIAO MG24's SAMD11 and TinyUSB devices discard traffic until DTR is asserted.
    [ObservableProperty]
    public partial bool DtrEnabled { get; set; } = true;

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

    /// <summary>Latest status or error message, shown at the right of the footer.</summary>
    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "";

    [ObservableProperty]
    public partial bool StatusIsError { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = "COMET";

    /// <summary>Connection bar text: what is connected, or what would be.</summary>
    public string ConnectionSummary
    {
        get
        {
            if (SelectedPort == null) return "No serial device found";
            string where = string.IsNullOrEmpty(SelectedPort.Description)
                ? SelectedPort.ShortName
                : $"{SelectedPort.ShortName} → {SelectedPort.Description}";
            return IsPortOpen ? where : $"{where}  ·  not open";
        }
    }

    /// <summary>Footer: 115200 8N1.</summary>
    public string LineSettingSummary
    {
        get
        {
            if (!HasSerialLine) return "USB bulk";
            char parity = SelectedParity switch
            {
                Parity.None => 'N',
                Parity.Odd => 'O',
                Parity.Even => 'E',
                Parity.Mark => 'M',
                Parity.Space => 'S',
                _ => 'N'
            };
            string stop = SelectedStopBits switch
            {
                StopBits.One => "1",
                StopBits.OnePointFive => "1.5",
                StopBits.Two => "2",
                _ => "1"
            };
            return $"{SelectedBaudRate} {SelectedDataBits}{parity}{stop}";
        }
    }

    public string FormatSummary => IsHexMode ? "HEX" : "ASCII";

    public string HistorySummary => History.Count == 1 ? "1 command" : $"{History.Count} commands";

    public string? CurrentHistoryFileName =>
        string.IsNullOrEmpty(CurrentHistoryFile) ? null : Path.GetFileName(CurrentHistoryFile);

    private void RefreshSummaries()
    {
        OnPropertyChanged(nameof(ConnectionSummary));
        OnPropertyChanged(nameof(LineSettingSummary));
        OnPropertyChanged(nameof(FormatSummary));
        OnPropertyChanged(nameof(HistorySummary));
        OnPropertyChanged(nameof(CurrentHistoryFileName));
    }

    private void UpdateTitle()
    {
        if (IsPortOpen && SelectedPort?.IsUsbBulk == true)
        {
            Title = "COMET - " + SelectedPort.ShortName + "  (USB)";
            StatusText = SelectedPort.DeviceName;
        }
        else if (IsPortOpen)
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

    public bool OpenPort() => OpenPort(reportFailure: true);

    private bool OpenPort(bool reportFailure)
    {
        if (SelectedPort == null) return false;

        if (!int.TryParse(SelectedBaudRate, out int baud) || baud <= 0)
        {
            SetStatus($"Invalid baud rate: {SelectedBaudRate}", isError: true);
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
            SetStatus($"Opened {SelectedPort.ShortName}");
            portManager.SetRtsDtr(RtsEnabled, DtrEnabled);
            UpdateLineEnding();
            UpdateTitle();
            InputFocusRequested?.Invoke(this, EventArgs.Empty);
            return true;
        }

        string reason = string.IsNullOrEmpty(portManager.LastOpenError) || !SelectedPort.IsUsbBulk
            ? "Another program may be holding the port, or it may need permission."
            : portManager.LastOpenError;
        if (reportFailure) SetStatus($"Could not open {SelectedPort.ShortName}. {reason}", isError: true);
        UpdateTitle();
        return false;
    }

    /// <summary>
    /// Tries each USB serial port in turn until one opens, matching
    /// openPortActionFirstTime in the Windows build. A new window uses this to
    /// land on the next free port: ports are opened exclusively, so the ones other
    /// windows hold fail quietly and are skipped.
    /// </summary>
    public bool OpenFirstAvailablePort()
    {
        var first = SelectedPort;

        // Raw USB interfaces are left alone: claiming a modem's diagnostic port
        // unasked is not a harmless default the way opening a USB UART is.
        foreach (var port in Ports.Where(p => p.IsUsb && !p.IsUsbBulk).ToList())
        {
            SelectedPort = port;
            if (OpenPort(reportFailure: false)) return true;
        }

        SelectedPort = first;
        SetStatus("No free USB serial port. Pick a device to open one.", isError: true);
        return false;
    }

    public bool ClosePort()
    {
        bool closed = portManager.ClosePort();
        if (IsPortOpen && SelectedPort != null) SetStatus($"Closed {SelectedPort.ShortName}");
        IsPortOpen = false;
        UpdateTitle();
        return closed;
    }

    private SerialPortInfo? deviceBeforeSelection;
    private bool wasOpenBeforeSelection;

    /// <summary>
    /// The device list was opened. Close the port so the choice that follows
    /// starts clean, and the port is never left open on a device no longer shown.
    /// </summary>
    public void BeginDeviceSelection()
    {
        deviceBeforeSelection = SelectedPort;
        wasOpenBeforeSelection = IsPortOpen;
        if (IsPortOpen) ClosePort();
    }

    /// <summary>
    /// The device list was dismissed. Picking a device opens it with the current
    /// settings; dismissing without a change restores whatever state it was in.
    /// </summary>
    public void EndDeviceSelection()
    {
        bool changed = !ReferenceEquals(SelectedPort, deviceBeforeSelection);
        if (!IsPortOpen && SelectedPort != null && (changed || wasOpenBeforeSelection)) OpenPort();
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
            SetStatus("Port is not open.", isError: true);
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
        if (entry.EntryType == CommandEntryType.ScriptRunner)
        {
            RunEntry(entry);
            return;
        }
        if (entry.EntryType != CommandEntryType.SerialCommand) return;

        if (!IsPortOpen)
        {
            SetStatus("Port is not open.", isError: true);
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

        // Timestamps are applied per line by the view, which knows where lines start.
        formatter.ByteSpacesEnabled = ByteSpaces;
        formatter.TimestampEnabled = false;
        text = formatter.FormatReceivedData(text, IsHexMode ? SerialDataType.Hex : SerialDataType.Ascii);

        // Sent commands always land on their own line. Received data does too
        // unless the user has turned that off, so a device that streams without
        // line endings can be read as a continuous flow.
        bool needsNewline = !text.EndsWith("\n", StringComparison.Ordinal);
        if (needsNewline && (!isReceived || AddNewLineToTerminal)) text += Environment.NewLine;

        TerminalTextAppended?.Invoke(this, new TerminalTextEventArgs(text,
            isReceived ? TerminalTextKind.Received : TerminalTextKind.Sent, DateTime.Now));
    }

    /// <summary>
    /// Reports what the app did in the footer. The terminal is reserved for what
    /// actually went over the wire, so nothing here is ever written to it.
    /// </summary>
    private void SetStatus(string message, bool isError = false)
    {
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => SetStatus(message, isError));
            return;
        }
        StatusIsError = isError;
        StatusMessage = message;
    }

    [RelayCommand]
    private void ClearTerminal()
    {
        TerminalCleared?.Invoke(this, EventArgs.Empty);
        InputFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    #endregion

    #region COMET folder

    /// <summary>
    /// The folder COMET runs from, where the Windows build kept its command files.
    /// For the app bundle that is the folder holding COMET.app, since the binary
    /// itself is buried in COMET.app/Contents/MacOS. Run from source, it is the
    /// build output folder.
    /// </summary>
    public static string AppFolder
    {
        get
        {
            string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            const string bundleTail = "/Contents/MacOS";
            if (baseDir.EndsWith(bundleTail, StringComparison.Ordinal))
            {
                string bundle = baseDir.Substring(0, baseDir.Length - bundleTail.Length);
                if (bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                    return Path.GetDirectoryName(bundle) ?? baseDir;
            }
            return baseDir;
        }
    }

    [RelayCommand]
    public void OpenContainingFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo("/usr/bin/open", new[] { AppFolder }) { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open {AppFolder}: {ex.Message}", isError: true);
        }
    }

    /// <summary>The A, B, C, D buttons in the header.</summary>
    public QuickLoadSlot[] QuickLoadSlots { get; } =
        { new("A"), new("B"), new("C"), new("D") };

    /// <summary>Called when the window comes forward, so files added in Finder light their button.</summary>
    public void RefreshQuickLoadSlots()
    {
        foreach (var slot in QuickLoadSlots) slot.Refresh(CurrentHistoryFile);
    }

    [RelayCommand]
    private void LoadQuickSlot(QuickLoadSlot slot)
    {
        slot.Refresh(CurrentHistoryFile);
        if (!slot.Exists)
        {
            SetStatus($"{slot.FileName} not found. Put it in {AppFolder} to use button {slot.Letter}.", isError: true);
            return;
        }
        LoadHistoryFile(slot.FilePath, replaceExisting: true);
    }

    /// <summary>Command files offered under Load Commands.</summary>
    public IReadOnlyList<string> FindCommandFiles() => CommandFileFinder.Find(AppFolder);

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
            SetStatus($"Loaded {entries.Count} command(s) from {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not load {path}: {ex.Message}", isError: true);
        }
    }

    public void SaveHistoryFile(string path)
    {
        try
        {
            HistoryFile.Save(path, History);
            CurrentHistoryFile = path;
            SetStatus($"Saved {History.Count} command(s) to {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not save {path}: {ex.Message}", isError: true);
        }
    }

    #endregion

    /// <summary>
    /// Fills the window with representative content for the headless screenshot
    /// tool, so the design can be reviewed against real text rather than an
    /// empty shell. Not called by the app itself.
    /// </summary>
    public void LoadSampleContent()
    {
        foreach (var entry in new[]
                 {
                     new CommandEntry("AT", "AT"),
                     new CommandEntry("AT+COPS?", "Current carrier", 1),
                     new CommandEntry("AT+CSQ", "Signal quality", 1),
                     new CommandEntry("AT+CFUN=1,1", "Restart module", 1),
                     new CommandEntry("qdver", "Firmware version"),
                     new CommandEntry("qdeeinfo", "Get all info"),
                     new CommandEntry("Update Firmware", "Update Firmware", 2, CommandEntryType.ScriptRunner),
                     new CommandEntry("sdboot", "sdboot"),
                     new CommandEntry("sbl", "Stay in bootloader"),
                 })
        {
            History.Add(entry);
        }

        SetStatus("Loaded 9 command(s) from raptor.ButtonA.txt");
        TerminalTextAppended?.Invoke(this, new TerminalTextEventArgs(
            "AT+CSQ" + Environment.NewLine, TerminalTextKind.Sent));
        TerminalTextAppended?.Invoke(this, new TerminalTextEventArgs(
            "+CSQ: 21,99" + Environment.NewLine + "OK" + Environment.NewLine, TerminalTextKind.Received));
        TerminalTextAppended?.Invoke(this, new TerminalTextEventArgs(
            "qdver" + Environment.NewLine, TerminalTextKind.Sent));
        TerminalTextAppended?.Invoke(this, new TerminalTextEventArgs(
            "RAPTOR v2.14.3  build 8821" + Environment.NewLine, TerminalTextKind.Received));
    }

    public void Dispose()
    {
        scriptCancellation?.Cancel();
        portManager.Dispose();
    }
}

/// <summary>
/// The terminal only ever shows wire traffic. Status and errors go to the
/// footer through StatusMessage, so there is deliberately no third kind here.
/// </summary>
public enum TerminalTextKind
{
    Received,
    Sent
}

public sealed class TerminalTextEventArgs : EventArgs
{
    public TerminalTextEventArgs(string text, TerminalTextKind kind, DateTime? time = null)
    {
        Text = text;
        Kind = kind;
        Time = time ?? DateTime.Now;
    }

    public string Text { get; }
    public TerminalTextKind Kind { get; }

    /// <summary>
    /// When the text arrived, taken here rather than when the UI thread draws it.
    /// Always recorded; the timestamp toggle only decides whether it is shown.
    /// </summary>
    public DateTime Time { get; }
}
