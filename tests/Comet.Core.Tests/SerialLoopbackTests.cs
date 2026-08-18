using System.Runtime.InteropServices;
using System.Text;
using Comet.Core;

namespace Comet.Core.Tests;

/// <summary>
/// Exercises the real serial path against a pseudo terminal pair, so the read
/// loop, the idle-timer coalescing and the ASCII and HEX formatting are all
/// proven on macOS without needing hardware plugged in.
///
/// The test opens a PTY, hands the slave device to ActiveSerialPort exactly as
/// if it were /dev/cu.usbserial-0001, and drives the master side from the test.
/// </summary>
public class SerialLoopbackTests
{
    [Fact]
    public void ReceivedBytesReachTheTerminalAsAscii()
    {
        using var pty = new PseudoTerminal();
        using var port = new ActiveSerialPort();

        string? received = null;
        using var gotData = new ManualResetEventSlim();
        port.DataReadyToRead += (_, _) =>
        {
            received = port.ReadData();
            gotData.Set();
        };

        Assert.True(port.OpenSerialPort(pty.SlavePath, 115200, System.IO.Ports.Parity.None, 8,
            System.IO.Ports.StopBits.One, 500, SerialDataType.Ascii, readTimeout: 50));

        pty.WriteToMaster("OK\r\n");

        Assert.True(gotData.Wait(TimeSpan.FromSeconds(5)), "no data event was raised");
        Assert.Equal("OK\r\n", received);
    }

    [Fact]
    public void ChunkedBytesArriveAsOneMessage()
    {
        using var pty = new PseudoTerminal();
        using var port = new ActiveSerialPort();

        string? received = null;
        using var gotData = new ManualResetEventSlim();
        port.DataReadyToRead += (_, _) =>
        {
            received = port.ReadData();
            gotData.Set();
        };

        // A generous idle window so the three writes coalesce into one event,
        // which is the behaviour the terminal relies on for multi-packet replies.
        Assert.True(port.OpenSerialPort(pty.SlavePath, 115200, System.IO.Ports.Parity.None, 8,
            System.IO.Ports.StopBits.One, 500, SerialDataType.Ascii, readTimeout: 300));

        pty.WriteToMaster("+CSQ: ");
        Thread.Sleep(40);
        pty.WriteToMaster("21,");
        Thread.Sleep(40);
        pty.WriteToMaster("99\r\n");

        Assert.True(gotData.Wait(TimeSpan.FromSeconds(5)), "no data event was raised");
        Assert.Equal("+CSQ: 21,99\r\n", received);
    }

    [Fact]
    public void ReceivedBytesAreShownAsHexInHexMode()
    {
        using var pty = new PseudoTerminal();
        using var port = new ActiveSerialPort();

        string? received = null;
        using var gotData = new ManualResetEventSlim();
        port.DataReadyToRead += (_, _) =>
        {
            received = port.ReadData();
            gotData.Set();
        };

        Assert.True(port.OpenSerialPort(pty.SlavePath, 115200, System.IO.Ports.Parity.None, 8,
            System.IO.Ports.StopBits.One, 500, SerialDataType.Hex, readTimeout: 50));

        pty.WriteToMasterBytes(new byte[] { 0xAA, 0x11, 0x22 });

        Assert.True(gotData.Wait(TimeSpan.FromSeconds(5)), "no data event was raised");
        Assert.Equal("AA1122", received);
    }

    [Fact]
    public void SentCommandsGetTheConfiguredLineEnding()
    {
        using var pty = new PseudoTerminal();
        using var port = new ActiveSerialPort();

        Assert.True(port.OpenSerialPort(pty.SlavePath, 115200, System.IO.Ports.Parity.None, 8,
            System.IO.Ports.StopBits.One, 500, SerialDataType.Ascii, readTimeout: 50));

        port.LineEnding = "\r\n";
        string echoed = port.SendData("AT", endline: true);

        Assert.Equal("AT", echoed);
        Assert.Equal("AT\r\n", pty.ReadFromMaster(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void HexCommandsAreSentAsBytes()
    {
        using var pty = new PseudoTerminal();
        using var port = new ActiveSerialPort();

        Assert.True(port.OpenSerialPort(pty.SlavePath, 115200, System.IO.Ports.Parity.None, 8,
            System.IO.Ports.StopBits.One, 500, SerialDataType.Hex, readTimeout: 50));

        string echoed = port.SendData("AA 11 22", endline: true);

        Assert.Equal("AA1122", echoed);
        Assert.Equal(new byte[] { 0xAA, 0x11, 0x22 }, pty.ReadFromMasterBytes(3, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void OpeningAMissingDeviceReportsFailureRatherThanThrowing()
    {
        using var port = new ActiveSerialPort();

        bool opened = port.OpenSerialPort("/dev/cu.does-not-exist", 115200, System.IO.Ports.Parity.None, 8,
            System.IO.Ports.StopBits.One, 500, SerialDataType.Ascii, readTimeout: 50);

        Assert.False(opened);
        Assert.False(port.IsOpen);
    }

    [Fact]
    public void ClosingLeavesThePortReusable()
    {
        using var pty = new PseudoTerminal();

        using (var port = new ActiveSerialPort())
        {
            Assert.True(port.OpenSerialPort(pty.SlavePath, 115200, System.IO.Ports.Parity.None, 8,
                System.IO.Ports.StopBits.One, 500, SerialDataType.Ascii, readTimeout: 50));
            Assert.Equal(1, port.CloseSerialPort());
            Assert.False(port.IsOpen);
        }

        using var second = new ActiveSerialPort();
        Assert.True(second.OpenSerialPort(pty.SlavePath, 115200, System.IO.Ports.Parity.None, 8,
            System.IO.Ports.StopBits.One, 500, SerialDataType.Ascii, readTimeout: 50));
    }
}

/// <summary>
/// A PTY pair. The slave path behaves like a serial device to anything that
/// opens it, which makes it a faithful stand-in for a USB adapter.
/// </summary>
internal sealed class PseudoTerminal : IDisposable
{
    private const int O_RDWR = 0x0002;
    private const int O_NOCTTY = 0x20000;

    [DllImport("libc", SetLastError = true)] private static extern int posix_openpt(int flags);
    [DllImport("libc", SetLastError = true)] private static extern int grantpt(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int unlockpt(int fd);
    [DllImport("libc", SetLastError = true)] private static extern IntPtr ptsname(int fd);
    [DllImport("libc", SetLastError = true)] private static extern nint write(int fd, byte[] buffer, nint count);
    [DllImport("libc", SetLastError = true)] private static extern nint read(int fd, byte[] buffer, nint count);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);

    private readonly int masterFd;

    public string SlavePath { get; }

    public PseudoTerminal()
    {
        masterFd = posix_openpt(O_RDWR | O_NOCTTY);
        if (masterFd < 0) throw new InvalidOperationException("posix_openpt failed");
        if (grantpt(masterFd) != 0) throw new InvalidOperationException("grantpt failed");
        if (unlockpt(masterFd) != 0) throw new InvalidOperationException("unlockpt failed");

        IntPtr namePtr = ptsname(masterFd);
        SlavePath = Marshal.PtrToStringAnsi(namePtr) ?? throw new InvalidOperationException("ptsname failed");
    }

    public void WriteToMaster(string text) => WriteToMasterBytes(Encoding.Latin1.GetBytes(text));

    public void WriteToMasterBytes(byte[] bytes)
    {
        if (write(masterFd, bytes, bytes.Length) < 0)
            throw new InvalidOperationException("write to pty master failed");
    }

    public string ReadFromMaster(TimeSpan timeout)
        => Encoding.Latin1.GetString(ReadFromMasterBytes(-1, timeout));

    /// <summary>
    /// Reads from the master side, retrying until the expected byte count arrives
    /// or the timeout expires. Pass -1 to take whatever the first read returns.
    /// </summary>
    public byte[] ReadFromMasterBytes(int expectedCount, TimeSpan timeout)
    {
        var collected = new List<byte>();
        var buffer = new byte[1024];
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            nint count = read(masterFd, buffer, buffer.Length);
            if (count > 0)
            {
                collected.AddRange(buffer.Take((int)count));
                if (expectedCount < 0 || collected.Count >= expectedCount) return collected.ToArray();
            }
            Thread.Sleep(10);
        }

        return collected.ToArray();
    }

    public void Dispose()
    {
        if (masterFd >= 0) close(masterFd);
    }
}
