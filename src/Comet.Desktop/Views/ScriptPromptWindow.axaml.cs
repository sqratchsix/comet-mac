using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Comet.Desktop.Views;

public partial class ScriptPromptWindow : Window
{
    public ScriptPromptWindow()
    {
        InitializeComponent();
        Opened += (_, _) => AnswerBox.Focus();
    }

    public ScriptPromptWindow(string prompt) : this()
    {
        PromptText.Text = string.IsNullOrWhiteSpace(prompt) ? "Enter a value" : prompt;
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(AnswerBox.Text ?? "");

    // Closing without an answer returns null, which stops the script.
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
