namespace Siglent.Scope;

public sealed record ScopeIdentity(string Manufacturer, string Model, string Serial, string Firmware)
{
    public static ScopeIdentity Parse(string idn)
    {
        var parts = idn.Split(',', StringSplitOptions.TrimEntries);
        string At(int i) => i < parts.Length ? parts[i] : "";
        return new ScopeIdentity(At(0), At(1), At(2), At(3));
    }

    /// <summary>Analog channel count from the model name: SDS1202X-E → 2, SDS1104X-E → 4.</summary>
    public int ChannelCount =>
        Model.Length >= 7 && Model.StartsWith("SDS", StringComparison.OrdinalIgnoreCase) && Model[6] is '2' or '4'
            ? Model[6] - '0'
            : 2;
}

public sealed record ChannelSettings(
    int Channel,
    bool Enabled,
    double VoltsPerDiv,
    double Offset,
    string Coupling,
    double Probe);

public sealed record TriggerSettings(
    string Mode,
    string Type,
    string Source,
    string? Slope,
    double? Level,
    string? Coupling);

/// <param name="TriggerDelay">Screen-center time relative to the trigger, as shown on the scope ("Delay").</param>
public sealed record ScopeSettings(
    double TimePerDiv,
    double TriggerDelay,
    double SampleRate,
    string Status,
    TriggerSettings Trigger,
    IReadOnlyList<ChannelSettings> Channels);

/// <summary>One channel of a captured frame. Sample i is at FirstPointTime + i * SampleInterval.</summary>
public sealed record ChannelWaveform(
    int Channel,
    double VoltsPerDiv,
    double Offset,
    double FirstPointTime,
    double SampleInterval,
    long MemoryPoints,
    float[] Volts);

public sealed record WaveformFrame(
    double TimePerDiv,
    double TriggerDelay,
    double SampleRate,
    IReadOnlyList<ChannelWaveform> Channels);

public sealed record ChannelUpdate(
    bool? Enabled = null,
    double? VoltsPerDiv = null,
    double? Offset = null,
    string? Coupling = null,
    double? Probe = null);

/// <param name="TriggerDelay">Screen-center time relative to the trigger, as shown on the scope ("Delay").</param>
public sealed record TimebaseUpdate(double? TimePerDiv = null, double? TriggerDelay = null);

public sealed record TriggerUpdate(
    string? Mode = null,
    string? Source = null,
    string? Slope = null,
    double? Level = null,
    string? Coupling = null);
