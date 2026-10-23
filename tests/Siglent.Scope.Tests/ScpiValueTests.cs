using Siglent.Scope;

namespace Siglent.Scope.Tests;

public class ScpiValueTests
{
    [Theory]
    [InlineData("C1:VDIV 2.00E+00V", 2.0)]
    [InlineData("2.00E-01", 0.2)]
    [InlineData("TDIV 2.00E-07S", 2e-7)]
    [InlineData("TRDL -0.00E+00S", 0)]
    [InlineData("SARA 5.00E+08Sa/s", 5e8)]
    [InlineData("SARA 1.00GSa/s", 1e9)]
    [InlineData("SARA 500.0kSa/s", 5e5)]
    [InlineData("SANU 1.40E+03pts", 1400)]
    [InlineData("7.00Mpts", 7e6)]
    [InlineData("C3:TRLV 52.00mv", 0.052)]
    [InlineData("500US", 500e-6)]
    [InlineData("2MS", 2e-3)]
    [InlineData("C1:ATTN 10", 10)]
    public void ParsesNumbers(string response, double expected) =>
        Assert.Equal(expected, ScpiValue.ParseNumber(response), 1e-12 + Math.Abs(expected) * 1e-9);

    [Theory]
    [InlineData("C1:TRA ON", true)]
    [InlineData("OFF", false)]
    public void ParsesBools(string response, bool expected) => Assert.Equal(expected, ScpiValue.ParseBool(response));

    [Fact]
    public void KeepsUnheaderedTextWithSpaces() =>
        Assert.Equal("Siglent Technologies,SDS1202X-E", ScpiValue.StripHeader("Siglent Technologies,SDS1202X-E"));

    [Fact]
    public void StripsHeaderFromLists() =>
        Assert.Equal(["EDGE", "SR", "C1", "HT", "OFF"], ScpiValue.ParseList("TRSE EDGE,SR,C1,HT,OFF"));

    [Fact]
    public void RejectsGarbage() => Assert.Throws<FormatException>(() => ScpiValue.ParseNumber("Trig'd"));

    [Theory]
    [InlineData(5e-9, "5NS")]
    [InlineData(500e-6, "500US")]
    [InlineData(0.02, "20MS")]
    [InlineData(2, "2S")]
    public void FormatsTimebase(double seconds, string expected) => Assert.Equal(expected, ScpiValue.FormatTimeDiv(seconds));

    [Fact]
    public void FormatsWithoutExponent() => Assert.Equal("0.0005V", ScpiValue.Format(0.0005, "V"));

    [Theory]
    [InlineData("SDS1202X-E", 2)]
    [InlineData("SDS1104X-E", 4)]
    [InlineData("Something", 2)]
    public void ChannelCountFromModel(string model, int expected) =>
        Assert.Equal(expected, new ScopeIdentity("Siglent", model, "", "").ChannelCount);
}

public class WaveformDecoderTests
{
    [Fact]
    public void DecodesSignedCodes()
    {
        // vdiv 0.5 V, offset -0.5 V: code 2 → 0.54 V (programming guide example); 0xFF is -1 code.
        var volts = WaveformDecoder.ToVolts([0x02, 0x00, 0xFF, 0x81, 0x7D], 0.5, -0.5);
        Assert.Equal([0.54f, 0.5f, 0.48f, -2.04f, 3f], volts.Select(v => MathF.Round(v, 4)));
    }

    [Fact]
    public void FirstPointIsLeftScreenEdge() =>
        Assert.Equal(-35e-9, WaveformDecoder.FirstPointTime(5e-9, 0), 1e-15);
}
