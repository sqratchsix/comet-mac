using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Comet.Desktop.Views;

public partial class ScriptResultWindow : Window
{
    private static readonly IBrush PassBrush = new SolidColorBrush(Color.Parse("#2E9E5B"));
    private static readonly IBrush FailBrush = new SolidColorBrush(Color.Parse("#C93A3A"));

    public ScriptResultWindow()
    {
        InitializeComponent();
    }

    public void ShowResult(bool passed, string detail)
    {
        Panel.Background = passed ? PassBrush : FailBrush;
        Verdict.Text = passed ? "PASSED" : "FAILED";
        Detail.Text = detail;
    }

    // Hidden rather than closed, so the next result reuses the window.
    private void OnOk(object? sender, RoutedEventArgs e) => Hide();
}
