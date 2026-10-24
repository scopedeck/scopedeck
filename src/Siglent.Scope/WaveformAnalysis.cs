namespace Siglent.Scope;

/// <summary>Measurements of a (dense) sample record; frequency, period and duty are null when not measurable.</summary>
/// <param name="Aliased">Fewer than about six samples per cycle: frequency and duty are not reported.</param>
/// <param name="RisingEdges">Times of the rising mid-level crossings (interpolated), for delay and phase between channels.</param>
public sealed record WaveformStats(
    double Max,
    double Min,
    double Mean,
    double Rms,
    double? Frequency,
    double? Duty,
    bool Aliased,
    double[] RisingEdges);

public static class WaveformAnalysis
{
    private const int MaxEdges = 1000;

    /// <summary>
    /// Min/max envelope: for each group of <paramref name="bucket"/> samples, the smallest and largest value.
    /// Drawn as a band, it shows the true signal span even when many cycles fall on one screen column.
    /// </summary>
    public static (float[] Min, float[] Max) Envelope(ReadOnlySpan<float> v, int bucket)
    {
        int n = (v.Length + bucket - 1) / bucket;
        var min = new float[n];
        var max = new float[n];
        for (int b = 0; b < n; b++)
        {
            var part = v.Slice(b * bucket, Math.Min(bucket, v.Length - b * bucket));
            float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
            foreach (var x in part)
            {
                if (x < lo) lo = x;
                if (x > hi) hi = x;
            }
            (min[b], max[b]) = (lo, hi);
        }
        return (min, max);
    }

    /// <param name="t0">Time of sample 0.</param>
    /// <param name="dt">Sample interval.</param>
    /// <param name="voltsPerDiv">Below 0.3 div peak-to-peak the record counts as noise (no frequency).</param>
    /// <returns>Null without samples (a channel switched off during the read, or an empty gate window).</returns>
    public static WaveformStats? Compute(ReadOnlySpan<float> v, double t0, double dt, double voltsPerDiv)
    {
        if (v.Length == 0) return null;

        double max = double.NegativeInfinity, min = double.PositiveInfinity, sum = 0, sumSq = 0;
        foreach (var x in v)
        {
            if (x > max) max = x;
            if (x < min) min = x;
            sum += x;
            sumSq += (double)x * x;
        }
        double pkpk = max - min;
        double mid = (max + min) / 2, hyst = pkpk * 0.1;
        bool hasSignal = pkpk > voltsPerDiv * 0.3;

        // Rising crossings of the mid level with 10 % hysteresis. The edge time is where a straight line fitted
        // through the samples between the low and high thresholds crosses the mid level: with 8-bit samples a
        // slow edge sits on one code for microseconds, so the last sample below mid would be off by that much.
        var edges = new List<double>();
        var risingIndex = new List<int>();
        bool high = v[0] > mid;
        int lastLow = v[0] < mid - hyst ? 0 : -1;
        for (int i = 1; i < v.Length && hasSignal; i++)
        {
            if (!high)
            {
                if (v[i] < mid - hyst) lastLow = i;
                else if (v[i] > mid + hyst)
                {
                    high = true;
                    risingIndex.Add(i);
                    if (lastLow >= 0 && edges.Count < MaxEdges) edges.Add(t0 + CrossingIndex(v, lastLow, i, mid) * dt);
                }
            }
            else if (v[i] < mid - hyst)
            {
                high = false;
                lastLow = i;
            }
        }

        double? frequency = null, duty = null;
        bool aliased = false;
        if (risingIndex.Count >= 2)
        {
            int first = risingIndex[0], last = risingIndex[^1];
            double f = (risingIndex.Count - 1) / ((last - first) * dt);
            if (1 / (f * dt) < 6)
            {
                aliased = true;
            }
            else
            {
                frequency = f;
                int above = 0;
                for (int i = first; i < last; i++) if (v[i] > mid) above++;
                duty = above / (double)(last - first);
            }
        }

        return new WaveformStats(max, min, sum / v.Length, Math.Sqrt(sumSq / v.Length), frequency, duty, aliased, [.. edges]);
    }

    /// <summary>Fractional sample index where the least-squares line through v[from..to] reaches <paramref name="level"/>.</summary>
    private static double CrossingIndex(ReadOnlySpan<float> v, int from, int to, double level)
    {
        int n = to - from + 1;
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        for (int k = from; k <= to; k++)
        {
            sx += k;
            sy += v[k];
            sxx += (double)k * k;
            sxy += k * (double)v[k];
        }
        double slope = (n * sxy - sx * sy) / (n * sxx - sx * sx);
        double intercept = (sy - slope * sx) / n;
        return slope > 0 ? (level - intercept) / slope : (from + to) / 2.0;
    }
}
