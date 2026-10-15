using Comet.Core;
using Xunit;

namespace Comet.Core.Tests;

public class CommandFileFinderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "comet-finder-" + Guid.NewGuid().ToString("N"));

    public CommandFileFinderTests()
    {
        Directory.CreateDirectory(root);
        Touch("ButtonA.txt");
        Touch("Button10.txt");
        Touch("Button2.txt");
        Touch("notes.md");
        Touch(".hidden.txt");
        Touch("COMMANDS/modem.txt");
        Touch("COMET.app/Contents/Resources/licence.txt");
        Touch(".git/info.txt");
        Touch("a/b/c/d/too-deep.txt");
    }

    private void Touch(string relative)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "AT");
    }

    private string[] Names(IReadOnlyList<string> paths) =>
        paths.Select(p => Path.GetRelativePath(root, p)).ToArray();

    [Fact]
    public void FindsTextFilesInNaturalOrderSkippingBundlesAndHiddenFolders()
    {
        Assert.Equal(new[] { "Button2.txt", "Button10.txt", "ButtonA.txt", "COMMANDS/modem.txt" },
            Names(CommandFileFinder.Find(root)));
    }

    [Fact]
    public void StopsAtTheCountLimit()
    {
        Assert.Equal(2, CommandFileFinder.Find(root, maxFiles: 2).Count);
    }

    [Fact]
    public void MissingFolderGivesNothing()
    {
        Assert.Empty(CommandFileFinder.Find(Path.Combine(root, "nope")));
    }

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (Exception) { }
    }
}
