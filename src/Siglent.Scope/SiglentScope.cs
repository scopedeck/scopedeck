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
    private static readonly HashSet<string> TriggerTypes = ["EDGE", "GLIT", "SLEW", "INTV", "RUNT", "DROP", "TV"];
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
        if (trigger.Source.StartsWith('C') || trigger.Source.StartsWith("EX"))
        {
            var src = trigger.Source;
            trigger = trigger with
            {
                Slope = ScpiValue.StripHeader(await Q($"{src}:TRSL?")),
                Level = ScpiValue.ParseNumber(await Q($"{src}:TRLV?")),
                Coupling = ScpiValue.StripHeader(await Q($"{src}:TRCP?")),
                Level2 = trigger.Type is "SLEW" or "RUNT" && src.StartsWith('C')
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
            Level2: null);
    }

    private int _averages = 16;

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
    public Task<WaveformFrame> GetFrameAsync(int maxPoints = 2000, double start = 0, double end = 1, CancellationToken ct = default)
    {
        if (!(start >= 0 && end <= 1 && start < end)) throw new ArgumentException("The range must satisfy 0 ≤ start < end ≤ 1.");
        return Locked(() => ReadWaveformsAsync(Math.Max(1, maxPoints), ct, start, end));
    }

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

    /// <param name="maxPoints">Decimate to about this many points on the scope (WFSU SP); null reads every sample.</param>
    private async Task<WaveformFrame> ReadWaveformsAsync(int? maxPoints, CancellationToken ct, double start = 0, double end = 1)
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
            int step = maxPoints is { } max ? (int)Math.Max(1, Math.Ceiling(span / (double)max)) : 1;
            long count = (span + step - 1) / step;
            // NP must be non-zero: with NP 0 the scope ignores SP.
            await _scpi.WriteAsync($"WFSU SP,{step},NP,{count},FP,{first}", ct);
            var data = await _scpi.QueryBlockAsync($"C{ch}:WF? DAT2", ct);

            channels.Add(new ChannelWaveform(
                ch, vdiv, ofst, t0 + first / sara, step / sara, total,
                WaveformDecoder.ToVolts(data, vdiv, ofst)));
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
        if (BuildTriggerSelect(current, update) is { } trse) await _scpi.WriteAsync(trse, ct);
        var source = update.Source?.Trim().ToUpperInvariant() ?? current.Source;
        if (update.Slope is { } slope) await _scpi.WriteAsync($"{source}:TRSL {Checked(slope, Slopes)}", ct);
        if (update.Coupling is { } cpl) await _scpi.WriteAsync($"{source}:TRCP {Checked(cpl, TriggerCouplings)}", ct);
        if (update.Level is { } level) await _scpi.WriteAsync($"{source}:TRLV {ScpiValue.Format(level, "V")}", ct);
        if (update.Level2 is { } level2) await _scpi.WriteAsync($"{source}:TRLV2 {ScpiValue.Format(level2, "V")}", ct);
        if (update.Mode is { } mode) await _scpi.WriteAsync($"TRMD {Checked(mode, TriggerModes)}", ct);
        return 0;
    });

    /// <summary>
    /// Builds the TRSE command for a trigger change, or null if type, source and condition are unchanged.
    /// A source-only change sends just the type and source, which keeps the condition (verified on hardware).
    /// </summary>
    internal static string? BuildTriggerSelect(TriggerSettings current, TriggerUpdate update)
    {
        if (update.Type is null && update.Source is null && update.HoldType is null && update.HoldValue is null && update.HoldValue2 is null)
            return null;

        string type = update.Type is { } t ? Checked(t, TriggerTypes) : current.Type;
        string source = update.Source?.Trim().ToUpperInvariant() ?? current.Source;
        if (!System.Text.RegularExpressions.Regex.IsMatch(source, "^(C[1-4]|EX|EX5|LINE)$"))
            throw new ArgumentException($"Invalid trigger source '{source}'.");
        if (type != "EDGE" && !source.StartsWith('C'))
            throw new ArgumentException("LINE and external sources work only with the edge trigger.");

        bool typeChanged = type != current.Type;
        if (!typeChanged && update.HoldType is null && update.HoldValue is null && update.HoldValue2 is null)
            return $"TRSE {type},SR,{source}";
        if (type == "TV")
            throw new ArgumentException("Set up the video trigger on the scope.");

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

    private static double DefaultHoldValue(string type) => type switch
    {
        "EDGE" => 1e-6,
        "DROP" => 1e-3,
        _ => 1e-6,
    };

    /// <summary>Sets the trigger level to the middle of the signal (edge trigger only).</summary>
    public Task SetTriggerLevelTo50PercentAsync(CancellationToken ct = default) => WriteAllAsync(["SET50"], ct);

    public Task AutoSetupAsync(CancellationToken ct = default) => WriteAllAsync(["ASET"], ct);

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
        try { await _scpi.DisposeAsync(); }
        finally { _lock.Release(); }
    }
}
