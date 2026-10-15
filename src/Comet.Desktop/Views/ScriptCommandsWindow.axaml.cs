using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Comet.Core;

namespace Comet.Desktop.Views;

public partial class ScriptCommandsWindow : Window
{
    public ScriptCommandsWindow()
    {
        InitializeComponent();
        CommandList.ItemsSource = ScriptCommandCatalog.All;
    }

    /// <summary>Set when opened from the script editor, which shows an INSERT button per row.</summary>
    public Action<ScriptCommandInfo>? InsertRequested { get; init; }

    public bool CanInsert => InsertRequested != null;

    private void OnInsert(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is ScriptCommandInfo info) InsertRequested?.Invoke(info);
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (Clipboard == null) return;
        var lines = ScriptCommandCatalog.All.Select(c =>
            $"**{c.Name}\t{c.ArgumentText}\t{c.Description}\t{c.Example.Replace("\t", "  ")}");
        await Clipboard.SetTextAsync(string.Join(Environment.NewLine, lines));
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
