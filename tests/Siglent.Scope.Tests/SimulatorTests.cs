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
        Assert.Equal(new ChannelSettings(2, true, 0.5, -1, "A1M", 1), s.Channels[1]);
        Assert.Equal(new TriggerSettings("NORM", "EDGE", "C2", "NEG", 1.65, "DC"), s.Trigger);
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
        Assert.Equal(1.4e6, c1.MemoryPoints);
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

        Assert.All(frame.Channels, c => Assert.Equal(1_400_000, c.Volts.Length));
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
