using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Comet.Core;
using Comet.Desktop.ViewModels;

namespace Comet.Desktop.Views;

public partial class MainWindow : Window
{
    /// <summary>
    /// Terminal output is built from coloured runs. Old runs are trimmed so a
    /// device that streams for hours does not grow the visual tree without bound.
    /// </summary>
    private const int MaxTerminalRuns = 4000;

    private MainViewModel? viewModel;

    public MainWindow()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnHistoryDragOver);
        AddHandler(DragDrop.DropEvent, OnHistoryDrop);
        Closing += (_, _) => viewModel?.Dispose();
        Opened += (_, _) => InputBox.Focus();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (viewModel != null)
        {
            viewModel.TerminalTextAppended -= OnTerminalTextAppended;
            viewModel.TerminalCleared -= OnTerminalCleared;
            viewModel.InputFocusRequested -= OnInputFocusRequested;
        }

        viewModel = DataContext as MainViewModel;
        if (viewModel == null) return;

        viewModel.TerminalTextAppended += OnTerminalTextAppended;
        viewModel.TerminalCleared += OnTerminalCleared;
        viewModel.InputFocusRequested += OnInputFocusRequested;
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
            TerminalTextKind.System => this.FindResource("TerminalSystem") as IBrush,
            _ => this.FindResource("TerminalReceive") as IBrush
        };

        Terminal.Inlines ??= new InlineCollection();
        Terminal.Inlines.Add(new Run(e.Text) { Foreground = brush });

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
        Dispatcher.UIThread.Post(() => Terminal.Inlines?.Clear());
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
