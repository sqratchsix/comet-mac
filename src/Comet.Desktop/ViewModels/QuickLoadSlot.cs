using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Comet.Desktop.ViewModels;

/// <summary>
/// One of the A, B, C, D buttons in the header. Each loads Button{Letter}.txt
/// from COMET's folder in place of the current smart buttons, as the Windows
/// build's AButton_A..D did.
/// </summary>
public partial class QuickLoadSlot : ViewModelBase
{
    public QuickLoadSlot(string letter)
    {
        Letter = letter;
    }

    public string Letter { get; }

    public string FileName => $"Button{Letter}.txt";

    public string FilePath => Path.Combine(MainViewModel.AppFolder, FileName);

    /// <summary>False dims the button; clicking it then explains where the file should go.</summary>
    [ObservableProperty]
    public partial bool Exists { get; set; }

    /// <summary>True while this file is the one loaded into the history pane.</summary>
    [ObservableProperty]
    public partial bool IsLoaded { get; set; }

    public string Tooltip => Exists ? $"Load {FileName}" : $"{FileName} not found in {MainViewModel.AppFolder}";

    partial void OnExistsChanged(bool value) => OnPropertyChanged(nameof(Tooltip));

    public void Refresh(string? currentFile)
    {
        Exists = File.Exists(FilePath);
        IsLoaded = Exists && currentFile != null &&
                   string.Equals(Path.GetFullPath(currentFile), Path.GetFullPath(FilePath), System.StringComparison.Ordinal);
    }
}
