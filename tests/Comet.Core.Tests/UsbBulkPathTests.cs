using Comet.Core;
using Xunit;

namespace Comet.Core.Tests;

public class UsbBulkPathTests
{
    [Fact]
    public void PathRoundTrips()
    {
        string path = UsbBulkEnumerator.BuildPath(0x2C7C, 0x0801, "1-3.2.1", 2);
        Assert.Equal("usb:2c7c:0801@1-3.2.1:2", path);

        Assert.True(UsbBulkEnumerator.TryParsePath(path, out int vid, out int pid, out string location, out int iface));
        Assert.Equal(0x2C7C, vid);
        Assert.Equal(0x0801, pid);
        Assert.Equal("1-3.2.1", location);
        Assert.Equal(2, iface);
    }

    [Theory]
    [InlineData("/dev/cu.usbmodem1101")]
    [InlineData("usb:2c7c@1-3:2")]
    [InlineData("usb:2c7c:0801:2")]
    [InlineData("usb:2c7c:0801@:2")]
    [InlineData("usb:zzzz:0801@1-3:2")]
    public void RejectsMalformedPaths(string path)
    {
        Assert.False(UsbBulkEnumerator.TryParsePath(path, out _, out _, out _, out _));
    }

    [Fact]
    public void SerialDevicesAreNotUsbPaths()
    {
        Assert.False(UsbBulkEnumerator.IsUsbPath("/dev/cu.usbmodemAE8913AC3"));
    }
}
