namespace Comet.Core;

/// <summary>
/// One line of a script: a command to send, or a **function with arguments.
/// Lines are tab separated and runs of tabs collapse, as in Comet1.ScriptRunner,
/// so "**response_str\t2000\tqdver\trqdver" is the function response_str with
/// three arguments.
/// </summary>
public sealed class ScriptStep
{
    public const string FunctionPrefix = "**";

    public bool IsFunction { get; set; }

    /// <summary>The text to send, or the function name without its ** prefix.</summary>
    public string Command { get; set; } = "";

    /// <summary>
    /// Function arguments. A serial line can carry fields after the command too
    /// (a description, in files written by hand); they are kept but never sent.
    /// </summary>
    public List<string> Args { get; set; } = new();

    public static ScriptStep? Parse(string line)
    {
        string[] parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0].Length == 0) return null;

        bool isFunction = parts[0].StartsWith(FunctionPrefix, StringComparison.Ordinal);
        return new ScriptStep
        {
            IsFunction = isFunction,
            Command = isFunction ? parts[0].Substring(FunctionPrefix.Length) : parts[0],
            Args = parts.Skip(1).ToList()
        };
    }

    public string ToLine()
    {
        string head = IsFunction ? FunctionPrefix + Command : Command;
        var fields = new[] { head }.Concat(Args.Where(a => !string.IsNullOrEmpty(a)));
        return string.Join("\t", fields);
    }

    public override string ToString() => ToLine();
}

/// <summary>
/// A script button's body plus its timing. The Windows build kept the step delay
/// and loop settings only in memory, so they were lost on save. Here they are
/// written as leading **delay and **loop lines. The Windows runner executes
/// **delay the same way and ignores **loop, so files still open there.
/// </summary>
public sealed class Script
{
    public const int DefaultDelayMs = 500;
    public const int DefaultLoopDelayMs = 100;

    public List<ScriptStep> Steps { get; } = new();

    /// <summary>Pause after every step, until a **delay step changes it.</summary>
    public int DelayMs { get; set; } = DefaultDelayMs;

    public int LoopCount { get; set; } = 1;

    /// <summary>Pause between passes when LoopCount is above one.</summary>
    public int LoopDelayMs { get; set; } = DefaultLoopDelayMs;

    public static Script FromLines(IEnumerable<string>? lines)
    {
        var script = new Script();
        bool leading = true;
        foreach (var line in lines ?? Array.Empty<string>())
        {
            var step = ScriptStep.Parse(line);
            if (step == null) continue;

            if (leading && step.IsFunction && step.Args.Count > 0)
            {
                if (step.Command.Equals("delay", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(step.Args[0], out int delay))
                {
                    script.DelayMs = delay;
                    continue;
                }
                if (step.Command.Equals("loop", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(step.Args[0], out int count))
                {
                    script.LoopCount = Math.Max(1, count);
                    if (step.Args.Count > 1 && int.TryParse(step.Args[1], out int loopDelay))
                        script.LoopDelayMs = loopDelay;
                    continue;
                }
            }

            leading = false;
            script.Steps.Add(step);
        }
        return script;
    }

    public string[] ToLines()
    {
        var lines = new List<string>();
        if (DelayMs != DefaultDelayMs) lines.Add($"{ScriptStep.FunctionPrefix}delay\t{DelayMs}");
        if (LoopCount != 1) lines.Add($"{ScriptStep.FunctionPrefix}loop\t{LoopCount}\t{LoopDelayMs}");
        lines.AddRange(Steps.Select(s => s.ToLine()));
        return lines.ToArray();
    }
}
