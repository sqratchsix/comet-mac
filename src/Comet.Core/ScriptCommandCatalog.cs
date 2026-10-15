namespace Comet.Core;

/// <summary>A script function as listed in the Script Commands table and the editor.</summary>
public sealed record ScriptCommandInfo(
    string Name,
    string[] Arguments,
    string Description,
    string Example,
    bool Supported = true)
{
    public string ArgumentText => Arguments.Length == 0 ? "(none)" : string.Join(", ", Arguments);
}

/// <summary>
/// Every function the runner understands, described by what the code does.
/// The Windows build's table (ScriptCommandsForm) had drifted from its runner:
/// it gave response_str as "expected, timeout" where the runner reads
/// "timeout, command, expected", and it left out log, unicode and
/// user_input_log. This table is written against ScriptRunner here.
/// </summary>
public static class ScriptCommandCatalog
{
    public static IReadOnlyList<ScriptCommandInfo> All { get; } = new[]
    {
        new ScriptCommandInfo("delay", new[] { "ms" },
            "Set the pause after every step from here on. Starts at 500 ms.",
            "**delay\t1000"),
        new ScriptCommandInfo("sleep", new[] { "ms" },
            "Pause once for this long.",
            "**sleep\t2000"),
        new ScriptCommandInfo("time", Array.Empty<string>(),
            "Show the current date and time in the status bar.",
            "**time"),
        new ScriptCommandInfo("serialbreak", Array.Empty<string>(),
            "Send a short break on the port.",
            "**serialbreak"),
        new ScriptCommandInfo("sbreak", new[] { "on/off" },
            "Hold the break condition on or release it.",
            "**sbreak\ttrue"),
        new ScriptCommandInfo("rts", new[] { "on/off" },
            "Set the RTS line.",
            "**rts\ttrue"),
        new ScriptCommandInfo("dtr", new[] { "on/off" },
            "Set the DTR line.",
            "**dtr\tfalse"),
        new ScriptCommandInfo("unicode", new[] { "hex code" },
            "Send one character given by its Unicode code point in hex.",
            "**unicode\t1B"),
        new ScriptCommandInfo("settings", new[] { "baud", "ASCII/HEX", "DTR on/off", "idle ms", "CR/NL on/off" },
            "Reopen the port with this baud rate, then set the format, DTR, idle time and read CR/NL. Only the baud rate is required.",
            "**settings\t9600\tASCII\tTrue\t30"),
        new ScriptCommandInfo("response_str", new[] { "timeout ms", "command", "expected" },
            "Send the command and wait for a reply containing the expected text, ignoring case. Shows PASSED or FAILED.",
            "**response_str\t2000\tATI\tRM520N"),
        new ScriptCommandInfo("response_int_between", new[] { "timeout ms", "command", "text before number", "low", "high" },
            "Send the command, read the whole number that follows the given text, and pass if it lies strictly between low and high.",
            "**response_int_between\t2000\tAT+CSQ\t+CSQ: \t10\t31"),
        new ScriptCommandInfo("response_log", new[] { "timeout ms", "command", "text before value" },
            "Send the command and append what follows the given text to COMETResponseLog.txt.",
            "**response_log\t2000\tAT+QTEMP\t+QTEMP: "),
        new ScriptCommandInfo("log", new[] { "timeout ms", "command" },
            "Send the command and append the first line of the reply to COMETResponseLog.txt.",
            "**log\t500\t:MEASURE:VOLTAGE:DC?"),
        new ScriptCommandInfo("user_input_command", new[] { "timeout ms", "command", "prompt" },
            "Ask for a value, then send the command with the value appended.",
            "**user_input_command\t500\tAT+CFUN=\tFunctionality level (0, 1, 4)"),
        new ScriptCommandInfo("user_input_string", new[] { "timeout ms", "command with %s or %h", "prompt" },
            "Ask for a value and send the command with %s replaced by it, or %h by it in hex.",
            "**user_input_string\t500\tswcmd,S268,%s,d\tEnter MTID"),
        new ScriptCommandInfo("user_input_log", new[] { "timeout ms", "prompt", "command" },
            "Ask for a value, send the command, and log the answer with the reply to COMETResponseLog.txt.",
            "**user_input_log\t500\tEnter value for WLAN PWR\tREAD?"),
        new ScriptCommandInfo("set_writenewline", new[] { "on/off" },
            "Turn the CR and NL write endings on or off together.",
            "**set_writenewline\ttrue"),
        new ScriptCommandInfo("set_readnewline", new[] { "on/off" },
            "Turn the CR/NL display option on or off, which puts each received message on its own line.",
            "**set_readnewline\tfalse"),
        new ScriptCommandInfo("ymodem", new[] { "file" },
            "Send a file by YModem. Not available on macOS yet.",
            "**ymodem\t/path/to/firmware.bin", Supported: false),
        new ScriptCommandInfo("xmodem", new[] { "file" },
            "Send a file by XModem. Not available on macOS yet.",
            "**xmodem\t/path/to/firmware.bin", Supported: false),
    };

    public static IReadOnlyList<string> Names { get; } = All.Select(c => c.Name).ToArray();

    public static ScriptCommandInfo? Find(string name) =>
        All.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The Windows runner used Convert.ToBoolean, so only "true" and "false" worked
    /// and "1" quietly meant false. Accept the obvious spellings as well.
    /// </summary>
    public static bool ParseSwitch(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() is "true" or "1" or "on" or "yes" or "high";
}
