namespace Siglent.Scope;

/// <summary>
/// Typed access to an SDS1000X-E series oscilloscope (tested with SDS1202X-E, FW 1.3.27).
/// All operations are serialized, so one instance can be shared across requests.
/// </summary>
public sealed class SiglentScope : IAsyncDisposable
{
    private static readonly HashSet<string> TriggerModes = ["AUTO", "NORM", "SINGLE", "STOP"];
    private static readonly HashSet<string> Slopes = ["POS", "NEG", "WINDOW"];
    private static readonly HashSet<string> Couplings = ["A1M", "D1M", "GND"];
    private static readonly HashSet<string> Units = ["V", "A"];
    private static readonly HashSet<string> TriggerCouplings = ["AC", "DC", "HFREJ", "LFREJ"];
    // WIN and PA are not in the programming guide; FW 1.3.27 accepts and reports them (TRSE WIN, TRSE PA).
    private static readonly HashSet<string> TriggerTypes = ["EDGE", "GLIT", "SLEW", "INTV", "RUNT", "DROP", "WIN", "PA", "TV"];
    private static readonly HashSet<string> PatternLogics = ["AND", "OR", "NAND", "NOR"];
    private static readonly HashSet<string> AcquisitionModes = ["SAMPLING", "PEAK_DETECT", "AVERAGE", "HIGH_RES"];
    private static readonly HashSet<int> AverageCounts = [4, 16, 32, 64, 128, 256, 512, 1024];
    private static readonly HashSet<string> MemoryDepths = ["7K", "70K", "700K", "7M", "14K", "140K", "1.4M", "14M"];

    /// <summary>Valid TRSE conditions per trigger type; the first one is used when switching to that type.</summary>
    private static readonly Dictionary<string, string[]> HoldTypes = new()
    {
        ["EDGE"] = ["OFF", "TI"],
        ["DROP"] = ["TI"],
        ["GLIT"] = ["PS", "PL", "P2", "P1"],
        ["RUNT"] = ["PS", "PL", "P2", "P1"],
        ["SLEW"] = ["IS", "IL", "I2", "I1"],
        ["INTV"] = ["IS", "IL", "I2", "I1"],
    };

    private readonly ScpiConnection _scpi;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ScopeIdentity Identity { get; }
    public int ChannelCount => Identity.ChannelCount;

    private SiglentScope(ScpiConnection scpi, ScopeIdentity identity)
    {
        _scpi = scpi;
        Identity = identity;
    }

    public static async Task<SiglentScope> ConnectAsync(string host, int port = ScpiConnection.DefaultPort, CancellationToken ct = default)
    {
        var scpi = await ScpiConnection.ConnectAsync(host, port, ct: ct);
        try
        {
            // Headers off: responses become bare values ("2.00E-01" instead of "C1:VDIV 2.00E-01V").
            await scpi.WriteAsync("CHDR OFF", ct);
            var identity = ScopeIdentity.Parse(await scpi.QueryAsync("*IDN?", ct));
            return new SiglentScope(scpi, identity);
        }
        catch (TimeoutException e)
        {
            await scpi.DisposeAsync();
            // The scope accepts a second TCP connection but serves only the first one.
            throw new TimeoutException($"{host} accepted the connection but did not answer *IDN?. " +
                "The scope serves one client at a time; close EasyScopeX or other remote sessions.", e);
        }
        catch
        {
            await scpi.DisposeAsync();
            throw;
        }
    }

    public Task<ScopeSettings> GetSettingsAsync(CancellationToken ct = default) => Locked(async () =>
    {
        // BWL? → "C1,OFF,C2,OFF"
        var bwl = ScpiValue.ParseList(await Q("BWL?"));
        var channels = new List<ChannelSettings>();
        for (int ch = 1; ch <= ChannelCount; ch++)
        {
            channels.Add(new ChannelSettings(
                ch,
                ScpiValue.ParseBool(await Q($"C{ch}:TRA?")),
                ScpiValue.ParseNumber(await Q($"C{ch}:VDIV?")),
                ScpiValue.ParseNumber(await Q($"C{ch}:OFST?")),
                ScpiValue.StripHeader(await Q($"C{ch}:CPL?")),
                ScpiValue.ParseNumber(await Q($"C{ch}:ATTN?")),
                FindPair(bwl, $"C{ch}") is "ON",
                ScpiValue.ParseBool(await Q($"C{ch}:INVS?")),
                ScpiValue.StripHeader(await Q($"C{ch}:UNIT?")).ToUpperInvariant(),
                ScpiValue.ParseNumber(await Q($"C{ch}:SKEW?"))));
        }

        // ACQW? → "SAMPLING" or "AVERAGE,16". Never send AVGA?: on FW 1.3.27 that query switches the
        // scope into average mode. The count is only known while averaging, so the last one is remembered.
        var acqw = ScpiValue.ParseList(await Q("ACQW?"));
        if (acqw.Length > 1 && int.TryParse(acqw[1], out int count)) _averages = count;
        var acquisition = new AcquisitionSettings(
            acqw[0].ToUpperInvariant(),
            _averages,
            ScpiValue.StripHeader(await Q("MSIZ?")).ToUpperInvariant(),
            ScpiValue.ParseBool(await Q("SXSA?")));

        var trigger = ParseTrigger(ScpiValue.ParseList(await Q("TRSE?")), ScpiValue.StripHeader(await Q("TRMD?")));
        if (trigger.Type == "WIN") trigger = trigger with { Source = _windowSource };
        // FW 1.3.27 does not answer some queries in some trigger types (verified): TRSL? in window mode, and
        // TRSL?/TRLV?/TRCP?/TRLV2? in pattern and video mode. Only ask what the type supports.
        if (trigger.Type == "PA")
        {
            var (states, logic) = ParsePattern(await Q("TRPA?"));
            trigger = trigger with { Pattern = new PatternTrigger(states, logic) };
        }
        else if (trigger.Type != "TV" && (trigger.Source.StartsWith('C') || trigger.Source.StartsWith("EX")))
        {
            var src = trigger.Source;
            trigger = trigger with
            {
                Slope = trigger.Type == "WIN" ? null : ScpiValue.StripHeader(await Q($"{src}:TRSL?")),
                Level = ScpiValue.ParseNumber(await Q($"{src}:TRLV?")),
                Coupling = ScpiValue.StripHeader(await Q($"{src}:TRCP?")),
                Level2 = trigger.Type is "SLEW" or "RUNT" or "WIN" && src.StartsWith('C')
                    ? ScpiValue.ParseNumber(await Q($"{src}:TRLV2?"))
                    : null,
            };
        }

        return new ScopeSettings(
            ScpiValue.ParseNumber(await Q("TDIV?")),
            -ScpiValue.ParseNumber(await Q("TRDL?")),
            ScpiValue.ParseNumber(await Q("SARA?")),
            ScpiValue.StripHeader(await Q("SAST?")),
            acquisition,
            trigger,
            channels,
            await ReadHistoryAsync(ct));

        Task<string> Q(string query) => _scpi.QueryAsync(query, ct);
    });

    /// <summary>Parses TRSE? → "GLIT,SR,C1,HT,P2,HV,1.00E-04s,HV2,8.00E-04s"; level, slope and coupling are filled in later.</summary>
    internal static TriggerSettings ParseTrigger(string[] trse, string mode)
    {
        string? hv = FindPair(trse, "HV"), hv2 = FindPair(trse, "HV2");
        // TV,SR,C1,STAN,PAL,SYNC,SELECT,LINE,300,FLD,2
        VideoTrigger? video = trse[0].Equals("TV", StringComparison.OrdinalIgnoreCase)
            ? new VideoTrigger(
                FindPair(trse, "STAN")?.ToUpperInvariant() ?? "NTSC",
                FindPair(trse, "SYNC")?.ToUpperInvariant() ?? "ANY",
                int.TryParse(FindPair(trse, "LINE"), out int line) ? line : null,
                int.TryParse(FindPair(trse, "FLD"), out int field) ? field : null)
            : null;
        return new TriggerSettings(
            mode,
            trse[0].ToUpperInvariant(),
            FindPair(trse, "SR") ?? "C1",
            Slope: null,
            Level: null,
            Coupling: null,
            FindPair(trse, "HT")?.ToUpperInvariant(),
            hv is null ? null : ScpiValue.TryParseNumber(hv),
            hv2 is null ? null : ScpiValue.TryParseNumber(hv2),
            Level2: null,
            Video: video);
    }

    private int _averages = 16;
    private string _windowSource = "C1";
    /// <summary>Set when this app switched the measurement gate on; it is switched off again on disconnect.</summary>
    private bool _gateOn;

    // History: FRAM? returns the frame count only right after history is switched on, so it is
    // remembered (or probed) and forgotten when history is switched off.
    private int? _historyFrameCount;

    private async Task<HistoryState> ReadHistoryAsync(CancellationToken ct)
    {
        if (!ScpiValue.ParseBool(await _scpi.QueryAsync("HSMD?", ct)))
        {
            _historyFrameCount = null;
            return new HistoryState(false, 0, 0, null);
        }
        int frame = (int)ScpiValue.ParseNumber(await _scpi.QueryAsync("FRAM?", ct));
        if (_historyFrameCount is null)
        {
            // The scope clamps an out-of-range frame number to the last frame.
            await _scpi.WriteAsync("FRAM 1000000", ct);
            _historyFrameCount = (int)ScpiValue.ParseNumber(await _scpi.QueryAsync("FRAM?", ct));
            await _scpi.WriteAsync($"FRAM {frame}", ct);
        }
        return new HistoryState(true, frame, _historyFrameCount.Value, FormatFrameTime(await _scpi.QueryAsync("FTIM?", ct)));
    }

    /// <summary>"FTIM 00: 26: 45. 388406" → "00:26:45.388406".</summary>
    internal static string FormatFrameTime(string response) => ScpiValue.StripHeader(response.Replace(" ", "").Replace("FTIM", ""));

    /// <summary>
    /// Reads the displayed waveform of every enabled channel. Long memories are decimated on the
    /// scope (WFSU SP) so that at most about <paramref name="maxPoints"/> points cross the network.
    /// </summary>
    /// <param name="start">Start of the transferred range as a fraction of the record (0 = left screen edge).</param>
    /// <param name="end">End of the range (1 = right screen edge). A narrow range is read at higher resolution (zoom).</param>
    /// <param name="gate">Time window (relative to the trigger) the measurements in <see cref="ChannelWaveform.Stats"/> cover; null = all.</param>
    public Task<WaveformFrame> GetFrameAsync(int maxPoints = 2000, double start = 0, double end = 1, (double Start, double End)? gate = null, CancellationToken ct = default)
    {
        if (!(start >= 0 && end <= 1 && start < end)) throw new ArgumentException("The range must satisfy 0 ≤ start < end ≤ 1.");
        if (gate is { } g && !(g.Start < g.End)) throw new ArgumentException("The gate must end after it starts.");
        return Locked(() => ReadWaveformsAsync(Math.Max(1, maxPoints), ct, start, end, gate));
    }

    /// <summary>Samples read for envelope and measurements; 175 000 took 90 ms on the SDS1202X-E, like 2000.</summary>
    internal const int DenseSamples = 200_000;

    /// <summary>
    /// Reads the complete acquisition memory (up to 14 Mpts per channel) of every enabled channel.
    /// A running scope is stopped first so all channels come from the same acquisition, then resumed.
    /// </summary>
    public Task<WaveformFrame> CaptureMemoryAsync(CancellationToken ct = default) => Locked(async () =>
    {
        string mode = ScpiValue.StripHeader(await _scpi.QueryAsync("TRMD?", ct)).ToUpperInvariant();
        bool resume = mode is "AUTO" or "NORM";
        if (mode != "STOP") await _scpi.WriteAsync("STOP", ct);
        try
        {
            return await ReadWaveformsAsync(null, ct);
        }
        finally
        {
            if (resume) await _scpi.WriteAsync($"TRMD {mode}", CancellationToken.None);
        }
    });

    /// <summary>The scope's own measurements of a channel (C1:PAVA? ALL, ~0.5 s). Unavailable values are null.</summary>
    public Task<IReadOnlyDictionary<string, double?>> GetMeasurementsAsync(int channel, CancellationToken ct = default)
    {
        CheckChannel(channel);
        return Locked<IReadOnlyDictionary<string, double?>>(async () =>
        {
            // → "MAX,3.04E-01,MIN,0.00E+00,...,FREQ,1.00E+03,..."
            var list = ScpiValue.ParseList(await _scpi.QueryAsync($"C{channel}:PAVA? ALL", ct));
            var result = new Dictionary<string, double?>();
            for (int i = 0; i + 1 < list.Length; i += 2)
                result[list[i].ToUpperInvariant()] = ScpiValue.TryParseNumber(list[i + 1]);
            return result;
        });
    }

    /// <summary>
    /// Limits the scope's own measurements to a time window relative to the trigger (MEGS/MEGA/MEGB, verified:
    /// a window on the flat top of a square wave gives Vpp ≈ noise and no frequency); null switches the gate off.
    /// The gate state cannot be read back (MEGS? is unanswered on FW 1.3.27).
    /// </summary>
    public Task SetMeasurementGateAsync((double Start, double End)? gate, CancellationToken ct = default)
    {
        if (gate is { } check && !(check.Start < check.End)) throw new ArgumentException("The gate must end after it starts.");
        _gateOn = gate is not null;
        if (gate is not { } g) return WriteAllAsync(["MEGS OFF"], ct);
        // Gate A may not pass gate B and vice versa, so B is set before and after A.
        string a = ScpiValue.Format(g.Start, "S"), b = ScpiValue.Format(g.End, "S");
        return WriteAllAsync(["MEGS ON", $"MEGB {b}", $"MEGA {a}", $"MEGB {b}"], ct);
    }

    /// <summary>
    /// The scope's delay measurements between two channels (C1-C2:MEAD?, ~0.2 s each): PHA in degrees and FRR
    /// (first rising edge to first rising edge) in seconds. Null where the scope reports "****".
    /// </summary>
    public Task<IReadOnlyDictionary<string, double?>> GetDelayAsync(int a, int b, CancellationToken ct = default)
    {
        CheckChannel(a);
        CheckChannel(b);
        if (a == b) throw new ArgumentException("Delay needs two different channels.");
        return Locked<IReadOnlyDictionary<string, double?>>(async () =>
        {
            var result = new Dictionary<string, double?>();
            foreach (var type in new[] { "PHA", "FRR" })
            {
                // → "PHA,-89.46degree" or "FRR,****"
                var list = ScpiValue.ParseList(await _scpi.QueryAsync($"C{a}-C{b}:MEAD? {type}", ct));
                result[type] = list.Length > 1 ? ScpiValue.TryParseNumber(list[1].Replace("degree", "", StringComparison.OrdinalIgnoreCase)) : null;
            }
            return result;
        });
    }

    /// <param name="maxPoints">
    /// Points per channel to return; null reads every sample. With a limit, up to <see cref="DenseSamples"/> are read
    /// and reduced to maxPoints samples plus a min/max envelope, and measured on the dense data.
    /// </param>
    private async Task<WaveformFrame> ReadWaveformsAsync(int? maxPoints, CancellationToken ct, double start = 0, double end = 1,
        (double Start, double End)? gate = null)
    {
        double tdiv = ScpiValue.ParseNumber(await Q("TDIV?"));
        double delay = -ScpiValue.ParseNumber(await Q("TRDL?"));
        double sara = ScpiValue.ParseNumber(await Q("SARA?"));
        double t0 = WaveformDecoder.FirstPointTime(tdiv, delay);

        var channels = new List<ChannelWaveform>();
        for (int ch = 1; ch <= ChannelCount; ch++)
        {
            // Requesting a disabled channel makes the scope stall.
            if (!ScpiValue.ParseBool(await Q($"C{ch}:TRA?"))) continue;
            double vdiv = ScpiValue.ParseNumber(await Q($"C{ch}:VDIV?"));
            double ofst = ScpiValue.ParseNumber(await Q($"C{ch}:OFST?"));
            long total = (long)ScpiValue.ParseNumber(await Q($"SANU? C{ch}"));
            if (total <= 0) continue;

            // FP selects the first sample, which makes zoomed views full-resolution (verified on hardware).
            long first = (long)Math.Floor(start * total);
            long span = Math.Max(1, (long)Math.Ceiling(end * total) - first);
            int step = maxPoints is null ? 1 : (int)Math.Max(1, Math.Ceiling(span / (double)DenseSamples));
            long count = (span + step - 1) / step;
            // NP must be non-zero: with NP 0 the scope ignores SP.
            await _scpi.WriteAsync($"WFSU SP,{step},NP,{count},FP,{first}", ct);
            var volts = WaveformDecoder.ToVolts(await _scpi.QueryBlockAsync($"C{ch}:WF? DAT2", ct), vdiv, ofst);
            double start0 = t0 + first / sara, dt = step / sara;

            if (maxPoints is not { } max)
            {
                channels.Add(new ChannelWaveform(ch, vdiv, ofst, start0, dt, total, volts));
                continue;
            }

            // Measure on the dense record (only the gate window, if any)…
            var measured = volts.AsSpan();
            double measuredStart = start0;
            if (gate is { } g)
            {
                int i0 = (int)Math.Clamp(Math.Ceiling((g.Start - start0) / dt), 0, volts.Length);
                int i1 = (int)Math.Clamp(Math.Floor((g.End - start0) / dt) + 1, i0, volts.Length);
                measured = measured[i0..i1];
                measuredStart = start0 + i0 * dt;
            }
            var stats = WaveformAnalysis.Compute(measured, measuredStart, dt, vdiv);

            // …and return every k-th sample plus the min/max of each group of k for drawing.
            int k = (int)Math.Max(1, Math.Ceiling(volts.Length / (double)max));
            if (k == 1)
            {
                channels.Add(new ChannelWaveform(ch, vdiv, ofst, start0, dt, total, volts, Stats: stats));
                continue;
            }
            var shown = new float[(volts.Length + k - 1) / k];
            for (int i = 0; i < shown.Length; i++) shown[i] = volts[i * k];
            var (min, maxEnv) = WaveformAnalysis.Envelope(volts, k);
            channels.Add(new ChannelWaveform(ch, vdiv, ofst, start0, dt * k, total, shown, min, maxEnv, stats));
        }

        return new WaveformFrame(tdiv, delay, sara, channels);

        Task<string> Q(string query) => _scpi.QueryAsync(query, ct);
    }

    /// <summary>Returns the screen as a BMP file (800×480, RGB565).</summary>
    public Task<byte[]> GetScreenshotAsync(CancellationToken ct = default) =>
        Locked(() => _scpi.QueryBitmapAsync("SCDP", ct));

    public Task UpdateChannelAsync(int channel, ChannelUpdate update, CancellationToken ct = default)
    {
        CheckChannel(channel);
        var c = $"C{channel}";
        var commands = new List<string>();
        if (update.Probe is { } probe) commands.Add($"{c}:ATTN {ScpiValue.Format(probe, "")}");
        if (update.Enabled is { } on) commands.Add($"{c}:TRA {(on ? "ON" : "OFF")}");
        if (update.Coupling is { } cpl) commands.Add($"{c}:CPL {Checked(cpl, Couplings)}");
        if (update.VoltsPerDiv is { } vdiv) commands.Add($"{c}:VDIV {ScpiValue.Format(vdiv, "V")}");
        if (update.Offset is { } ofst) commands.Add($"{c}:OFST {ScpiValue.Format(ofst, "V")}");
        if (update.BandwidthLimit is { } bwl) commands.Add($"BWL {c},{(bwl ? "ON" : "OFF")}");
        if (update.Inverted is { } inv) commands.Add($"{c}:INVS {(inv ? "ON" : "OFF")}");
        // Switching the unit can change the trigger level on FW 1.3.27; settings are re-read afterwards.
        if (update.Unit is { } unit) commands.Add($"{c}:UNIT {Checked(unit, Units)}");
        if (update.Skew is { } skew)
        {
            if (Math.Abs(skew) > 100e-9) throw new ArgumentException("Skew must be within ±100 ns.");
            commands.Add($"{c}:SKEW {ScpiValue.Format(skew, "S")}");
        }
        return WriteAllAsync(commands, ct);
    }

    public Task UpdateAcquisitionAsync(AcquisitionUpdate update, CancellationToken ct = default)
    {
        var commands = new List<string>();
        if (update.Averages is { } n && !AverageCounts.Contains(n))
            throw new ArgumentException($"Invalid average count {n}.");
        // AVGA (like the AVGA? query) switches the scope into average mode, so averages always go with ACQW.
        var m = update.Mode is { } mode ? Checked(mode, AcquisitionModes) : update.Averages is not null ? "AVERAGE" : null;
        if (m == "AVERAGE" && update.Averages is { } requested) _averages = requested;
        if (m is not null)
            commands.Add(m == "AVERAGE" ? $"ACQW AVERAGE,{update.Averages ?? _averages}" : $"ACQW {m}");
        if (update.MemoryDepth is { } depth)
        {
            var d = depth.Trim().ToUpperInvariant();
            commands.Add(MemoryDepths.Contains(d) ? $"MSIZ {d}" : throw new ArgumentException($"Invalid memory depth '{depth}'."));
        }
        if (update.SinXInterpolation is { } sinx) commands.Add($"SXSA {(sinx ? "ON" : "OFF")}");
        return WriteAllAsync(commands, ct);
    }

    public Task UpdateTimebaseAsync(TimebaseUpdate update, CancellationToken ct = default)
    {
        var commands = new List<string>();
        if (update.TimePerDiv is { } tdiv) commands.Add($"TDIV {ScpiValue.FormatTimeDiv(tdiv)}");
        if (update.TriggerDelay is { } delay) commands.Add($"TRDL {ScpiValue.Format(-delay, "S")}");
        return WriteAllAsync(commands, ct);
    }

    public Task UpdateTriggerAsync(TriggerUpdate update, CancellationToken ct = default) => Locked(async () =>
    {
        var current = ParseTrigger(ScpiValue.ParseList(await _scpi.QueryAsync("TRSE?", ct)), "");
        if (current.Type == "WIN") current = current with { Source = _windowSource };
        var source = update.Source?.Trim().ToUpperInvariant() ?? current.Source;
        var type = update.Type?.Trim().ToUpperInvariant() ?? current.Type;
        if (BuildTriggerSelect(current, update) is { } trse)
        {
            await _scpi.WriteAsync(trse, ct);
            if (type != current.Type) await AwaitTriggerTypeAsync(trse, type, source, ct);
        }
        // TRSE? does not report a window trigger's source, so the one set here is remembered.
        if (type == "WIN") _windowSource = source;

        if (update.PatternStates is not null || update.PatternLogic is not null)
            await _scpi.WriteAsync(BuildPattern(ParsePattern(await _scpi.QueryAsync("TRPA?", ct)), update), ct);
        if (update.Slope is { } slope) await _scpi.WriteAsync($"{source}:TRSL {Checked(slope, Slopes)}", ct);
        if (update.Coupling is { } cpl) await _scpi.WriteAsync($"{source}:TRCP {Checked(cpl, TriggerCouplings)}", ct);
        if (update.Level is { } level) await _scpi.WriteAsync($"{source}:TRLV {ScpiValue.Format(level, "V")}", ct);
        if (update.Level2 is { } level2) await _scpi.WriteAsync($"{source}:TRLV2 {ScpiValue.Format(level2, "V")}", ct);
        if (update.Mode is { } mode) await _scpi.WriteAsync($"TRMD {Checked(mode, TriggerModes)}", ct);
        return 0;
    });

    /// <summary>
    /// After a trigger type change the scope needs about a second before it applies the next TRSE: switching from
    /// window or video back to edge kept the old source when the command came too soon (FW 1.3.27). Waits until
    /// TRSE? reports the new type (and source, where reported) and sends the command once more if it does not.
    /// </summary>
    private async Task AwaitTriggerTypeAsync(string trse, string type, string source, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            for (int poll = 0; poll < 10; poll++)
            {
                var now = ParseTrigger(ScpiValue.ParseList(await _scpi.QueryAsync("TRSE?", ct)), "");
                bool sourceKnown = type is not ("WIN" or "PA");
                if (now.Type == type && (!sourceKnown || now.Source == source)) return;
                await Task.Delay(150, ct);
            }
            await _scpi.WriteAsync(trse, ct);
        }
        throw new InvalidOperationException($"The scope did not switch to the {type} trigger.");
    }

    /// <summary>
    /// Builds the TRSE command for a trigger change, or null if type, source and condition are unchanged.
    /// A source-only change sends just the type and source, which keeps the condition (verified on hardware).
    /// </summary>
    internal static string? BuildTriggerSelect(TriggerSettings current, TriggerUpdate update)
    {
        bool holdChange = update.HoldType is not null || update.HoldValue is not null || update.HoldValue2 is not null;
        bool videoChange = update.VideoStandard is not null || update.VideoSync is not null || update.VideoLine is not null || update.VideoField is not null;
        if (update.Type is null && update.Source is null && !holdChange && !videoChange)
            return null;

        string type = update.Type is { } t ? Checked(t, TriggerTypes) : current.Type;
        bool typeChanged = type != current.Type;
        if (videoChange && type != "TV") throw new ArgumentException("Video settings need the video trigger.");
        if (holdChange && type is "WIN" or "PA" or "TV") throw new ArgumentException($"A {type} trigger has no time condition.");

        // Pattern: the channels and logic are set with TRPA; TRSE only selects the type.
        if (type == "PA") return typeChanged ? "TRSE PA" : null;

        string source = update.Source?.Trim().ToUpperInvariant() ?? current.Source;
        if (!System.Text.RegularExpressions.Regex.IsMatch(source, "^(C[1-4]|EX|EX5|LINE)$"))
            throw new ArgumentException($"Invalid trigger source '{source}'.");
        if (type != "EDGE" && !source.StartsWith('C'))
            throw new ArgumentException("LINE and external sources work only with the edge trigger.");

        // Window: the upper and lower limits are the trigger levels TRLV and TRLV2.
        if (type == "WIN") return $"TRSE WIN,SR,{source}";
        if (type == "TV") return BuildVideo(source, typeChanged ? null : current.Video, update);

        if (!typeChanged && !holdChange)
            return $"TRSE {type},SR,{source}";

        var allowed = HoldTypes[type];
        string hold = update.HoldType is { } h ? h.Trim().ToUpperInvariant()
            : !typeChanged && current.HoldType is { } ch ? ch
            : allowed[0];
        if (!allowed.Contains(hold))
            throw new ArgumentException($"Condition '{hold}' is not valid for a {type} trigger.");
        if (hold == "OFF") return $"TRSE {type},SR,{source},HT,OFF";

        double hv = update.HoldValue ?? (!typeChanged ? current.HoldValue : null) ?? DefaultHoldValue(type);
        var command = $"TRSE {type},SR,{source},HT,{hold},HV,{ScpiValue.Format(hv, "S")}";
        if (hold is "P2" or "P1" or "I2" or "I1")
        {
            double hv2 = update.HoldValue2 ?? (!typeChanged ? current.HoldValue2 : null) ?? hv * 2;
            // Raising only the lower limit past the upper one moves the upper limit along.
            if (update.HoldValue2 is null && hv2 <= hv) hv2 = hv * 2;
            if (hv2 <= hv) throw new ArgumentException("The upper time limit must be larger than the lower one.");
            command += $",HV2,{ScpiValue.Format(hv2, "S")}";
        }
        return command;
    }

    /// <summary>Line counts per standard: field 1 and field 2 for interlaced standards.</summary>
    private static readonly Dictionary<string, int[]> VideoLines = new()
    {
        ["NTSC"] = [263, 262],
        ["PAL"] = [313, 312],
        ["720P/50"] = [750],
        ["720P/60"] = [750],
        ["1080P/50"] = [1125],
        ["1080P/60"] = [1125],
        ["1080I/50"] = [563, 562],
        ["1080I/60"] = [563, 562],
    };

    /// <summary>TRSE TV,SR,C1,STAN,PAL,SYNC,SELECT,LINE,300,FLD,2 — unspecified values keep the current ones.</summary>
    private static string BuildVideo(string source, VideoTrigger? current, TriggerUpdate u)
    {
        string standard = (u.VideoStandard ?? current?.Standard ?? "NTSC").Trim().ToUpperInvariant();
        if (!VideoLines.TryGetValue(standard, out var lines)) throw new ArgumentException($"Unsupported video standard '{standard}'.");
        string sync = (u.VideoSync ?? current?.Sync ?? "ANY").Trim().ToUpperInvariant();
        if (sync is not ("ANY" or "SELECT")) throw new ArgumentException("Video sync must be ANY or SELECT.");

        var command = $"TRSE TV,SR,{source},STAN,{standard},SYNC,{sync}";
        if (sync == "ANY") return command;

        bool interlaced = lines.Length == 2;
        int field = interlaced ? u.VideoField ?? current?.Field ?? 1 : 1;
        if (field is < 1 or > 2) throw new ArgumentException("The video field must be 1 or 2.");
        int max = lines[interlaced ? field - 1 : 0];
        int line = u.VideoLine ?? Math.Min(current?.Line ?? 1, max);
        if (line < 1 || line > max) throw new ArgumentException($"Line must be 1–{max} for {standard}{(interlaced ? $" field {field}" : "")}.");
        return command + $",LINE,{line}" + (interlaced ? $",FLD,{field}" : "");
    }

    /// <summary>TRPA? → "C1,H,C2,L,STATE,AND".</summary>
    internal static (Dictionary<string, string> States, string Logic) ParsePattern(string response)
    {
        var list = ScpiValue.ParseList(response);
        var states = new Dictionary<string, string>();
        for (int i = 0; i + 1 < list.Length; i += 2)
            if (list[i].StartsWith('C')) states[list[i].ToUpperInvariant()] = list[i + 1].ToUpperInvariant();
        return (states, (FindPair(list, "STATE") ?? "AND").ToUpperInvariant());
    }

    internal static string BuildPattern((Dictionary<string, string> States, string Logic) current, TriggerUpdate u)
    {
        var states = new Dictionary<string, string>(current.States);
        foreach (var (ch, state) in u.PatternStates ?? new Dictionary<string, string>())
        {
            var s = state.Trim().ToUpperInvariant();
            if (s is not ("H" or "L" or "X")) throw new ArgumentException($"Pattern state must be H, L or X, not '{state}'.");
            states[ch.Trim().ToUpperInvariant()] = s;
        }
        string logic = Checked(u.PatternLogic ?? current.Logic, PatternLogics);
        return "TRPA " + string.Join(",", states.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key},{kv.Value}")) + $",STATE,{logic}";
    }


    private static double DefaultHoldValue(string type) => type switch
    {
        "EDGE" => 1e-6,
        "DROP" => 1e-3,
        _ => 1e-6,
    };

    /// <summary>Sets the trigger level to the middle of the signal (edge trigger only).</summary>
    public Task SetTriggerLevelTo50PercentAsync(CancellationToken ct = default) => WriteAllAsync(["SET50"], ct);

    public Task AutoSetupAsync(CancellationToken ct = default) => WriteAllAsync(["ASET"], ct);

    // ---------- virtual front panel ----------

    /// <summary>Front-panel buttons that can be pressed ($$SY_FP n,1); Default (13) and Print (25) are left out.</summary>
    public static readonly IReadOnlySet<int> PanelButtons = new HashSet<int>
    {
        0, 1, 2, 3, 4, 5, 6, // Menu on/off and the six soft keys below the screen
        11, 12, 17, 18, 19, 20, 22, 23, 24, 26, 27, 28, 29, 31, 32, 39, 40, 47, 48, 49,
    };

    /// <summary>Front-panel knobs: turned with $$SY_FP n,±1 (one detent), pushed with $$SY_FP n,0.</summary>
    public static readonly IReadOnlySet<int> PanelKnobs = new HashSet<int> { 7, 10, 15, 16, 35, 36, 43, 44 };

    /// <summary>
    /// Presses a front-panel key or turns a knob by one detent (direction −1/+1; 0 pushes the knob) with the
    /// undocumented $$SY_FP command (key codes from klumw/sdsremote, verified on an SDS1202X-E with FW 1.3.27).
    /// </summary>
    public Task PressPanelKeyAsync(int key, int value = 1, CancellationToken ct = default)
    {
        bool valid = PanelKnobs.Contains(key) ? value is -1 or 0 or 1 : PanelButtons.Contains(key) && value == 1;
        if (!valid) throw new ArgumentException($"Unknown front-panel key {key},{value}.");
        return WriteAllAsync([$"$$SY_FP {key},{value}"], ct);
    }

    // ---------- pass/fail mask test ----------

    /// <summary>Mask tolerance range in divisions (programming guide).</summary>
    public const double MinMask = 0.04, MaxMask = 4;

    public Task<PassFailState> GetPassFailAsync(CancellationToken ct = default) => Locked(() => ReadPassFailAsync(ct));

    /// <summary>
    /// Changes the pass/fail test. The guide's PFDS TEST,… is ignored on FW 1.3.27; the test is enabled with the
    /// undocumented PFEN, and the scope only accepts PFST, PFCM and PFOP while it is enabled, so changing the
    /// tolerance, creating a mask or starting the test enables it first. Disabling stops the test before PFEN OFF.
    /// </summary>
    public Task<PassFailState> UpdatePassFailAsync(PassFailUpdate update, CancellationToken ct = default)
    {
        if (update.XMask is { } x && x is < MinMask or > MaxMask || update.YMask is { } y && y is < MinMask or > MaxMask)
            throw new ArgumentException($"The mask tolerance must be {MinMask}–{MaxMask} div.");
        var source = update.Source?.Trim().ToUpperInvariant();
        if (source is not null && !(source.Length == 2 && source[0] == 'C' && source[1] - '0' is var n && n >= 1 && n <= ChannelCount))
            throw new ArgumentException($"Unknown pass/fail source {update.Source}.");

        return Locked(async () =>
        {
            // *OPC? after each command: the next one is only sent once the scope has taken this one (the
            // connection already leaves a short gap after a command, see ScpiConnection.CommandGap).
            async Task Send(string command)
            {
                await _scpi.WriteAsync(command, ct);
                await _scpi.QueryAsync("*OPC?", ct);
            }
            string OnOff(bool on) => on ? "ON" : "OFF";

            // PFST is ignored too while the test is disabled; Enabled: false (applied last) switches it off again.
            bool enable = update.Enabled == true || update.CreateMask || update.Running == true
                || update.XMask is not null || update.YMask is not null;
            if (enable) await Send("PFEN ON");
            if (source is not null) await Send($"PFSC {source}");
            if (update.XMask is not null || update.YMask is not null)
            {
                var current = await ReadPassFailAsync(ct);
                double mx = update.XMask ?? current.XMask, my = update.YMask ?? current.YMask;
                await Send($"PFST XMASK,{ScpiValue.Format(Math.Round(mx, 2), "")},YMASK,{ScpiValue.Format(Math.Round(my, 2), "")}");
            }
            if (update.StopOnFail is { } stop) await Send($"PFFS {OnOff(stop)}");
            if (update.Beep is { } beep) await Send($"PFBF {OnOff(beep)}");
            if (update.ShowMessage is { } show) await Send($"PFDS {OnOff(show)}");
            if (update.CreateMask) await Send("PFCM");
            if (update.Running is { } run) await Send($"PFOP {OnOff(run)}");
            if (update.Enabled == false)
            {
                await Send("PFOP OFF");
                await Send("PFEN OFF");
            }
            return await ReadPassFailAsync(ct);
        });
    }

    private async Task<PassFailState> ReadPassFailAsync(CancellationToken ct)
    {
        async Task<bool> On(string query) => ScpiValue.ParseBool(await _scpi.QueryAsync(query, ct));
        bool enabled = await On("PFEN?");
        bool running = await On("PFOP?");
        string source = ScpiValue.StripHeader(await _scpi.QueryAsync("PFSC?", ct)).ToUpperInvariant();
        var mask = ScpiValue.ParseList(await _scpi.QueryAsync("PFST?", ct));
        bool stop = await On("PFFS?");
        bool beep = await On("PFBF?");
        bool show = await On("PFDS?");
        var counts = ScpiValue.ParseList(await _scpi.QueryAsync("PFDD?", ct));
        double Number(string[] list, string key) => FindPair(list, key) is { } v ? ScpiValue.ParseNumber(v) : 0;
        long fail = (long)Number(counts, "FAIL"), pass = (long)Number(counts, "PASS"), total = (long)Number(counts, "TOTAL");
        // Before a test has been started, FW 1.3.27 can report leftovers such as FAIL,865,PASS,-865,TOTAL,0.
        if (pass < 0 || fail + pass != total) (fail, pass, total) = (0, 0, 0);
        return new PassFailState(enabled, running, source, Number(mask, "XMASK"), Number(mask, "YMASK"), stop, beep, show,
            fail, pass, total);
    }

    // ---------- history ----------

    /// <summary>Switches history mode on or off. While on, acquisition stops and WF? returns the selected frame.</summary>
    public Task<HistoryState> SetHistoryAsync(bool enabled, CancellationToken ct = default) => Locked(async () =>
    {
        await _scpi.WriteAsync($"HSMD {(enabled ? "ON" : "OFF")}", ct);
        // Right after switching on, FRAM? reports the number of stored frames (the last one is selected).
        _historyFrameCount = enabled ? (int)ScpiValue.ParseNumber(await _scpi.QueryAsync("FRAM?", ct)) : null;
        return await ReadHistoryAsync(ct);
    });

    public Task<HistoryState> SetHistoryFrameAsync(int frame, CancellationToken ct = default) => Locked(async () =>
    {
        if (frame < 1) throw new ArgumentOutOfRangeException(nameof(frame), "Frames start at 1.");
        await _scpi.WriteAsync($"FRAM {frame}", ct);
        return await ReadHistoryAsync(ct);
    });

    /// <summary>Reads every history frame (decimated to about <paramref name="maxPoints"/>) and passes it to <paramref name="onFrame"/>.</summary>
    public Task ExportHistoryAsync(int maxPoints, Func<int, string?, WaveformFrame, Task> onFrame, CancellationToken ct = default) =>
        Locked(async () =>
        {
            var state = await ReadHistoryAsync(ct);
            if (!state.Enabled) throw new InvalidOperationException("History mode is off.");
            try
            {
                for (int i = 1; i <= state.FrameCount; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    await _scpi.WriteAsync($"FRAM {i}", ct);
                    var time = FormatFrameTime(await _scpi.QueryAsync("FTIM?", ct));
                    await onFrame(i, time, await ReadWaveformsAsync(maxPoints, ct));
                }
            }
            finally
            {
                await _scpi.WriteAsync($"FRAM {state.Frame}", CancellationToken.None);
            }
            return 0;
        });

    // ---------- setups ----------

    /// <summary>The complete panel setup (PNSU?). FW 1.3.27 returns XML; other firmware may return binary data.</summary>
    public Task<byte[]> GetSetupAsync(CancellationToken ct = default) => Locked(() => _scpi.QueryBlockAsync("PNSU?", ct));

    public Task LoadSetupAsync(byte[] setup, CancellationToken ct = default)
    {
        if (setup.Length is 0 or > 4_000_000) throw new ArgumentException("Not a scope setup file.");
        return Locked(async () =>
        {
            await _scpi.WriteBlockAsync("PNSU ", setup, ct);
            await _scpi.QueryAsync("*OPC?", ct);
            _historyFrameCount = null;
            return 0;
        });
    }

    /// <summary>Stores the setup in internal memory 1–20 (overwrites that slot).</summary>
    public Task SaveSetupToMemoryAsync(int slot, CancellationToken ct = default)
    {
        if (slot is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(slot), "Memory slots are 1..20.");
        return WriteAllAsync([$"*SAV {slot}"], ct);
    }

    /// <summary>Recalls internal memory 1–20, or the default setup for slot 0.</summary>
    public Task RecallSetupFromMemoryAsync(int slot, CancellationToken ct = default)
    {
        if (slot is < 0 or > 20) throw new ArgumentOutOfRangeException(nameof(slot), "Memory slots are 0..20.");
        return Locked(async () =>
        {
            await _scpi.WriteAsync($"*RCL {slot}", ct);
            await _scpi.QueryAsync("*OPC?", ct);
            _historyFrameCount = null;
            return 0;
        });
    }

    /// <summary>Sends any SCPI command; queries (containing '?') return the response line.</summary>
    public Task<string?> SendRawAsync(string command, CancellationToken ct = default) => Locked(async () =>
        command.Contains('?')
            ? await _scpi.QueryAsync(command, ct)
            : await WriteThenNull(command));

    private async Task<string?> WriteThenNull(string command)
    {
        await _scpi.WriteAsync(command);
        return null;
    }

    private Task WriteAllAsync(IEnumerable<string> commands, CancellationToken ct) => Locked(async () =>
    {
        foreach (var command in commands) await _scpi.WriteAsync(command, ct);
        return 0;
    });

    private async Task<T> Locked<T>(Func<Task<T>> action)
    {
        await _lock.WaitAsync();
        try { return await action(); }
        finally { _lock.Release(); }
    }

    private void CheckChannel(int channel)
    {
        if (channel < 1 || channel > ChannelCount)
            throw new ArgumentOutOfRangeException(nameof(channel), $"Channel must be 1..{ChannelCount}.");
    }

    private static string? FindPair(string[] list, string key)
    {
        for (int i = 0; i + 1 < list.Length; i++)
            if (list[i].Equals(key, StringComparison.OrdinalIgnoreCase)) return list[i + 1];
        return null;
    }

    private static string Checked(string value, HashSet<string> allowed)
    {
        var upper = value.Trim().ToUpperInvariant();
        return allowed.Contains(upper) ? upper : throw new ArgumentException($"Invalid value '{value}'.");
    }

    public async ValueTask DisposeAsync()
    {
        await _lock.WaitAsync();
        try
        {
            // Leave the scope's measurements as the app found them.
            if (_gateOn)
            {
                try { await _scpi.WriteAsync("MEGS OFF"); }
                catch (Exception e) when (e is IOException or TimeoutException or System.Net.Sockets.SocketException) { }
            }
            await _scpi.DisposeAsync();
        }
        finally { _lock.Release(); }
    }
}
