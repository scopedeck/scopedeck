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
    double Probe,
    bool BandwidthLimit,
    bool Inverted,
    string Unit,
    double Skew);

/// <param name="Type">EDGE, GLIT (pulse), SLEW, INTV (interval), RUNT, DROP (dropout), WIN (window), PA (pattern) or TV.</param>
/// <param name="HoldType">Condition: OFF/TI (edge holdoff, dropout time), PS/PL/P2/P1 (pulse, runt), IS/IL/I2/I1 (slope, interval).</param>
/// <param name="HoldValue">Holdoff or condition time in seconds; the lower limit for range conditions.</param>
/// <param name="HoldValue2">Upper limit for range conditions (P2/P1/I2/I1).</param>
/// <param name="Level2">Lower trigger level (slope, runt and window triggers).</param>
public sealed record TriggerSettings(
    string Mode,
    string Type,
    string Source,
    string? Slope,
    double? Level,
    string? Coupling,
    string? HoldType,
    double? HoldValue,
    double? HoldValue2,
    double? Level2,
    VideoTrigger? Video = null,
    PatternTrigger? Pattern = null);

/// <param name="Standard">NTSC, PAL, 720P/50, 720P/60, 1080P/50, 1080P/60, 1080I/50 or 1080I/60.</param>
/// <param name="Sync">ANY (every line) or SELECT (the given line and field).</param>
/// <param name="Field">1 or 2 for interlaced standards.</param>
public sealed record VideoTrigger(string Standard, string Sync, int? Line, int? Field);

/// <param name="States">Per channel: H (above its threshold), L (below) or X (ignored).</param>
/// <param name="Logic">AND, OR, NAND or NOR.</param>
/// <remarks>The channel thresholds are set on the scope: in pattern mode it answers no level queries.</remarks>
public sealed record PatternTrigger(IReadOnlyDictionary<string, string> States, string Logic);

/// <param name="Mode">SAMPLING, PEAK_DETECT, AVERAGE or HIGH_RES.</param>
/// <param name="MemoryDepth">As the scope reports it: 7K…7M with two channels, 14K…14M with one.</param>
/// <param name="SinXInterpolation">sin(x)/x (true) or linear (false) interpolation.</param>
public sealed record AcquisitionSettings(string Mode, int Averages, string MemoryDepth, bool SinXInterpolation);

/// <param name="Frame">Current history frame (1-based).</param>
/// <param name="FrameCount">Number of stored frames.</param>
/// <param name="FrameTime">Acquisition time of the current frame, as the scope reports it ("00:26:45.388406").</param>
public sealed record HistoryState(bool Enabled, int Frame, int FrameCount, string? FrameTime);

/// <param name="TriggerDelay">Screen-center time relative to the trigger, as shown on the scope ("Delay").</param>
public sealed record ScopeSettings(
    double TimePerDiv,
    double TriggerDelay,
    double SampleRate,
    string Status,
    AcquisitionSettings Acquisition,
    TriggerSettings Trigger,
    IReadOnlyList<ChannelSettings> Channels,
    HistoryState History);

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
    double? Probe = null,
    bool? BandwidthLimit = null,
    bool? Inverted = null,
    string? Unit = null,
    double? Skew = null);

/// <param name="TriggerDelay">Screen-center time relative to the trigger, as shown on the scope ("Delay").</param>
public sealed record TimebaseUpdate(double? TimePerDiv = null, double? TriggerDelay = null);

public sealed record AcquisitionUpdate(
    string? Mode = null,
    int? Averages = null,
    string? MemoryDepth = null,
    bool? SinXInterpolation = null);

/// <param name="HoldValue">Seconds; for an edge trigger, null or 0 together with HoldType OFF disables holdoff.</param>
public sealed record TriggerUpdate(
    string? Mode = null,
    string? Source = null,
    string? Slope = null,
    double? Level = null,
    string? Coupling = null,
    string? Type = null,
    string? HoldType = null,
    double? HoldValue = null,
    double? HoldValue2 = null,
    double? Level2 = null,
    string? VideoStandard = null,
    string? VideoSync = null,
    int? VideoLine = null,
    int? VideoField = null,
    IReadOnlyDictionary<string, string>? PatternStates = null,
    string? PatternLogic = null);
