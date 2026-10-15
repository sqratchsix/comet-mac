using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Comet.Core;
using Comet.Desktop.ViewModels;

namespace Comet.Desktop.Views;

public partial class MainWindow : Window, IScriptUi
{
    /// <summary>
    /// Terminal output is built from coloured runs. Old runs are trimmed so a
    /// device that streams for hours does not grow the visual tree without bound.
    /// </summary>
    private const int MaxTerminalRuns = 8000;

    private MainViewModel? viewModel;

    // Lives on the UI thread with the terminal, so line state follows what is drawn.
    private readonly TerminalLineStamper stamper = new();

    /// <summary>
    /// A line's timestamp. Every line gets one whether or not timestamps are
    /// showing; turning them on or changing the format rewrites these runs, so
    /// earlier output gains its stamps too.
    /// </summary>
    private sealed class StampRun : Run
    {
        public DateTime Time { get; init; }
    }

    public MainWindow()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnHistoryDragOver);
        AddHandler(DragDrop.DropEvent, OnHistoryDrop);
        HistoryPanel.AddHandler(PointerPressedEvent, OnHistoryPointerPressed, RoutingStrategies.Tunnel);
        Closing += (_, _) => viewModel?.Dispose();
        Opened += (_, _) => InputBox.Focus();
        Activated += (_, _) => viewModel?.RefreshQuickLoadSlots();
        Opened += (_, _) => Dispatcher.UIThread.Post(FitMinHeightToPortPanel, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// The port panel never scrolls, so the window may not be shorter than its
    /// settings. MinHeight in the XAML is measured from a headless render; this
    /// raises it if the real font metrics turn out to need a few pixels more.
    /// </summary>
    private void FitMinHeightToPortPanel()
    {
        PortSettings.Measure(Size.Infinity);
        double needed = PortSettings.DesiredSize.Height + PortPanel.Padding.Top + PortPanel.Padding.Bottom
                        + PortPanel.BorderThickness.Top + PortPanel.BorderThickness.Bottom;
        double shortfall = needed - PortPanel.Bounds.Height;
        double requiredWindow = Bounds.Height + shortfall;
        if (requiredWindow > MinHeight) MinHeight = Math.Ceiling(requiredWindow);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (viewModel != null)
        {
            viewModel.TerminalTextAppended -= OnTerminalTextAppended;
            viewModel.TerminalCleared -= OnTerminalCleared;
            viewModel.InputFocusRequested -= OnInputFocusRequested;
            viewModel.History.CollectionChanged -= OnHistoryChanged;
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel.ScriptUi = null;
        }

        viewModel = DataContext as MainViewModel;
        if (viewModel == null) return;

        viewModel.TerminalTextAppended += OnTerminalTextAppended;
        viewModel.TerminalCleared += OnTerminalCleared;
        viewModel.InputFocusRequested += OnInputFocusRequested;
        viewModel.History.CollectionChanged += OnHistoryChanged;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.ScriptUi = this;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.TimestampEnabled) or nameof(MainViewModel.TimestampFormat))
            RefreshStamps();
    }

    private string FormatStamp(DateTime time)
    {
        if (viewModel?.TimestampEnabled != true) return string.Empty;
        try
        {
            return time.ToString(viewModel.TimestampFormat) + " ";
        }
        catch (FormatException)
        {
            // Half-typed format string: fall back rather than blank the column.
            return time.ToString("HH:mm:ss.fff") + " ";
        }
    }

    private void RefreshStamps()
    {
        if (Terminal.Inlines == null) return;
        foreach (var run in Terminal.Inlines.OfType<StampRun>()) run.Text = FormatStamp(run.Time);
    }

    /// <summary>
    /// The newest button is drawn at the top of the stack, so when the pane
    /// overflows, scroll up to keep it in view.
    /// </summary>
    private void OnHistoryChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is System.Collections.Specialized.NotifyCollectionChangedAction.Add
            or System.Collections.Specialized.NotifyCollectionChangedAction.Move)
        {
            Dispatcher.UIThread.Post(() => HistoryScroll.ScrollToHome(), DispatcherPriority.Background);
        }
    }

    #region Terminal rendering

    private void OnTerminalTextAppended(object? sender, TerminalTextEventArgs e)
    {
        // Serial data arrives on the read loop's thread, so hop to the UI thread.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnTerminalTextAppended(sender, e));
            return;
        }

        var brush = e.Kind switch
        {
            TerminalTextKind.Sent => this.FindResource("TerminalSend") as IBrush,
            _ => this.FindResource("TerminalReceive") as IBrush
        };

        // Sent commands and messages always start their own line, even when the
        // device left the cursor after a prompt.
        string text = e.Text;
        if (e.Kind == TerminalTextKind.Sent && !stamper.AtLineStart) text = "\n" + text;

        var stampBrush = this.FindResource("TextTertiary") as IBrush;
        Terminal.Inlines ??= new InlineCollection();
        foreach (var (segment, isStamp) in stamper.Process(text, stamp: ""))
        {
            Terminal.Inlines.Add(isStamp
                ? new StampRun { Time = e.Time, Text = FormatStamp(e.Time), Foreground = stampBrush }
                : new Run(segment) { Foreground = brush });
        }

        while (Terminal.Inlines.Count > MaxTerminalRuns)
        {
            Terminal.Inlines.RemoveAt(0);
        }

        // Only follow the output when the view is already at the bottom, so
        // scrolling back to read something is not yanked away by new data.
        bool atBottom = TerminalScroll.Offset.Y >= TerminalScroll.Extent.Height - TerminalScroll.Viewport.Height - 24;
        if (atBottom)
        {
            Dispatcher.UIThread.Post(() => TerminalScroll.ScrollToEnd(), DispatcherPriority.Background);
        }
    }

    private void OnTerminalCleared(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Terminal.Inlines?.Clear();
            stamper.Reset();
        });
    }

    private void OnInputFocusRequested(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            InputBox.Focus();
            InputBox.CaretIndex = InputBox.Text?.Length ?? 0;
        });
    }

    #endregion

    #region Windows

    /// <summary>
    /// Another COMET with its own port, as File > New did on Windows. It takes the
    /// first USB serial port no other window holds, since ports open exclusively.
    /// </summary>
    private void OnNewWindow(object? sender, EventArgs e)
    {
        var newViewModel = new MainViewModel();
        int step = (int)Math.Round(28 * RenderScaling);
        var window = new MainWindow
        {
            DataContext = newViewModel,
            Width = Bounds.Width,
            Height = Bounds.Height,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(Position.X + step, Position.Y + step)
        };
        window.Show();
        newViewModel.OpenFirstAvailablePort();
    }

    private void OnCloseWindow(object? sender, EventArgs e) => Close();

    // In-window menus raise routed events; the macOS menu bar raises plain ones.
    private void OnNewWindowItem(object? sender, RoutedEventArgs e) => OnNewWindow(sender, e);
    private void OnAppendHistoryItem(object? sender, RoutedEventArgs e) => OnAppendHistory(sender, e);
    private void OnSaveHistoryItem(object? sender, RoutedEventArgs e) => OnSaveHistory(sender, e);

    #endregion

    #region Terminal menu

    private void OnTerminalCopy(object? sender, RoutedEventArgs e) => Terminal.Copy();

    private void OnTerminalSelectAll(object? sender, RoutedEventArgs e) => Terminal.SelectAll();

    #endregion

    #region History menu

    // The button under the pointer when the menu was summoned, or null for the pane.
    private CommandEntry? clickedEntry;

    private void OnHistoryPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(HistoryPanel).Properties.IsRightButtonPressed) return;
        var button = (e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true);
        clickedEntry = button?.DataContext as CommandEntry;
    }

    private void OnHistoryMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var entry = clickedEntry;
        bool onButton = entry != null;
        bool onScript = entry?.IsScript == true;
        foreach (var item in new Control[] { SendButtonItem, CopyButtonItem, RemoveButtonItem, ButtonItemsSeparator })
            item.IsVisible = onButton;
        EditScriptItem.IsVisible = onScript;
        EditButtonItem.IsVisible = onButton && !onScript;

        if (entry != null)
        {
            SendButtonItem.Header = (onScript ? "Run Script  " : "Send  ") + MenuText(Shorten(entry.Command));
            SendButtonItem.IsEnabled = entry.EntryType != CommandEntryType.Log;
            CopyButtonItem.Header = onScript ? "Copy Script" : "Copy Command";
        }

        FillLoadCommandsMenu();
    }

    /// <summary>Every .txt file in COMET's folder, then Browse for anything else.</summary>
    private void FillLoadCommandsMenu()
    {
        LoadCommandsItem.Items.Clear();
        if (viewModel == null) return;

        string folder = MainViewModel.AppFolder;
        var files = viewModel.FindCommandFiles();
        foreach (var path in files)
        {
            var item = new MenuItem { Header = MenuText(Path.GetRelativePath(folder, path)) };
            ToolTip.SetTip(item, path);
            item.Click += (_, _) => viewModel?.LoadHistoryFile(path, replaceExisting: true);
            LoadCommandsItem.Items.Add(item);
        }

        if (files.Count == 0)
        {
            LoadCommandsItem.Items.Add(new MenuItem
            {
                Header = MenuText($"No .txt files in {folder}"),
                IsEnabled = false
            });
        }

        LoadCommandsItem.Items.Add(new Separator());
        var browse = new MenuItem { Header = "Browse…" };
        browse.Click += OnOpenHistory;
        LoadCommandsItem.Items.Add(browse);
    }

    private void OnSendClickedButton(object? sender, RoutedEventArgs e)
    {
        if (clickedEntry != null) viewModel?.SendEntry(clickedEntry);
    }

    private async void OnCopyClickedButton(object? sender, RoutedEventArgs e)
    {
        if (clickedEntry == null || Clipboard == null) return;
        await Clipboard.SetTextAsync(clickedEntry.IsScript
            ? MainViewModel.ScriptText(clickedEntry)
            : clickedEntry.Command);
    }

    private void OnEditClickedButton(object? sender, RoutedEventArgs e)
    {
        if (clickedEntry != null) viewModel?.EditEntry(clickedEntry);
    }

    private void OnRemoveClickedButton(object? sender, RoutedEventArgs e)
    {
        if (clickedEntry != null) viewModel?.RemoveEntry(clickedEntry);
    }

    private static string Shorten(string text) => text.Length <= 40 ? text : text.Substring(0, 39) + "…";

    // Menu headers treat an underscore as an access key marker; double it to show it.
    private static string MenuText(string text) => text.Replace("_", "__");

    #endregion

    #region Scripts

    private ScriptResultWindow? resultWindow;

    public async Task<string?> AskAsync(string prompt) =>
        await new ScriptPromptWindow(prompt).ShowDialog<string?>(this);

    /// <summary>One result window, reused; the script does not wait for it.</summary>
    public void ShowResult(bool passed, string detail)
    {
        if (resultWindow == null)
        {
            resultWindow = new ScriptResultWindow();
            resultWindow.Closed += (_, _) => resultWindow = null;
        }
        resultWindow.ShowResult(passed, detail);
        if (!resultWindow.IsVisible) resultWindow.Show(this);
        resultWindow.Activate();
    }

    private async Task EditScript(CommandEntry? entry)
    {
        if (viewModel == null) return;

        var editor = entry == null
            ? new ScriptEditorViewModel("", "", null, isNew: true)
            : new ScriptEditorViewModel(entry.Command, entry.Description, entry.ScriptLines, isNew: false);

        var window = new ScriptEditorWindow
        {
            DataContext = editor,
            RunRequested = (script, name) => viewModel?.RunScript(script, name)
        };
        if (await window.ShowDialog<bool>(this)) viewModel.SaveScript(entry, editor);
    }

    private ScriptCommandsWindow? commandsWindow;

    private void ShowScriptCommands()
    {
        if (commandsWindow == null)
        {
            commandsWindow = new ScriptCommandsWindow();
            commandsWindow.Closed += (_, _) => commandsWindow = null;
            commandsWindow.Show(this);
        }
        commandsWindow.Activate();
    }

    private async void OnNewScript(object? sender, EventArgs e) => await EditScript(null);
    private async void OnNewScriptItem(object? sender, RoutedEventArgs e) => await EditScript(null);
    private void OnScriptCommands(object? sender, EventArgs e) => ShowScriptCommands();
    private void OnScriptCommandsItem(object? sender, RoutedEventArgs e) => ShowScriptCommands();

    private async void OnEditClickedScript(object? sender, RoutedEventArgs e)
    {
        if (clickedEntry is { IsScript: true } entry) await EditScript(entry);
    }

    #endregion

    #region Device picker

    private void OnDeviceDropDownOpened(object? sender, EventArgs e) => viewModel?.BeginDeviceSelection();

    private void OnDeviceDropDownClosed(object? sender, EventArgs e) => viewModel?.EndDeviceSelection();

    #endregion

    #region Input box

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (viewModel == null) return;

        switch (e.Key)
        {
            case Key.Enter:
                viewModel.Send();
                e.Handled = true;
                break;

            case Key.Up:
                viewModel.RecallPreviousCommand();
                InputBox.CaretIndex = InputBox.Text?.Length ?? 0;
                e.Handled = true;
                break;

            case Key.Down:
                viewModel.RecallNextCommand();
                InputBox.CaretIndex = InputBox.Text?.Length ?? 0;
                e.Handled = true;
                break;
        }
    }

    #endregion

    #region File menu

    private async void OnOpenHistory(object? sender, EventArgs e) => await OpenHistoryFile(replaceExisting: true);

    private async void OnAppendHistory(object? sender, EventArgs e) => await OpenHistoryFile(replaceExisting: false);

    private async Task OpenHistoryFile(bool replaceExisting)
    {
        if (viewModel == null) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = replaceExisting ? "Open History File" : "Append History File",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Text files") { Patterns = new[] { "*.txt" } },
                FilePickerFileTypes.All
            }
        });

        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path)) viewModel.LoadHistoryFile(path, replaceExisting);
    }

    private async void OnSaveHistory(object? sender, EventArgs e)
    {
        if (viewModel == null) return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save History File",
            DefaultExtension = "txt",
            SuggestedFileName = Path.GetFileName(viewModel.CurrentHistoryFile) ?? "COMET_history.txt",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Text files") { Patterns = new[] { "*.txt" } }
            }
        });

        var path = file?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path)) viewModel.SaveHistoryFile(path);
    }

    /// <summary>
    /// The Windows build shelled out to Explorer here; on macOS the equivalent is
    /// `open -R`, which reveals and selects the file in Finder.
    /// </summary>
    private void OnRevealHistoryFile(object? sender, EventArgs e)
    {
        string? path = viewModel?.CurrentHistoryFile;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        try
        {
            Process.Start(new ProcessStartInfo("/usr/bin/open", new[] { "-R", path })
            {
                UseShellExecute = false
            });
        }
        catch (Exception)
        {
            // Nothing useful to do if Finder will not open.
        }
    }

    #endregion

    #region Drag and drop

    private void OnHistoryDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// Dropping text files or folders onto the window loads their commands, the
    /// same convenience the history pane had on Windows.
    /// </summary>
    private void OnHistoryDrop(object? sender, DragEventArgs e)
    {
        if (viewModel == null) return;

        var items = e.DataTransfer.TryGetFiles();
        if (items == null) return;

        var paths = items.Select(i => i.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToList();
        if (paths.Count == 0) return;

        var textFiles = new RecursiveFileProcessor().GetAllPaths(paths);
        bool replace = true;
        foreach (var file in textFiles)
        {
            viewModel.LoadHistoryFile(file, replace);
            replace = false;
        }
    }

    #endregion
}
