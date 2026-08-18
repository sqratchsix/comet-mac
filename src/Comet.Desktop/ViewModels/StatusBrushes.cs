using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Comet.Desktop.ViewModels;

/// <summary>
/// The open / closed indicators. mmyyxx signals state with a small glowing dot
/// and a footer chip whose text sits on a 14% wash of its own colour, so both
/// are reproduced here rather than approximated.
/// </summary>
public static class StatusBrushes
{
    private static readonly IBrush Live = new SolidColorBrush(Color.Parse("#3DDC84"));
    private static readonly IBrush Idle = new SolidColorBrush(Color.Parse("#5C626B"));

    private static readonly IBrush LiveWash = new SolidColorBrush(Color.Parse("#3DDC84"), 0.14);
    private static readonly IBrush IdleWash = new SolidColorBrush(Color.Parse("#5C626B"), 0.14);

    /// <summary>Dot and chip text colour.</summary>
    public static readonly IValueConverter Converter =
        new FuncValueConverter<bool, IBrush>(isOpen => isOpen ? Live : Idle);

    /// <summary>Chip background: the same colour at 14%.</summary>
    public static readonly IValueConverter ChipBackground =
        new FuncValueConverter<bool, IBrush>(isOpen => isOpen ? LiveWash : IdleWash);

    public static readonly IValueConverter ChipLabel =
        new FuncValueConverter<bool, string>(isOpen => isOpen ? "OPEN" : "CLOSED");
}
