using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using Comet.Core;
using Comet.Desktop;
using Comet.Desktop.ViewModels;
using Comet.Desktop.Views;

// Renders MainWindow off screen and writes a PNG. Used to review the design
// without a display, and to regenerate the README screenshot.
//
//   dotnet run --project tools/Comet.Shot -- out.png

string outputPath = args.Length > 0 ? args[0] : "comet.png";

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

var viewModel = new MainViewModel();
var window = new MainWindow { DataContext = viewModel };
window.Show();

// After Show, so the window is already subscribed to the terminal events and
// the sample output actually lands in the view.
viewModel.LoadSampleContent();

// Two layout passes: the first realises the template, the second settles the
// text runs the terminal added during load.
Dispatcher.UIThread.RunJobs();
window.Measure(new Size(1060, 640));
window.Arrange(new Rect(0, 0, 1060, 640));
Dispatcher.UIThread.RunJobs();

var frame = window.CaptureRenderedFrame();
if (frame == null)
{
    Console.Error.WriteLine("capture returned nothing");
    return 1;
}

frame.Save(outputPath);
Console.WriteLine($"wrote {outputPath}");
return 0;
