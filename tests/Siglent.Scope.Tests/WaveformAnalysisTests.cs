namespace Siglent.Scope.Tests;

public class WaveformAnalysisTests
{
    /// <summary>1 kHz square wave 0…3 V sampled every <paramref name="dt"/>, starting at t0.</summary>
    private static float[] Square(int n, double dt, double t0 = 0, double frequency = 1000) =>
        Enumerable.Range(0, n).Select(i => (t0 + i * dt) * frequency % 1 is var p && (p < 0 ? p + 1 : p) < 0.5 ? 3f : 0f).ToArray();

    [Fact]
    public void MeasuresFrequencyDutyAndEdges()
    {
        var v = Square(10_000, 1e-6);   // 10 ms, 1000 samples per cycle

        var s = WaveformAnalysis.Compute(v, 0, 1e-6, voltsPerDiv: 1)!;

        Assert.Equal(1000, s.Frequency!.Value, 0.5);
        Assert.Equal(0.5, s.Duty!.Value, 0.01);
        Assert.Equal((3.0, 0.0, 1.5), (s.Max, s.Min, Math.Round(s.Mean, 2)));
        // Rising edges at 1, 2, … 9 ms (the record starts on a high level).
        Assert.Equal(9, s.RisingEdges.Length);
        Assert.Equal(1e-3, s.RisingEdges[0], 2e-6);
        Assert.False(s.Aliased);
    }

    [Fact]
    public void ReportsAliasingInsteadOfAWrongFrequency()
    {
        // 1 kHz sampled every 0.7 ms (1.4 samples per cycle), like the calibrator at 100 ms/div with 2000 points.
        var v = Square(2000, 0.7e-3);

        var s = WaveformAnalysis.Compute(v, 0, 0.7e-3, voltsPerDiv: 1)!;

        Assert.True(s.Aliased);
        Assert.Null(s.Frequency);
    }

    [Fact]
    public void NoiseHasNoFrequency()
    {
        var rnd = new Random(1);
        var v = Enumerable.Range(0, 5000).Select(_ => (float)(rnd.NextDouble() * 0.02)).ToArray();

        Assert.Null(WaveformAnalysis.Compute(v, 0, 1e-6, voltsPerDiv: 1)!.Frequency);
    }

    [Fact]
    public void NoSamplesGiveNoStats()
    {
        // E.g. a channel switched off between the settings and the waveform read: NaN would break the JSON.
        Assert.Null(WaveformAnalysis.Compute([], 0, 1e-6, voltsPerDiv: 1));
    }

    [Fact]
    public void EnvelopeKeepsTheFullSpanOfEachGroup()
    {
        float[] v = [0, 3, 1, 1, 2, 0, -1, 4, 5];

        var (min, max) = WaveformAnalysis.Envelope(v, 4);

        Assert.Equal([0f, -1f, 5f], min);
        Assert.Equal([3f, 4f, 5f], max);
    }
}
