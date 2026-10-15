using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Comet.Core;

namespace Comet.Desktop.ViewModels;

/// <summary>Dialogs a running script needs, supplied by the window.</summary>
public interface IScriptUi
{
    Task<string?> AskAsync(string prompt);
    void ShowResult(bool passed, string detail);
}

/// <summary>
/// Script buttons: running, stopping, and saving edits back to the history.
/// The runner itself is Comet.Core.ScriptRunner; this side gives it the port,
/// the settings and the dialogs, and keeps status in the footer.
/// </summary>
public partial class MainViewModel
{
    public const string ResponseLogFileName = "COMETResponseLog.txt";

    private CancellationTokenSource? scriptCancellation;

    // Received text kept while a response_* or log step waits for its reply.
    private readonly object captureLock = new();
    private readonly StringBuilder capture = new();
    private bool capturing;

    /// <summary>Set by the window so scripts can ask questions and show PASSED / FAILED.</summary>
    public IScriptUi? ScriptUi { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopScriptCommand))]
    public partial bool IsScriptRunning { get; set; }

    /// <summary>Footer text while a script runs: name, step and pass.</summary>
    [ObservableProperty]
    public partial string ScriptProgressText { get; set; } = "";

    public string ResponseLogPath => Path.Combine(AppFolder, ResponseLogFileName);

    private void CaptureReceived(string data)
    {
        lock (captureLock)
        {
            if (capturing) capture.Append(data);
        }
    }

    public void RunEntry(CommandEntry entry)
    {
        if (entry.EntryType == CommandEntryType.ScriptRunner)
            RunScript(Script.FromLines(entry.ScriptLines), entry.Command);
        else
            SendEntry(entry);
    }

    public async void RunScript(Script script, string name)
    {
        if (IsScriptRunning)
        {
            SetStatus("A script is already running. Stop it first.", isError: true);
            return;
        }
        if (!IsPortOpen)
        {
            SetStatus("Port is not open.", isError: true);
            return;
        }

        scriptCancellation = new CancellationTokenSource();
        IsScriptRunning = true;
        ScriptProgressText = $"▶ {name}";
        var progress = new Progress<ScriptProgress>(p =>
        {
            if (!IsScriptRunning) return;
            string loop = p.LoopCount > 1 ? $" · pass {p.Loop}/{p.LoopCount}" : "";
            ScriptProgressText = $"▶ {name} · step {p.Step}/{p.StepCount}{loop}";
        });

        var runner = new ScriptRunner(new Host(this));
        var token = scriptCancellation.Token;
        try
        {
            await Task.Run(() => runner.RunAsync(script, token, progress), token);
            SetStatus($"Script {name} finished");
        }
        catch (OperationCanceledException)
        {
            SetStatus($"Script {name} stopped");
        }
        catch (Exception ex)
        {
            SetStatus($"Script {name} stopped: {ex.Message}", isError: true);
        }
        finally
        {
            lock (captureLock) capturing = false;
            IsScriptRunning = false;
            ScriptProgressText = "";
            scriptCancellation.Dispose();
            scriptCancellation = null;
        }
    }

    [RelayCommand]
    private void OpenResponseLog()
    {
        if (!File.Exists(ResponseLogPath))
        {
            SetStatus($"No response log yet. Scripts write it to {ResponseLogPath}.");
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/usr/bin/open", new[] { ResponseLogPath })
            {
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open {ResponseLogFileName}: {ex.Message}", isError: true);
        }
    }

    [RelayCommand(CanExecute = nameof(IsScriptRunning))]
    public void StopScript() => scriptCancellation?.Cancel();

    /// <summary>The text Copy Script puts on the clipboard, as it would be saved.</summary>
    public static string ScriptText(CommandEntry entry) =>
        string.Join(Environment.NewLine, entry.ScriptLines ?? Array.Empty<string>());

    /// <summary>
    /// Writes an edited script back to its button, or adds a new button. Like the
    /// Windows build, this changes the buttons in the window; Save Commands As
    /// writes them to a file.
    /// </summary>
    public void SaveScript(CommandEntry? entry, ScriptEditorViewModel editor)
    {
        var script = editor.BuildScript();
        if (script == null) return;

        string name = editor.Name.Trim();
        string description = string.IsNullOrWhiteSpace(editor.Description) ? name : editor.Description.Trim();

        if (entry == null)
        {
            History.Add(new CommandEntry(name, description, 2, CommandEntryType.ScriptRunner)
            {
                ScriptLines = script.ToLines(),
                ShowCommand = ShowCommandOnButtons
            });
            SetStatus($"Added script {name}. Use Save Commands As to keep it in a file.");
        }
        else
        {
            entry.Command = name;
            entry.Description = description;
            entry.ScriptLines = script.ToLines();
            SetStatus($"Updated script {name}. Use Save Commands As to keep it in a file.");
        }
    }

    /// <summary>
    /// The runner's view of this window. Called from the script's task, so
    /// anything that touches bound properties goes through the UI thread.
    /// </summary>
    private sealed class Host : IScriptHost
    {
        private readonly MainViewModel vm;

        public Host(MainViewModel vm)
        {
            this.vm = vm;
        }

        private static void OnUi(Action action) => Dispatcher.UIThread.Invoke(action);

        public void Send(string command)
        {
            if (!vm.IsPortOpen) throw new InvalidOperationException("the port closed");
            string sent = vm.portManager.SendData(command, vm.WriteCr || vm.WriteNl);
            if (sent == "ERROR") throw new InvalidOperationException($"could not write {command}");
            if (!string.IsNullOrEmpty(sent)) vm.AppendToTerminal(sent, false);
        }

        public void Status(string message, bool isError = false) => OnUi(() => vm.SetStatus(message, isError));

        public void SerialBreak() => vm.portManager.Connection?.SerialBreak();

        public void SetBreak(bool on) => vm.portManager.Connection?.SetBreak(on);

        public void SetRts(bool on) => OnUi(() => vm.RtsEnabled = on);

        public void SetDtr(bool on) => OnUi(() => vm.DtrEnabled = on);

        public void SetWriteNewLine(bool on) => OnUi(() =>
        {
            vm.WriteCr = on;
            vm.WriteNl = on;
        });

        public void SetReadNewLine(bool on) => OnUi(() => vm.AddNewLineToTerminal = on);

        /// <summary>**settings baud [ASCII/HEX] [DTR] [idle ms] [read CR/NL], reopening the port.</summary>
        public Task ApplySettingsAsync(IReadOnlyList<string> args)
        {
            OnUi(() =>
            {
                vm.ClosePort();
                vm.SelectedBaudRate = args[0].Trim();
                if (args.Count > 1) vm.IsHexMode = args[1].Trim().Equals("HEX", StringComparison.OrdinalIgnoreCase);
                if (args.Count > 2) vm.DtrEnabled = ScriptCommandCatalog.ParseSwitch(args[2]);
                if (args.Count > 3) vm.ReadTimeoutText = args[3].Trim();
                if (args.Count > 4) vm.AddNewLineToTerminal = ScriptCommandCatalog.ParseSwitch(args[4]);
                if (!vm.OpenPort()) throw new InvalidOperationException($"could not reopen the port at {args[0]} baud");
            });
            return Task.CompletedTask;
        }

        public void StartCapture()
        {
            lock (vm.captureLock)
            {
                vm.capture.Clear();
                vm.capturing = true;
            }
        }

        public string ReadCapture()
        {
            lock (vm.captureLock) return vm.capture.ToString();
        }

        public void StopCapture()
        {
            lock (vm.captureLock)
            {
                vm.capturing = false;
                vm.capture.Clear();
            }
        }

        public Task<string?> AskAsync(string prompt)
        {
            var ui = vm.ScriptUi;
            if (ui == null) return Task.FromResult<string?>(null);
            return Dispatcher.UIThread.InvokeAsync(() => ui.AskAsync(prompt));
        }

        public void ShowResult(bool passed, string detail) => OnUi(() => vm.ScriptUi?.ShowResult(passed, detail));

        public void AppendLog(string line)
        {
            try
            {
                File.AppendAllText(vm.ResponseLogPath, line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Status($"Could not write {ResponseLogFileName}: {ex.Message}", isError: true);
            }
        }
    }
}
