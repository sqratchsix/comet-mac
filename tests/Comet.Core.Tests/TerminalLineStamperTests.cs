using Comet.Core;
using Xunit;

namespace Comet.Core.Tests;

public class TerminalLineStamperTests
{
    private static string Render(TerminalLineStamper stamper, string text, string? stamp = "T") =>
        string.Concat(stamper.Process(text, stamp).Select(s => s.Text));

    [Fact]
    public void EveryLineOfAMultiLineReplyIsStamped()
    {
        var stamper = new TerminalLineStamper();
        Assert.Equal("T ATI\nT Quectel\nT OK\n", Render(stamper, "ATI\r\r\nQuectel\r\nOK\r\n"));
    }

    [Fact]
    public void ReplySplitAcrossReadsIsStampedOncePerLine()
    {
        var stamper = new TerminalLineStamper();
        string output = Render(stamper, "Upti") + Render(stamper, "me: 42 ms\r") + Render(stamper, "\nuart:~$ ");
        Assert.Equal("T Uptime: 42 ms\nT uart:~$ ", output);
    }

    [Fact]
    public void LoneCarriageReturnBreaksTheLine()
    {
        var stamper = new TerminalLineStamper();
        Assert.Equal("T a\nT b", Render(stamper, "a\rb"));
    }

    [Fact]
    public void BlankLinesAreNotStamped()
    {
        var stamper = new TerminalLineStamper();
        Assert.Equal("T a\n\nT b\n", Render(stamper, "a\n\nb\n"));
    }

    [Fact]
    public void StampsAreMarkedAsSuch()
    {
        var stamper = new TerminalLineStamper();
        var segments = stamper.Process("a\n", "12:00");
        Assert.Equal(("12:00 ", true), segments[0]);
        Assert.Equal(("a", false), segments[1]);
    }

    [Fact]
    public void WithoutAStampTextPassesThroughButLineStateIsKept()
    {
        var stamper = new TerminalLineStamper();
        Assert.Equal("partial", Render(stamper, "partial", null));
        Assert.False(stamper.AtLineStart);
        Assert.Equal(" more\nT next", Render(stamper, " more\nnext"));
    }
}
