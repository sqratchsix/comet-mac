using Comet.Core;

namespace Comet.Core.Tests;

/// <summary>
/// The history file format is the contract between this build and the Windows
/// one: files in the COMMANDS folder of the original repo have to load here
/// unchanged, and files saved here have to open there.
/// </summary>
public class HistoryFileTests
{
    [Fact]
    public void CommandAndDescriptionSplitOnTabs()
    {
        var entries = HistoryFile.Parse(new[] { "qdver\t\t\t\tget version" });

        var entry = Assert.Single(entries);
        Assert.Equal("qdver", entry.Command);
        Assert.Equal("get version", entry.Description);
    }

    [Fact]
    public void CommandWithNoDescriptionDescribesItself()
    {
        var entries = HistoryFile.Parse(new[] { "sdboot" });

        var entry = Assert.Single(entries);
        Assert.Equal("sdboot", entry.Command);
        Assert.Equal("sdboot", entry.Description);
    }

    [Fact]
    public void BlankLinesAlternateTheButtonShade()
    {
        var entries = HistoryFile.Parse(new[]
        {
            "first",
            "",
            "second",
            "",
            "third"
        });

        Assert.Equal(3, entries.Count);
        Assert.Equal(0, entries[0].Style);
        Assert.Equal(1, entries[1].Style);
        Assert.Equal(0, entries[2].Style);
    }

    [Fact]
    public void ScriptBlocksBecomeOneEntryAndKeepTheirBody()
    {
        var entries = HistoryFile.Parse(new[]
        {
            "**script\t\t\t\tStartup",
            "sdboot",
            "**DELAY\t500",
            "**script\t\t\t\truns the boot sequence"
        });

        var entry = Assert.Single(entries);
        Assert.Equal(CommandEntryType.ScriptRunner, entry.EntryType);
        Assert.Equal("Startup", entry.Command);
        Assert.Equal("runs the boot sequence", entry.Description);
        Assert.Equal(new[] { "sdboot", "**DELAY\t500" }, entry.ScriptLines);
    }

    [Fact]
    public void RoundTripsThroughSerialize()
    {
        var original = HistoryFile.Parse(new[]
        {
            "qdver\t\t\t\tget version",
            "rbt\t\t\t\treboot"
        });

        var reparsed = HistoryFile.Parse(HistoryFile.Serialize(original));

        Assert.Equal(original.Count, reparsed.Count);
        for (int i = 0; i < original.Count; i++)
        {
            Assert.Equal(original[i].Command, reparsed[i].Command);
            Assert.Equal(original[i].Description, reparsed[i].Description);
        }
    }

    [Fact]
    public void ShowCommandTogglesWhatTheButtonDisplays()
    {
        var entry = new CommandEntry("qdver", "get version");

        Assert.Equal("qdver", entry.DisplayText);
        Assert.Equal("get version", entry.TooltipText);

        entry.ShowCommand = false;

        Assert.Equal("get version", entry.DisplayText);
        Assert.Equal("qdver", entry.TooltipText);
    }

    [Fact]
    public void EntryWithNoDescriptionAlwaysShowsTheCommand()
    {
        var entry = new CommandEntry("rma", "") { ShowCommand = false };

        Assert.Equal("rma", entry.DisplayText);
    }
}
