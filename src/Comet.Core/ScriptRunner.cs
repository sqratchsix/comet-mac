using System.Diagnostics;

namespace Comet.Core;

/// <summary>
/// What a running script needs from the window it runs in. The runner calls
/// these from a background task; implementations marshal to the UI themselves.
/// </summary>
public interface IScriptHost
{
    /// <summary>Send a command with the current write ending, echoing it in the terminal.</summary>
    void Send(string command);

    /// <summary>Report progress or a problem in the status bar, never the terminal.</summary>
    void Status(string message, bool isError = false);

    void SerialBreak();
    void SetBreak(bool on);
    void SetRts(bool on);
    void SetDtr(bool on);
    void SetWriteNewLine(bool on);
    void SetReadNewLine(bool on);

    /// <summary>**settings: baud, then optionally ASCII/HEX, DTR, idle ms, read CR/NL.</summary>
    Task ApplySettingsAsync(IReadOnlyList<string> args);

    /// <summary>Start keeping a copy of received text for the response functions.</summary>
    void StartCapture();

    /// <summary>Everything received since <see cref="StartCapture"/>.</summary>
    string ReadCapture();

    void StopCapture();

    /// <summary>Ask the user for a value. Null means they cancelled, which stops the script.</summary>
    Task<string?> AskAsync(string prompt);

    /// <summary>The PASSED / FAILED window. It does not wait for the user.</summary>
    void ShowResult(bool passed, string detail);

    /// <summary>Append one line to the response log.</summary>
    void AppendLog(string line);
}

public sealed record ScriptProgress(int Step, int StepCount, int Loop, int LoopCount);

/// <summary>
/// Runs a script button, ported from SerialWindow.runScript and
/// scriptHandleFunction in the Windows build. Every step is followed by the
/// current delay, functions included, and the whole script repeats LoopCount
/// times with LoopDelayMs between passes.
///
/// The Windows build ran this on the UI thread and pumped messages with
/// Application.DoEvents; this runs on a task and can be cancelled between and
/// during steps.
/// </summary>
public sealed class ScriptRunner
{
    private const int PollMs = 20;

    private readonly IScriptHost host;
    private int delayMs;

    public ScriptRunner(IScriptHost host)
    {
        this.host = host;
    }

    /// <summary>Time source for waits, replaceable so tests need not sleep.</summary>
    public Func<int, CancellationToken, Task> Delay { get; set; } = (ms, ct) => ms > 0 ? Task.Delay(ms, ct) : Task.CompletedTask;

    public async Task RunAsync(Script script, CancellationToken token, IProgress<ScriptProgress>? progress = null)
    {
        delayMs = script.DelayMs;
        int loops = Math.Max(1, script.LoopCount);

        for (int loop = 0; loop < loops; loop++)
        {
            if (loop > 0) await Delay(script.LoopDelayMs, token);

            for (int i = 0; i < script.Steps.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report(new ScriptProgress(i + 1, script.Steps.Count, loop + 1, loops));

                var step = script.Steps[i];
                if (step.IsFunction) await RunFunctionAsync(step, token);
                else host.Send(step.Command);

                await Delay(delayMs, token);
            }
        }
    }

    private async Task RunFunctionAsync(ScriptStep step, CancellationToken token)
    {
        var args = step.Args;
        string Arg(int i) => i < args.Count ? args[i] : "";
        int IntArg(int i, int fallback) => int.TryParse(Arg(i), out int v) ? v : fallback;

        switch (step.Command.ToLowerInvariant())
        {
            case "delay":
                if (int.TryParse(Arg(0), out int newDelay)) delayMs = newDelay;
                break;

            case "sleep":
                await Delay(IntArg(0, 1000), token);
                break;

            case "time":
                host.Status(DateTime.Now.ToString());
                break;

            case "serialbreak":
                host.SerialBreak();
                break;

            case "sbreak":
                host.SetBreak(ScriptCommandCatalog.ParseSwitch(Arg(0)));
                break;

            case "rts":
                host.SetRts(ScriptCommandCatalog.ParseSwitch(Arg(0)));
                break;

            case "dtr":
                host.SetDtr(ScriptCommandCatalog.ParseSwitch(Arg(0)));
                break;

            case "unicode":
                try
                {
                    int code = Convert.ToInt32(Arg(0), 16);
                    host.Send(char.ConvertFromUtf32(code));
                }
                catch (Exception)
                {
                    host.Status($"**unicode: \"{Arg(0)}\" is not a hex code point", isError: true);
                }
                break;

            case "settings":
                if (args.Count > 0) await host.ApplySettingsAsync(args);
                break;

            case "set_writenewline":
                host.SetWriteNewLine(ScriptCommandCatalog.ParseSwitch(Arg(0)));
                break;

            case "set_readnewline":
                host.SetReadNewLine(ScriptCommandCatalog.ParseSwitch(Arg(0)));
                break;

            case "response_str":
            {
                string expected = Arg(2);
                bool found = await SendAndWaitAsync(IntArg(0, 3000), Arg(1), token,
                    buffer => ResponseAnalyzer.Contains(buffer, expected));
                host.ShowResult(found, found
                    ? $"{Arg(1)}  →  found \"{expected}\""
                    : $"{Arg(1)}  →  \"{expected}\" not received");
                break;
            }

            case "response_int_between":
            {
                string prefix = Arg(2);
                int low = IntArg(3, 0), high = IntArg(4, 100);
                bool passed = false;
                string detail = $"\"{prefix}\" not received";
                await SendAndWaitAsync(IntArg(0, 3000), Arg(1), token, buffer =>
                    ResponseAnalyzer.TryCheckBetween(ResponseAnalyzer.CompleteLines(buffer), prefix, low, high,
                        out passed, out detail));
                host.ShowResult(passed, $"{Arg(1)}  →  {detail}");
                break;
            }

            case "response_log":
                await LogReplyAsync(IntArg(0, 3000), Arg(1), Arg(2), Arg(1), token);
                break;

            case "log":
                await LogReplyAsync(IntArg(0, 3000), Arg(1), "", Arg(1), token);
                break;

            case "user_input_command":
            {
                string? answer = await AskOrStopAsync(Arg(2));
                host.Send(Arg(1) + answer);
                break;
            }

            case "user_input_string":
            {
                string? answer = await AskOrStopAsync(Arg(2));
                string command = Arg(1)
                    .Replace("%s", answer)
                    .Replace("%h", Utilities.ConvertStringToHex(answer!, false));
                host.Send(command);
                break;
            }

            case "user_input_log":
            {
                // As on Windows: the prompt comes first, then the command, and the
                // log line starts with the user's answer rather than the command.
                string? answer = await AskOrStopAsync(Arg(1));
                await LogReplyAsync(IntArg(0, 3000), Arg(2), "", answer!, token);
                break;
            }

            case "ymodem":
            case "xmodem":
                host.Status($"**{step.Command} is not available on macOS yet; step skipped.", isError: true);
                break;

            default:
                host.Status($"Unknown script function **{step.Command}; step skipped.", isError: true);
                break;
        }
    }

    private async Task<string?> AskOrStopAsync(string prompt)
    {
        string? answer = await host.AskAsync(prompt);
        if (answer == null) throw new OperationCanceledException("Cancelled at a prompt.");
        return answer;
    }

    /// <summary>
    /// Sends a command and polls the received text until <paramref name="done"/>
    /// says the reply is there or the timeout passes. Returns whether it was found.
    /// </summary>
    private async Task<bool> SendAndWaitAsync(int timeoutMs, string command, CancellationToken token,
        Func<string, bool> done)
    {
        host.StartCapture();
        try
        {
            host.Send(command);
            var clock = Stopwatch.StartNew();
            while (true)
            {
                if (done(host.ReadCapture())) return true;
                if (clock.ElapsedMilliseconds >= timeoutMs) return false;
                await Delay(PollMs, token);
            }
        }
        finally
        {
            host.StopCapture();
        }
    }

    private async Task LogReplyAsync(int timeoutMs, string command, string prefix, string label,
        CancellationToken token)
    {
        string value = "";
        bool found = await SendAndWaitAsync(timeoutMs, command, token, buffer =>
            ResponseAnalyzer.TryGetValueAfter(ResponseAnalyzer.CompleteLines(buffer), prefix, out value));

        if (found)
        {
            host.AppendLog($"{label}\t{DateTime.Now}\t{value.Trim()}");
            host.Status($"Logged {label}: {value.Trim()}");
        }
        else
        {
            host.Status($"No reply to log for {command}", isError: true);
        }
    }
}
