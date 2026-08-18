using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Comet.Desktop.ViewModels;

/// <summary>
/// Colours the connection dot in the status bar: green when a port is open,
/// grey when it is not.
/// </summary>
public static class StatusBrushes
{
    private static readonly IBrush Open = new SolidColorBrush(Color.Parse("#3DDC6E"));
    private static readonly IBrush Closed = new SolidColorBrush(Color.Parse("#80808080"));

    public static readonly IValueConverter Converter = new FuncValueConverter<bool, IBrush>(
        isOpen => isOpen ? Open : Closed);
}
