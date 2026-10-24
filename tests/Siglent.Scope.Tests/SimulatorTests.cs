using System.Buffers.Binary;
using Siglent.Simulator;

namespace Siglent.Scope.Tests;

public sealed class SimulatorTests : IAsyncLifetime
{
    private FakeScopeServer _server = null!;
    private SiglentScope _scope = null!;

    public async Task InitializeAsync()
    {
        _server = new FakeScopeServer(port: 0);
        _scope = await SiglentScope.ConnectAsync("127.0.0.1", _server.Port);
    }

    public async Task DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _server.DisposeAsync();
    }

    [Fact]
    public void ReadsIdentity()
    {
        Assert.Equal("SDS1202X-E", _scope.Identity.Model);
        Assert.Equal(2, _scope.ChannelCount);
    }

    [Fact]
    public async Task ReadsAndChangesSettings()
    {
        await _scope.UpdateChannelAsync(2, new ChannelUpdate(VoltsPerDiv: 0.5, Offset: -1, Coupling: "a1m"));
        await _scope.UpdateTimebaseAsync(new TimebaseUpdate(TimePerDiv: 500e-6, TriggerDelay: -1e-4));
        await _scope.UpdateTriggerAsync(new TriggerUpdate(Mode: "NORM", Source: "C2", Slope: "NEG", Level: 1.65));

        var s = await _scope.GetSettingsAsync();

        Assert.Equal(500e-6, s.TimePerDiv, 1e-12);
        Assert.Equal(-1e-4, s.TriggerDelay, 1e-12);
        Assert.Equal(new ChannelSettings(2, true, 0.5, -1, "A1M", 1, false, false, "V", 0), s.Channels[1]);
        Assert.Equal(new TriggerSettings("NORM", "EDGE", "C2", "NEG", 1.65, "DC", "OFF", null, null, null), s.Trigger);
    }

    [Fact]
    public async Task SetsInvertUnitAndSkew()
    {
        await _scope.UpdateChannelAsync(1, new ChannelUpdate(Inverted: true, Unit: "a", Skew: 12.5e-9));

        var c1 = (await _scope.GetSettingsAsync()).Channels[0];

        Assert.Equal((true, "A"), (c1.Inverted, c1.Unit));
        Assert.Equal(12.5e-9, c1.Skew, 1e-15);
        await Assert.ThrowsAsync<ArgumentException>(() => _scope.UpdateChannelAsync(1, new ChannelUpdate(Skew: 150e-9)));
        await Assert.ThrowsAsync<ArgumentException>(() => _scope.UpdateChannelAsync(1, new ChannelUpdate(Unit: "W")));
    }

    [Fact]
    public async Task FrameCarriesEnvelopeAndDenseMeasurements()
    {
        // 700 000 samples: read densely (200 000), returned as 2000 points with a min/max envelope.
        var c2 = (await _scope.GetFrameAsync(maxPoints: 2000)).Channels[1];

        Assert.Equal(c2.Volts.Length, c2.Min!.Length);
        Assert.All(Enumerable.Range(0, c2.Volts.Length), i => Assert.InRange(c2.Volts[i], c2.Min[i], c2.Max![i]));
        Assert.Equal(1000, c2.Stats!.Frequency!.Value, 1);
        Assert.InRange(c2.Stats.Max, 3.2, 3.4);
    }

    [Fact]
    public async Task GateLimitsTheMeasurements()
    {
        // 0.1–0.4 ms after the trigger the square wave on C2 is high.
        var c2 = (await _scope.GetFrameAsync(maxPoints: 2000, gate: (0.1e-3, 0.4e-3))).Channels[1];

        Assert.InRange(c2.Stats!.Min, 3.2, 3.4);
        Assert.Null(c2.Stats.Frequency);
        await Assert.ThrowsAsync<ArgumentException>(() => _scope.GetFrameAsync(gate: (1e-3, 0.5e-3)));
        await _scope.SetMeasurementGateAsync((0.1e-3, 0.4e-3));
        await _scope.SetMeasurementGateAsync(null);
    }

    [Fact]
    public async Task RunsThePassFailTest()
    {
        var pf = await _scope.UpdatePassFailAsync(new PassFailUpdate(XMask: 0.4, YMask: 0.3, CreateMask: true, Running: true));
        Assert.True(pf.Enabled && pf.Running);
        Assert.Equal((0.4, 0.3, "C1"), (pf.XMask, pf.YMask, pf.Source));

        await Task.Delay(400);
        pf = await _scope.GetPassFailAsync();
        Assert.True(pf.Pass > 0);
        Assert.Equal(0, pf.Fail);

        // Moving the waveform makes it fail; with stop on fail the scope stops.
        await _scope.UpdatePassFailAsync(new PassFailUpdate(StopOnFail: true));
        await _scope.UpdateChannelAsync(1, new ChannelUpdate(Offset: 0.5));
        await Task.Delay(300);
        pf = await _scope.GetPassFailAsync();
        Assert.Equal(1, pf.Fail);
        Assert.False(pf.Running);
        Assert.Equal("STOP", (await _scope.GetSettingsAsync()).Trigger.Mode);

        pf = await _scope.UpdatePassFailAsync(new PassFailUpdate(Enabled: false));
        Assert.False(pf.Enabled || pf.Running);
        await Assert.ThrowsAsync<ArgumentException>(() => _scope.UpdatePassFailAsync(new PassFailUpdate(XMask: 5)));
        await Assert.ThrowsAsync<ArgumentException>(() => _scope.UpdatePassFailAsync(new PassFailUpdate(Source: "C3")));
    }

    [Fact]
    public async Task PressesOnlyKnownPanelKeys()
    {
        // CH2 key: selects C2; pressed again switches it off; a third press switches it on again. A channel switched
        // off stays selected, so the next CH1 press only selects C1 (as on the SDS1202X-E).
        async Task<bool> On(int n) => (await _scope.GetSettingsAsync()).Channels.Single(c => c.Channel == n).Enabled;
        await _scope.PressPanelKeyAsync(40);
        Assert.True(await On(2));
        await _scope.PressPanelKeyAsync(40);
        Assert.False(await On(2));
        await _scope.PressPanelKeyAsync(39);
        Assert.True(await On(1));
        await _scope.PressPanelKeyAsync(40);
        Assert.True(await On(2));
        await _scope.PressPanelKeyAsync(7, -1);
        await _scope.PressPanelKeyAsync(16, 0);

        await Assert.ThrowsAsync<ArgumentException>(() => _scope.PressPanelKeyAsync(13)); // Default setup
        await Assert.ThrowsAsync<ArgumentException>(() => _scope.PressPanelKeyAsync(39, -1)); // a button cannot turn
        await Assert.ThrowsAsync<ArgumentException>(() => _scope.PressPanelKeyAsync(7, 2));
        Assert.True((await _scope.GetSettingsAsync()).Channels.Count > 0); // the connection still answers
    }

    [Fact]
    public async Task MeasuresDelayBetweenChannels()
    {
        // Skew C2 by 50 ns: the scope's FRR and the app's edge times must both see it.
        await _scope.UpdateChannelAsync(2, new ChannelUpdate(Skew: 50e-9));
        await _scope.UpdateTimebaseAsync(new TimebaseUpdate(TimePerDiv: 100e-6));

        var scope = await _scope.GetDelayAsync(1, 2);
        Assert.Equal(50e-9, scope["FRR"]!.Value, 1e-12);
        Assert.Equal(0.018, scope["PHA"]!.Value, 1e-6);

        var frame = await _scope.GetFrameAsync(maxPoints: 2000);
        double e1 = frame.Channels[0].Stats!.RisingEdges[0], e2 = frame.Channels[1].Stats!.RisingEdges[0];
        // C1 is a slow sine (8-bit codes, 9 mV/µs at the crossing), C2 a step: ~20 ns agreement is 0.007° at 1 kHz.
        Assert.Equal(50e-9, e2 - e1, 30e-9);
    }

    [Fact]
    public async Task ZoomedFrameIsAFullResolutionSlice()
    {
        var full = await _scope.GetFrameAsync(maxPoints: 2000);
        // The middle 1 % of the record: 7000 of 700 000 samples, read without decimation.
        var zoom = await _scope.GetFrameAsync(maxPoints: 10_000, start: 0.495, end: 0.505);

        var c1 = zoom.Channels[0];
        Assert.Equal(7000, c1.Volts.Length);
        Assert.Equal(1 / zoom.SampleRate, c1.SampleInterval, 1e-15);
        Assert.Equal(full.Channels[0].FirstPointTime + 0.495 * 700_000 / zoom.SampleRate, c1.FirstPointTime, 1e-12);
        await Assert.ThrowsAsync<ArgumentException>(() => _scope.GetFrameAsync(start: 0.6, end: 0.4));
    }

    [Fact]
    public async Task ChangingTriggerSourceKeepsTypeAndCondition()
    {
        await _scope.UpdateTriggerAsync(new TriggerUpdate(Type: "GLIT", HoldType: "P2", HoldValue: 1e-4, HoldValue2: 8e-4));
        await _scope.UpdateTriggerAsync(new TriggerUpdate(Source: "C2"));

        var t = (await _scope.GetSettingsAsync()).Trigger;

        Assert.Equal(("GLIT", "C2", "P2"), (t.Type, t.Source, t.HoldType));
        Assert.Equal(1e-4, t.HoldValue!.Value, 1e-12);
        Assert.Equal(8e-4, t.HoldValue2!.Value, 1e-12);
    }

    [Fact]
    public async Task SetsEdgeHoldoffSlopeTriggerAndLowLevel()
    {
        await _scope.UpdateTriggerAsync(new TriggerUpdate(HoldType: "TI", HoldValue: 1e-3));
        Assert.Equal("EDGE,SR,C1,HT,TI,HV,1.00E-03s", ScpiValue.StripHeader((await _scope.SendRawAsync("TRSE?"))!));

        await _scope.UpdateTriggerAsync(new TriggerUpdate(Type: "SLEW", Level: 1, Level2: -1));
        var t = (await _scope.GetSettingsAsync()).Trigger;
        Assert.Equal(("SLEW", "IS"), (t.Type, t.HoldType));
        Assert.Equal((1.0, -1.0), (t.Level, t.Level2));
    }

    [Fact]
    public async Task WindowTriggerRemembersItsSourceAndUsesBothLevels()
    {
        await _scope.UpdateTriggerAsync(new TriggerUpdate(Type: "WIN", Source: "C2", Level: 2.5, Level2: 0.5));

        var t = (await _scope.GetSettingsAsync()).Trigger;

        // The scope only reports "WIN"; the source comes from what the app set.
        Assert.Equal(("WIN", "C2", 2.5, 0.5), (t.Type, t.Source, t.Level, t.Level2));
    }

    [Fact]
    public async Task PatternTriggerSetsStatesAndLogic()
    {
        await _scope.UpdateTriggerAsync(new TriggerUpdate(
            Type: "PA",
            PatternStates: new Dictionary<string, string> { ["C1"] = "H", ["C2"] = "L" },
            PatternLogic: "NAND"));

        // Reading the settings must not send the level queries the scope leaves unanswered in pattern mode.
        var p = (await _scope.GetSettingsAsync()).Trigger.Pattern!;

        Assert.Equal(("H", "L", "NAND"), (p.States["C1"], p.States["C2"], p.Logic));
    }

    [Fact]
    public async Task SettingsCanBeReadInEveryTriggerType()
    {
        // Guards against queries the scope does not answer in a given type (they would time out).
        foreach (var type in new[] { "EDGE", "GLIT", "SLEW", "INTV", "RUNT", "DROP", "WIN", "PA", "TV" })
        {
            await _scope.UpdateTriggerAsync(new TriggerUpdate(Type: type));
            Assert.Equal(type, (await _scope.GetSettingsAsync()).Trigger.Type);
        }
    }

    [Fact]
    public async Task VideoTriggerRoundTrip()
    {
        await _scope.UpdateTriggerAsync(new TriggerUpdate(Type: "TV", VideoStandard: "PAL", VideoSync: "SELECT", VideoLine: 300, VideoField: 2));

        var t = (await _scope.GetSettingsAsync()).Trigger;

        Assert.Equal(("TV", "C1"), (t.Type, t.Source));
        Assert.Equal(new VideoTrigger("PAL", "SELECT", 300, 2), t.Video);
    }

    [Fact]
    public async Task ChangesAcquisitionAndBandwidthLimit()
    {
        await _scope.UpdateAcquisitionAsync(new AcquisitionUpdate(Mode: "average", Averages: 64, MemoryDepth: "700k", SinXInterpolation: false));
        await _scope.UpdateChannelAsync(1, new ChannelUpdate(BandwidthLimit: true));

        var s = await _scope.GetSettingsAsync();

        Assert.Equal(new AcquisitionSettings("AVERAGE", 64, "700K", false), s.Acquisition);
        Assert.True(s.Channels[0].BandwidthLimit);
        Assert.False(s.Channels[1].BandwidthLimit);
        await Assert.ThrowsAsync<ArgumentException>(() => _scope.UpdateAcquisitionAsync(new AcquisitionUpdate(MemoryDepth: "5M")));
        await Assert.ThrowsAsync<ArgumentException>(() => _scope.UpdateAcquisitionAsync(new AcquisitionUpdate(Averages: 5)));
    }

    [Fact]
    public async Task ReadingSettingsDoesNotSwitchToAverageMode()
    {
        // Regression: FW 1.3.27 switches to average mode when queried with AVGA?.
        await _scope.UpdateAcquisitionAsync(new AcquisitionUpdate(Mode: "SAMPLING"));
        await _scope.GetSettingsAsync();

        Assert.Equal("SAMPLING", (await _scope.GetSettingsAsync()).Acquisition.Mode);
    }

    [Fact]
    public async Task AverageCountIsSetTogetherWithTheMode()
    {
        await _scope.UpdateAcquisitionAsync(new AcquisitionUpdate(Mode: "AVERAGE", Averages: 128));
        await _scope.UpdateAcquisitionAsync(new AcquisitionUpdate(Mode: "SAMPLING"));
        Assert.Equal(128, (await _scope.GetSettingsAsync()).Acquisition.Averages);

        // Switching back to average without a count reuses the last one.
        await _scope.UpdateAcquisitionAsync(new AcquisitionUpdate(Mode: "AVERAGE"));
        Assert.Equal("AVERAGE,128", ScpiValue.StripHeader((await _scope.SendRawAsync("ACQW?"))!));
    }

    [Fact]
    public async Task HistoryStaysOffInAverageMode()
    {
        await _scope.UpdateAcquisitionAsync(new AcquisitionUpdate(Mode: "AVERAGE", Averages: 16));

        Assert.False((await _scope.SetHistoryAsync(true)).Enabled);
    }

    [Fact]
    public async Task BrowsesAndExportsHistoryFrames()
    {
        Assert.False((await _scope.GetSettingsAsync()).History.Enabled);

        var on = await _scope.SetHistoryAsync(true);
        Assert.Equal((true, 40, 40), (on.Enabled, on.Frame, on.FrameCount));

        var fifth = await _scope.SetHistoryFrameAsync(5);
        Assert.Equal((5, "00:12:15.006170"), (fifth.Frame, fifth.FrameTime));

        var exported = new List<(int, string?)>();
        await _scope.ExportHistoryAsync(200, (i, time, frame) =>
        {
            Assert.Equal(2, frame.Channels.Count);
            exported.Add((i, time));
            return Task.CompletedTask;
        });
        Assert.Equal(Enumerable.Range(1, 40), exported.Select(e => e.Item1));
        Assert.Equal(5, (await _scope.GetSettingsAsync()).History.Frame);

        Assert.False((await _scope.SetHistoryAsync(false)).Enabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _scope.ExportHistoryAsync(200, (_, _, _) => Task.CompletedTask));
    }

    [Fact]
    public async Task HistoryFrameCountIsProbedWhenEnabledOnTheScope()
    {
        // History switched on at the front panel: the app did not see FRAM? right after HSMD ON.
        await _scope.SendRawAsync("HSMD ON");
        await _scope.SendRawAsync("FRAM 7");

        var h = (await _scope.GetSettingsAsync()).History;

        Assert.Equal((7, 40), (h.Frame, h.FrameCount));
    }

    [Fact]
    public async Task SavesAndLoadsSetupFile()
    {
        var setup = await _scope.GetSetupAsync();
        Assert.StartsWith("<?xml", System.Text.Encoding.ASCII.GetString(setup));
        await _scope.UpdateTimebaseAsync(new TimebaseUpdate(TimePerDiv: 5e-3));

        await _scope.LoadSetupAsync(setup);

        Assert.Equal(200e-6, (await _scope.GetSettingsAsync()).TimePerDiv, 1e-12);
    }

    [Fact]
    public async Task StoresAndRecallsInternalMemories()
    {
        await _scope.UpdateTimebaseAsync(new TimebaseUpdate(TimePerDiv: 1e-3));
        await _scope.SaveSetupToMemoryAsync(3);
        await _scope.UpdateTimebaseAsync(new TimebaseUpdate(TimePerDiv: 5e-3));

        await _scope.RecallSetupFromMemoryAsync(3);
        Assert.Equal(1e-3, (await _scope.GetSettingsAsync()).TimePerDiv, 1e-12);

        await _scope.RecallSetupFromMemoryAsync(0);
        Assert.Equal(200e-6, (await _scope.GetSettingsAsync()).TimePerDiv, 1e-12);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _scope.SaveSetupToMemoryAsync(0));
    }

    [Fact]
    public async Task RejectsInvalidValues()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _scope.UpdateTriggerAsync(new TriggerUpdate(Mode: "RUN; *RST")));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _scope.UpdateChannelAsync(3, new ChannelUpdate(Enabled: true)));
    }

    [Fact]
    public async Task FrameIsDecimatedAndScaled()
    {
        var frame = await _scope.GetFrameAsync(maxPoints: 2000);

        Assert.Equal(2, frame.Channels.Count);
        var c1 = frame.Channels[0];
        Assert.InRange(c1.Volts.Length, 1900, 2000);
        Assert.Equal(700_000, c1.MemoryPoints);
        // 14 divisions of 200 µs should be covered by the decimated samples.
        Assert.Equal(14 * 200e-6, c1.Volts.Length * c1.SampleInterval, 2e-5);
        Assert.InRange(c1.Volts.Max(), 1.45, 1.6);
        Assert.InRange(c1.Volts.Min(), -1.6, -1.45);
        Assert.InRange(frame.Channels[1].Volts.Max(), 3.2, 3.4);
    }

    [Fact]
    public async Task SkipsDisabledChannels()
    {
        await _scope.UpdateChannelAsync(1, new ChannelUpdate(Enabled: false));
        var frame = await _scope.GetFrameAsync();
        Assert.Equal([2], frame.Channels.Select(c => c.Channel));
    }

    [Fact]
    public async Task DelayIsScreenCenterTimeAsShownOnScope()
    {
        // The scope displays "Delay: -1.00ms" after TRDL 1MS; the API uses the displayed value.
        await _scope.UpdateTimebaseAsync(new TimebaseUpdate(TimePerDiv: 500e-6, TriggerDelay: -1e-3));
        Assert.Equal(1e-3, ScpiValue.ParseNumber((await _scope.SendRawAsync("TRDL?"))!), 1e-12);

        var frame = await _scope.GetFrameAsync();
        Assert.Equal(-1e-3, frame.TriggerDelay, 1e-12);
        Assert.Equal(-1e-3 - 7 * 500e-6, frame.Channels[0].FirstPointTime, 1e-12);
    }

    [Fact]
    public async Task CapturesFullMemoryAndResumesAcquisition()
    {
        var frame = await _scope.CaptureMemoryAsync();

        Assert.All(frame.Channels, c => Assert.Equal(700_000, c.Volts.Length));
        Assert.Equal(1 / frame.SampleRate, frame.Channels[0].SampleInterval, 1e-15);
        Assert.Equal("AUTO", await _scope.SendRawAsync("TRMD?"));
    }

    [Fact]
    public async Task ReadsScopeMeasurements()
    {
        var m = await _scope.GetMeasurementsAsync(1);

        Assert.Equal(1000, m["FREQ"]);
        Assert.Equal(3.0, m["PKPK"]!.Value, 1e-9);
        Assert.Null(m["OVSP"]);
    }

    [Fact]
    public async Task WritesCaptureCsv()
    {
        var frame = new WaveformFrame(1e-3, 0, 1e6,
        [
            new ChannelWaveform(1, 1, 0, -7e-3, 1e-6, 3, [0.5f, -0.25f, 1f]),
            new ChannelWaveform(2, 1, 0, -7e-3, 1e-6, 2, [3.3f, 0f]),
        ]);
        using var stream = new MemoryStream();

        await Siglent.Server.SiglentServer.WriteCsvAsync(frame, stream);

        var lines = System.Text.Encoding.UTF8.GetString(stream.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["time_s,C1_V,C2_V", "-7.00000000E-003,0.5,3.3", "-6.99900000E-003,-0.25,0", "-6.99800000E-003,1,"], lines);
    }

    [Fact]
    public async Task ScreenshotIsCompleteBmpAndConnectionStaysUsable()
    {
        var bmp = await _scope.GetScreenshotAsync();

        Assert.Equal((byte)'B', bmp[0]);
        Assert.Equal(BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(2)), bmp.Length);
        // The trailing byte after the BMP must not confuse the next query.
        Assert.Equal("SDS1202X-E", (await _scope.SendRawAsync("*IDN?"))!.Split(',')[1]);
    }
}
