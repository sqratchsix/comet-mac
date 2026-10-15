using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Comet.Core;

namespace Comet.Desktop.ViewModels;

/// <summary>
/// One row of the script editor's step table: Type, Command and Arg1 to Arg5,
/// the columns of the Windows build's PromptScriptEdit grid.
/// </summary>
public partial class ScriptStepRow : ViewModelBase
{
    public const int ArgCount = 5;

    public static string[] Types { get; } = { "SERIAL", "FUNCTION" };

    [ObservableProperty]
    public partial string Type { get; set; } = "SERIAL";

    [ObservableProperty]
    public partial string Command { get; set; } = "";

    [ObservableProperty] public partial string Arg1 { get; set; } = "";
    [ObservableProperty] public partial string Arg2 { get; set; } = "";
    [ObservableProperty] public partial string Arg3 { get; set; } = "";
    [ObservableProperty] public partial string Arg4 { get; set; } = "";
    [ObservableProperty] public partial string Arg5 { get; set; } = "";

    public bool IsFunction => Type == "FUNCTION";

    /// <summary>Function names to complete against; none for a serial line.</summary>
    public IReadOnlyList<string> Suggestions => IsFunction ? ScriptCommandCatalog.Names : Array.Empty<string>();

    private ScriptCommandInfo? Info => IsFunction ? ScriptCommandCatalog.Find(Command) : null;

    // Ghost text in each argument box, the Windows editor's per-command hints.
    public string Hint1 => HintAt(0);
    public string Hint2 => HintAt(1);
    public string Hint3 => HintAt(2);
    public string Hint4 => HintAt(3);
    public string Hint5 => HintAt(4);

    public string CommandHint => IsFunction ? "function" : "text to send";

    /// <summary>The description of the function under the cursor, shown as a tooltip.</summary>
    public string? CommandTip => Info?.Description;

    private string HintAt(int i)
    {
        var info = Info;
        return info != null && i < info.Arguments.Length ? info.Arguments[i] : "";
    }

    partial void OnTypeChanged(string value) => RaiseDerived();
    partial void OnCommandChanged(string value) => RaiseDerived();

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(IsFunction));
        OnPropertyChanged(nameof(Suggestions));
        OnPropertyChanged(nameof(CommandHint));
        OnPropertyChanged(nameof(CommandTip));
        foreach (var name in new[] { nameof(Hint1), nameof(Hint2), nameof(Hint3), nameof(Hint4), nameof(Hint5) })
            OnPropertyChanged(name);
    }

    public static ScriptStepRow From(ScriptStep step)
    {
        string Arg(int i) => i < step.Args.Count ? step.Args[i] : "";
        return new ScriptStepRow
        {
            Type = step.IsFunction ? "FUNCTION" : "SERIAL",
            Command = step.Command,
            Arg1 = Arg(0), Arg2 = Arg(1), Arg3 = Arg(2), Arg4 = Arg(3),
            // Anything past the fifth argument is folded into the last box rather than lost.
            Arg5 = string.Join("\t", step.Args.Skip(4)),
        };
    }

    public ScriptStep? ToStep()
    {
        string command = Command.Trim();
        if (IsFunction && command.StartsWith(ScriptStep.FunctionPrefix, StringComparison.Ordinal))
            command = command.Substring(ScriptStep.FunctionPrefix.Length);
        if (command.Length == 0) return null;

        var args = new[] { Arg1, Arg2, Arg3, Arg4 }.Concat(Arg5.Split('\t')).ToList();
        while (args.Count > 0 && args[^1].Length == 0) args.RemoveAt(args.Count - 1);

        return new ScriptStep { IsFunction = IsFunction, Command = command, Args = args };
    }
}

/// <summary>
/// The script editor: name, description, timing, and the steps either as a
/// table or as the raw text that goes in the history file.
/// </summary>
public partial class ScriptEditorViewModel : ViewModelBase
{
    public ScriptEditorViewModel(string name, string description, IEnumerable<string>? lines, bool isNew)
    {
        IsNew = isNew;
        Name = name;
        Description = description;
        LoadScript(Script.FromLines(lines));
        if (Steps.Count == 0) Steps.Add(new ScriptStepRow());
    }

    public bool IsNew { get; }

    public string WindowTitle => IsNew ? "New Script" : $"Edit Script – {Name}";

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string Description { get; set; } = "";

    [ObservableProperty]
    public partial string DelayText { get; set; } = Script.DefaultDelayMs.ToString();

    [ObservableProperty]
    public partial string LoopCountText { get; set; } = "1";

    [ObservableProperty]
    public partial string LoopDelayText { get; set; } = Script.DefaultLoopDelayMs.ToString();

    public ObservableCollection<ScriptStepRow> Steps { get; } = new();

    /// <summary>Text view of the steps, one line each, as saved in the history file.</summary>
    [ObservableProperty]
    public partial bool IsTextMode { get; set; }

    [ObservableProperty]
    public partial string ScriptText { get; set; } = "";

    [ObservableProperty]
    public partial string Error { get; set; } = "";

    partial void OnIsTextModeChanged(bool value)
    {
        // Switching views carries the edits across in both directions.
        if (value)
        {
            ScriptText = string.Join(Environment.NewLine, BuildFromRows().Steps.Select(s => s.ToLine()));
        }
        else
        {
            var parsed = Script.FromLines(SplitLines(ScriptText));
            TakeTimingFrom(parsed);
            Steps.Clear();
            foreach (var step in parsed.Steps) Steps.Add(ScriptStepRow.From(step));
            if (Steps.Count == 0) Steps.Add(new ScriptStepRow());
        }
    }

    private void LoadScript(Script script)
    {
        DelayText = script.DelayMs.ToString();
        LoopCountText = script.LoopCount.ToString();
        LoopDelayText = script.LoopDelayMs.ToString();
        Steps.Clear();
        foreach (var step in script.Steps) Steps.Add(ScriptStepRow.From(step));
    }

    private static IEnumerable<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private Script BuildFromRows()
    {
        var script = new Script();
        foreach (var row in Steps)
        {
            var step = row.ToStep();
            if (step != null) script.Steps.Add(step);
        }
        return script;
    }

    /// <summary>
    /// The script as edited, or null with <see cref="Error"/> set when the timing
    /// fields are not numbers.
    /// </summary>
    public Script? BuildScript()
    {
        Script script;
        if (IsTextMode)
        {
            script = Script.FromLines(SplitLines(ScriptText));
            TakeTimingFrom(script);
        }
        else
        {
            script = BuildFromRows();
        }

        if (!int.TryParse(DelayText, out int delay) || delay < 0) return Fail("Step delay must be a whole number of milliseconds.");
        if (!int.TryParse(LoopCountText, out int loops) || loops < 1) return Fail("Loops must be 1 or more.");
        if (!int.TryParse(LoopDelayText, out int loopDelay) || loopDelay < 0) return Fail("Loop delay must be a whole number of milliseconds.");

        script.DelayMs = delay;
        script.LoopCount = loops;
        script.LoopDelayMs = loopDelay;

        if (script.Steps.Count == 0) return Fail("Add at least one step.");
        Error = "";
        return script;
    }

    /// <summary>
    /// A leading **delay or **loop line typed in text mode is lifted out of the
    /// steps by the parser; move it into the timing boxes so it is not lost.
    /// </summary>
    private void TakeTimingFrom(Script parsed)
    {
        if (parsed.DelayMs != Script.DefaultDelayMs) DelayText = parsed.DelayMs.ToString();
        if (parsed.LoopCount != 1)
        {
            LoopCountText = parsed.LoopCount.ToString();
            LoopDelayText = parsed.LoopDelayMs.ToString();
        }
    }

    private Script? Fail(string message)
    {
        Error = message;
        return null;
    }

    public bool Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            Error = "Give the script a name; it is the button's label.";
            return false;
        }
        return BuildScript() != null;
    }

    [RelayCommand]
    private void AddStep() => Steps.Add(new ScriptStepRow());

    [RelayCommand]
    private void InsertStepAbove(ScriptStepRow row) => Steps.Insert(Math.Max(0, Steps.IndexOf(row)), new ScriptStepRow());

    [RelayCommand]
    private void RemoveStep(ScriptStepRow row)
    {
        Steps.Remove(row);
        if (Steps.Count == 0) Steps.Add(new ScriptStepRow());
    }

    [RelayCommand]
    private void MoveStepUp(ScriptStepRow row)
    {
        int i = Steps.IndexOf(row);
        if (i > 0) Steps.Move(i, i - 1);
    }

    [RelayCommand]
    private void MoveStepDown(ScriptStepRow row)
    {
        int i = Steps.IndexOf(row);
        if (i >= 0 && i < Steps.Count - 1) Steps.Move(i, i + 1);
    }

    /// <summary>Adds a function step from the commands table, prefilled from its example.</summary>
    public void InsertFunction(ScriptCommandInfo info)
    {
        var example = ScriptStep.Parse(info.Example);
        var row = example != null ? ScriptStepRow.From(example) : new ScriptStepRow { Type = "FUNCTION", Command = info.Name };

        if (IsTextMode)
        {
            ScriptText = ScriptText.TrimEnd() + (ScriptText.Trim().Length > 0 ? Environment.NewLine : "") + info.Example;
            return;
        }

        // Fill an empty last row rather than leaving a blank one above the new step.
        var last = Steps.LastOrDefault();
        if (last != null && last.ToStep() == null) Steps[Steps.Count - 1] = row;
        else Steps.Add(row);
    }
}
