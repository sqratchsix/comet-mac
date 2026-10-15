using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Comet.Core;
using Comet.Desktop.ViewModels;

namespace Comet.Desktop.Views;

public partial class ScriptEditorWindow : Window
{
    public ScriptEditorWindow()
    {
        InitializeComponent();
        Opened += (_, _) => NameBox.Focus();
    }

    private ScriptEditorViewModel? Editor => DataContext as ScriptEditorViewModel;

    /// <summary>Runs the script as edited, from the window that opened the editor.</summary>
    public Action<Script, string>? RunRequested { get; set; }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (Editor?.Validate() == true) Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void OnRun(object? sender, RoutedEventArgs e)
    {
        var script = Editor?.BuildScript();
        if (script != null) RunRequested?.Invoke(script, string.IsNullOrWhiteSpace(Editor!.Name) ? "script" : Editor.Name);
    }

    private void OnShowCommands(object? sender, RoutedEventArgs e)
    {
        var table = new ScriptCommandsWindow { InsertRequested = info => Editor?.InsertFunction(info) };
        table.Show(this);
    }
}
