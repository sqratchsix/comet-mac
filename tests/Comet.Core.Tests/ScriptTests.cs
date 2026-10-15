using Comet.Core;
using Xunit;

namespace Comet.Core.Tests;

public class ScriptTests
{
    [Fact]
    public void ParsesSerialAndFunctionStepsLikeTheWindowsRunner()
    {
        var script = Script.FromLines(new[]
        {
            "qdver",
            "",
            "**response_str\t30000\tswcmd,swcmd,0,qdver\tRWQDVER",
            "++addr 22\t\t\t\t++addr 6",
        });

        Assert.Equal(3, script.Steps.Count);
        Assert.False(script.Steps[0].IsFunction);
        Assert.Equal("qdver", script.Steps[0].Command);

        Assert.True(script.Steps[1].IsFunction);
        Assert.Equal("response_str", script.Steps[1].Command);
        Assert.Equal(new[] { "30000", "swcmd,swcmd,0,qdver", "RWQDVER" }, script.Steps[1].Args);

        // A serial line's trailing field is kept but is not part of what is sent.
        Assert.Equal("++addr 22", script.Steps[2].Command);
        Assert.Equal(new[] { "++addr 6" }, script.Steps[2].Args);
    }

    [Fact]
    public void TimingRoundTripsThroughLeadingDirectives()
    {
        var script = Script.FromLines(new[] { "AT" });
        script.DelayMs = 1000;
        script.LoopCount = 5;
        script.LoopDelayMs = 2000;

        string[] lines = script.ToLines();
        Assert.Equal(new[] { "**delay\t1000", "**loop\t5\t2000", "AT" }, lines);

        var again = Script.FromLines(lines);
        Assert.Equal(1000, again.DelayMs);
        Assert.Equal(5, again.LoopCount);
        Assert.Equal(2000, again.LoopDelayMs);
        Assert.Single(again.Steps);
    }

    [Fact]
    public void DefaultTimingWritesNoDirectivesSoWindowsFilesStayUnchanged()
    {
        string[] original = { "**response_log\t30000\tSWCMD,swcmd,0,q01\tRWQ01, " };
        Assert.Equal(original, Script.FromLines(original).ToLines());
    }

    [Fact]
    public void DelayLaterInTheScriptStaysAStep()
    {
        var script = Script.FromLines(new[] { "AT", "**delay\t50", "ATI" });
        Assert.Equal(Script.DefaultDelayMs, script.DelayMs);
        Assert.Equal(3, script.Steps.Count);
    }
}

public class ResponseAnalyzerTests
{
    [Fact]
    public void ContainsIgnoresCase() =>
        Assert.True(ResponseAnalyzer.Contains("ATI\r\nQuectel\r\nRM520N-GL\r\nOK\r\n", "rm520n"));

    [Fact]
    public void ValueAfterPrefixKeepsTheDevicesCase()
    {
        Assert.True(ResponseAnalyzer.TryGetValueAfter("AT+QTEMP\r\n+QTEMP: 34,Modem\r\nOK\r\n", "+qtemp: ", out var value));
        Assert.Equal("34,Modem", value);
    }

    [Fact]
    public void ValueWithNoPrefixIsTheFirstLineWithText()
    {
        Assert.True(ResponseAnalyzer.TryGetValueAfter("\r\n8.0012\r\n", "", out var value));
        Assert.Equal("8.0012", value);
    }

    [Theory]
    [InlineData("+CSQ: 21,99\r\n", true)]
    [InlineData("+CSQ: 31,99\r\n", false)] // strictly between, as on Windows
    [InlineData("+CSQ: 5,99\r\n", false)]
    public void BetweenIsExclusive(string reply, bool expected)
    {
        Assert.True(ResponseAnalyzer.TryCheckBetween(reply, "+CSQ: ", 10, 31, out bool passed, out _));
        Assert.Equal(expected, passed);
    }

    [Fact]
    public void BetweenWaitsForTheNumber() =>
        Assert.False(ResponseAnalyzer.TryCheckBetween("+CSQ: \r\n", "+CSQ: ", 0, 100, out _, out _));

    [Fact]
    public void CompleteLinesDropsAPartialLastLine() =>
        Assert.Equal("a\r\n", ResponseAnalyzer.CompleteLines("a\r\n+CSQ: 2"));
}

public class ScriptRunnerTests
{
    private sealed class FakeHost : IScriptHost
    {
        private readonly object gate = new();
        private string capture = "";
        private bool capturing;

        public List<string> Sent { get; } = new();
        public List<string> Statuses { get; } = new();
        public List<string> Log { get; } = new();
        public List<(bool Passed, string Detail)> Results { get; } = new();
        public Func<string, string?> Replies { get; set; } = _ => null;
        public Queue<string?> Answers { get; } = new();
        public bool? Dtr;

        public void Send(string command)
        {
            Sent.Add(command);
            string? reply = Replies(command);
            if (reply != null) lock (gate) if (capturing) capture += reply;
        }

        public void Status(string message, bool isError = false) => Statuses.Add(message);
        public void SerialBreak() { }
        public void SetBreak(bool on) { }
        public void SetRts(bool on) { }
        public void SetDtr(bool on) => Dtr = on;
        public void SetWriteNewLine(bool on) { }
        public void SetReadNewLine(bool on) { }
        public Task ApplySettingsAsync(IReadOnlyList<string> args) => Task.CompletedTask;
        public void StartCapture() { lock (gate) { capturing = true; capture = ""; } }
        public string ReadCapture() { lock (gate) return capture; }
        public void StopCapture() { lock (gate) capturing = false; }
        public Task<string?> AskAsync(string prompt) => Task.FromResult(Answers.Dequeue());
        public void ShowResult(bool passed, string detail) => Results.Add((passed, detail));
        public void AppendLog(string line) => Log.Add(line);
    }

    private static (ScriptRunner Runner, List<int> Waits) Make(FakeHost host)
    {
        var waits = new List<int>();
        var runner = new ScriptRunner(host)
        {
            Delay = (ms, _) => { waits.Add(ms); return Task.Delay(ms == 20 ? 1 : 0); }
        };
        return (runner, waits);
    }

    [Fact]
    public async Task SendsEachStepWithTheDelayAfterIt()
    {
        var host = new FakeHost();
        var (runner, waits) = Make(host);
        await runner.RunAsync(Script.FromLines(new[] { "AT", "**delay\t50", "ATI", "**sleep\t7" }), default);

        Assert.Equal(new[] { "AT", "ATI" }, host.Sent);
        // AT then 500; **delay itself is followed by the new 50; ATI 50; the sleep, then 50.
        Assert.Equal(new[] { 500, 50, 50, 7, 50 }, waits);
    }

    [Fact]
    public async Task LoopsWithTheLoopDelayBetweenPasses()
    {
        var host = new FakeHost();
        var (runner, waits) = Make(host);
        var script = Script.FromLines(new[] { "**loop\t3\t1000", "AT" });
        await runner.RunAsync(script, default);

        Assert.Equal(new[] { "AT", "AT", "AT" }, host.Sent);
        Assert.Equal(new[] { 500, 1000, 500, 1000, 500 }, waits);
    }

    [Fact]
    public async Task ResponseStrPassesWhenTheReplyArrives()
    {
        var host = new FakeHost { Replies = c => c == "ATI" ? "Quectel\r\nRM520N-GL\r\nOK\r\n" : null };
        var (runner, _) = Make(host);
        await runner.RunAsync(Script.FromLines(new[] { "**response_str\t200\tATI\trm520n" }), default);

        Assert.True(Assert.Single(host.Results).Passed);
    }

    [Fact]
    public async Task ResponseStrFailsOnTimeout()
    {
        var host = new FakeHost();
        var (runner, _) = Make(host);
        await runner.RunAsync(Script.FromLines(new[] { "**response_str\t50\tATI\tnothing" }), default);

        Assert.False(Assert.Single(host.Results).Passed);
    }

    [Fact]
    public async Task ResponseLogWritesTheValueAfterThePrefix()
    {
        var host = new FakeHost { Replies = _ => "+QTEMP: 34\r\nOK\r\n" };
        var (runner, _) = Make(host);
        await runner.RunAsync(Script.FromLines(new[] { "**response_log\t200\tAT+QTEMP\t+QTEMP: " }), default);

        var line = Assert.Single(host.Log).Split('\t');
        Assert.Equal("AT+QTEMP", line[0]);
        Assert.Equal("34", line[2]);
    }

    [Fact]
    public async Task UserInputStringSubstitutesTheAnswer()
    {
        var host = new FakeHost();
        host.Answers.Enqueue("22");
        var (runner, _) = Make(host);
        await runner.RunAsync(Script.FromLines(new[] { "**user_input_string\t500\tS268,%s,d\tEnter MTID" }), default);

        Assert.Equal(new[] { "S268,22,d" }, host.Sent);
    }

    [Fact]
    public async Task CancellingAPromptStopsTheScript()
    {
        var host = new FakeHost();
        host.Answers.Enqueue(null);
        var (runner, _) = Make(host);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(Script.FromLines(new[] { "**user_input_command\t500\tAT+CFUN=\tLevel", "ATI" }), default));
        Assert.Empty(host.Sent);
    }

    [Fact]
    public async Task SwitchArgumentsAcceptOneAndZero()
    {
        var host = new FakeHost();
        var (runner, _) = Make(host);
        await runner.RunAsync(Script.FromLines(new[] { "**dtr\t1" }), default);
        Assert.True(host.Dtr);
    }

    [Fact]
    public async Task UnknownFunctionsAreReportedAndSkipped()
    {
        var host = new FakeHost();
        var (runner, _) = Make(host);
        await runner.RunAsync(Script.FromLines(new[] { "**frobnicate", "AT" }), default);
        Assert.Equal(new[] { "AT" }, host.Sent);
        Assert.Contains(host.Statuses, s => s.Contains("frobnicate"));
    }

    [Fact]
    public async Task CatalogCoversEveryFunctionTheRunnerHandles()
    {
        var host = new FakeHost();
        var (runner, _) = Make(host);
        foreach (var command in ScriptCommandCatalog.All)
        {
            host.Statuses.Clear();
            host.Answers.Enqueue("x");
            await runner.RunAsync(Script.FromLines(new[] { $"**{command.Name}\t10\t1\t2" }), default);
            Assert.DoesNotContain(host.Statuses, s => s.StartsWith("Unknown"));
        }
    }
}
