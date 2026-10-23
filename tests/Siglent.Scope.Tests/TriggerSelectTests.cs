namespace Siglent.Scope.Tests;

public class TriggerSelectTests
{
    private static TriggerSettings Current(string trse) => SiglentScope.ParseTrigger(trse.Split(','), "AUTO");

    [Fact]
    public void ParsesConditionWithRange() =>
        Assert.Equal(
            new TriggerSettings("AUTO", "GLIT", "C1", null, null, null, "P2", 1e-4, 8e-4, null),
            Current("GLIT,SR,C1,HT,P2,HV,1.00E-04s,HV2,8.00E-04s"));

    [Fact]
    public void NoChangeSendsNothing() =>
        Assert.Null(SiglentScope.BuildTriggerSelect(Current("EDGE,SR,C1,HT,OFF"), new TriggerUpdate(Level: 1)));

    [Fact]
    public void SourceOnlyKeepsCondition() =>
        Assert.Equal("TRSE GLIT,SR,C2", SiglentScope.BuildTriggerSelect(Current("GLIT,SR,C1,HT,PS,HV,6.00E-04s"), new TriggerUpdate(Source: "c2")));

    [Theory]
    [InlineData("GLIT", "TRSE GLIT,SR,C1,HT,PS,HV,0.000001S")]
    [InlineData("SLEW", "TRSE SLEW,SR,C1,HT,IS,HV,0.000001S")]
    [InlineData("DROP", "TRSE DROP,SR,C1,HT,TI,HV,0.001S")]
    [InlineData("EDGE", "TRSE EDGE,SR,C1,HT,OFF")]
    public void TypeChangeUsesValidDefaults(string type, string expected) =>
        Assert.Equal(expected, SiglentScope.BuildTriggerSelect(Current("INTV,SR,C1,HT,I2,HV,1.00E-05s,HV2,3.00E-05s"), new TriggerUpdate(Type: type)));

    [Fact]
    public void RangeConditionGetsUpperLimit() =>
        Assert.Equal("TRSE GLIT,SR,C1,HT,P2,HV,0.0001S,HV2,0.0002S",
            SiglentScope.BuildTriggerSelect(Current("GLIT,SR,C1,HT,PS,HV,1.00E-04s"), new TriggerUpdate(HoldType: "P2")));

    [Fact]
    public void RaisingLowerLimitPastUpperMovesUpperLimit() =>
        Assert.Equal("TRSE GLIT,SR,C1,HT,P2,HV,0.0004S,HV2,0.0008S",
            SiglentScope.BuildTriggerSelect(Current("GLIT,SR,C1,HT,P2,HV,1.00E-06s,HV2,2.00E-06s"), new TriggerUpdate(HoldValue: 4e-4)));

    [Fact]
    public void EdgeHoldoffOnAndOff()
    {
        var edge = Current("EDGE,SR,C1,HT,OFF");
        Assert.Equal("TRSE EDGE,SR,C1,HT,TI,HV,0.0005S", SiglentScope.BuildTriggerSelect(edge, new TriggerUpdate(HoldType: "TI", HoldValue: 5e-4)));
        Assert.Equal("TRSE EDGE,SR,C1,HT,OFF", SiglentScope.BuildTriggerSelect(Current("EDGE,SR,C1,HT,TI,HV,1.00E-03s"), new TriggerUpdate(HoldType: "OFF")));
    }

    [Theory]
    [InlineData("GLIT", "LINE", null, null, null)] // line/external sources only for edge
    [InlineData(null, null, "PS", null, null)]     // pulse condition on an edge trigger
    [InlineData("GLIT", null, "P2", 2e-4, 1e-4)]  // upper limit below lower limit
    [InlineData("TV", null, null, null, null)]     // video trigger is set up on the scope
    [InlineData("FOO", null, null, null, null)]
    public void RejectsInvalidCombinations(string? type, string? source, string? hold, double? hv, double? hv2) =>
        Assert.Throws<ArgumentException>(() => SiglentScope.BuildTriggerSelect(
            Current("EDGE,SR,C1,HT,OFF"), new TriggerUpdate(Type: type, Source: source, HoldType: hold, HoldValue: hv, HoldValue2: hv2)));

    [Fact]
    public void FormatsFrameTime() => Assert.Equal("00:26:45.388406", SiglentScope.FormatFrameTime("FTIM 00: 26: 45. 388406"));
}
