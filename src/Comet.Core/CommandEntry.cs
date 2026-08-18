using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Comet.Core;

/// <summary>What a history button does when clicked.</summary>
public enum CommandEntryType
{
    SerialCommand,
    ScriptRunner,
    Log
}

/// <summary>
/// One history button. In the Windows build this was a SmartButton, a WinForms
/// Button subclass carrying its own data. Here the data is separated from the
/// control so the UI can bind to a collection of these.
/// </summary>
public sealed class CommandEntry : INotifyPropertyChanged
{
    private string command = "";
    private string description = "";
    private bool showCommand = true;

    public CommandEntryType EntryType { get; set; } = CommandEntryType.SerialCommand;

    /// <summary>The text actually written to the port.</summary>
    public string Command
    {
        get => command;
        set
        {
            if (command == value) return;
            command = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayText));
            OnPropertyChanged(nameof(TooltipText));
        }
    }

    /// <summary>Tooltip text, or the button label when descriptions are shown.</summary>
    public string Description
    {
        get => description;
        set
        {
            if (description == value) return;
            description = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayText));
            OnPropertyChanged(nameof(TooltipText));
        }
    }

    /// <summary>
    /// Whether the button shows the raw command or its description. Toggled for
    /// the whole pane by the Toggle CMD/Description menu item, as in the original.
    /// </summary>
    public bool ShowCommand
    {
        get => showCommand;
        set
        {
            if (showCommand == value) return;
            showCommand = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayText));
            OnPropertyChanged(nameof(TooltipText));
        }
    }

    /// <summary>Text on the button face.</summary>
    public string DisplayText =>
        ShowCommand || string.IsNullOrEmpty(Description) ? Command : Description;

    /// <summary>Text shown on hover: whichever of the two is not on the button.</summary>
    public string TooltipText =>
        ShowCommand || string.IsNullOrEmpty(Description) ? Description : Command;

    /// <summary>
    /// Alternating shade for blank-line-separated groups in a history file: 0 and 1
    /// alternate, 2 marks a script button. Matches the original's buttonStyle.
    /// </summary>
    public int Style { get; set; }

    public bool IsAlternateStyle => Style == 1;

    public bool IsScript => EntryType == CommandEntryType.ScriptRunner;

    /// <summary>
    /// Raw script body lines for a script button, preserved verbatim so a file
    /// round trips through this build without losing script content.
    /// </summary>
    public string[]? ScriptLines { get; set; }

    public CommandEntry() { }

    public CommandEntry(string command, string description, int style = 0,
        CommandEntryType entryType = CommandEntryType.SerialCommand)
    {
        this.command = command;
        this.description = description;
        Style = style;
        EntryType = entryType;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
