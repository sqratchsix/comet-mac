using Comet.Core;

namespace Comet.Core.Tests;

public class FormattingTests
{
    [Fact]
    public void HexValidationStripsSpacesAndHyphens()
    {
        Assert.Equal("AA1122", ActiveSerialPort.ValidateStringIsHex("AA 11-22"));
    }

    [Fact]
    public void HexValidationPadsOddLengths()
    {
        Assert.Equal("0ABC", ActiveSerialPort.ValidateStringIsHex("ABC"));
    }

    [Fact]
    public void NonHexInputIsRejected()
    {
        Assert.Equal("DEADC0DE", ActiveSerialPort.ValidateStringIsHex("nothex!!"));
    }

    [Fact]
    public void ByteSpacingGroupsHexIntoPairs()
    {
        var formatter = new DataFormatter();
        Assert.Equal("AA 11 22 33", formatter.AddByteSpacing("AA112233"));
    }

    [Fact]
    public void ByteSpacingOnlyAppliesToHexMode()
    {
        var formatter = new DataFormatter { ByteSpacesEnabled = true };

        Assert.Equal("AA 11", formatter.FormatReceivedData("AA11", SerialDataType.Hex));
        Assert.Equal("AA11", formatter.FormatReceivedData("AA11", SerialDataType.Ascii));
    }

    [Fact]
    public void TimestampIsPrependedWhenEnabled()
    {
        var formatter = new DataFormatter
        {
            TimestampEnabled = true,
            TimestampFormat = "yyyy"
        };

        string result = formatter.FormatReceivedData("OK", SerialDataType.Ascii);

        Assert.EndsWith(" OK", result);
        Assert.Equal(DateTime.Now.Year.ToString() + " OK", result);
    }

    [Fact]
    public void AsciiAndHexConversionsRoundTrip()
    {
        string hex = Utilities.ConvertStringToHex("AT", removeWhitespace: false);
        Assert.Equal("4154", hex);
        Assert.Equal("AT", Utilities.ConvertHex(hex, removeWhitespace: false));
    }

    [Fact]
    public void NaturalComparerOrdersPortNumbersNumerically()
    {
        var names = new List<string> { "usbserial-10", "usbserial-2", "usbserial-1" };
        names.Sort((IComparer<string>)new NaturalComparer());

        Assert.Equal(new[] { "usbserial-1", "usbserial-2", "usbserial-10" }, names);
    }
}
