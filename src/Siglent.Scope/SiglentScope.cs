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
    private static readonly HashSet<string> TriggerCouplings = ["AC", "DC", "HFREJ", "LFREJ"];

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
        var channels = new List<ChannelSettings>();
        for (int ch = 1; ch <= ChannelCount; ch++)
        {
            channels.Add(new ChannelSettings(
                ch,
                ScpiValue.ParseBool(await Q($"C{ch}:TRA?")),
                ScpiValue.ParseNumber(await Q($"C{ch}:VDIV?")),
                ScpiValue.ParseNumber(await Q($"C{ch}:OFST?")),
                ScpiValue.StripHeader(await Q($"C{ch}:CPL?")),
                ScpiValue.ParseNumber(await Q($"C{ch}:ATTN?"))));
        }

        // TRSE? → "EDGE,SR,C1,HT,OFF"
        var trse = ScpiValue.ParseList(await Q("TRSE?"));
        string type = trse[0];
        string source = FindPair(trse, "SR") ?? "C1";
        bool hasLevel = source.StartsWith('C') || source.StartsWith("EX");
        var trigger = new TriggerSettings(
            ScpiValue.StripHeader(await Q("TRMD?")),
            type,
            source,
            hasLevel ? ScpiValue.StripHeader(await Q($"{source}:TRSL?")) : null,
            hasLevel ? ScpiValue.ParseNumber(await Q($"{source}:TRLV?")) : null,
            hasLevel ? ScpiValue.StripHeader(await Q($"{source}:TRCP?")) : null);

        return new ScopeSettings(
            ScpiValue.ParseNumber(await Q("TDIV?")),
            -ScpiValue.ParseNumber(await Q("TRDL?")),
            ScpiValue.ParseNumber(await Q("SARA?")),
            ScpiValue.StripHeader(await Q("SAST?")),
            trigger,
            channels);

        Task<string> Q(string query) => _scpi.QueryAsync(query, ct);
    });

    /// <summary>
    /// Reads the displayed waveform of every enabled channel. Long memories are decimated on the
    /// scope (WFSU SP) so that at most about <paramref name="maxPoints"/> points cross the network.
    /// </summary>
    public Task<WaveformFrame> GetFrameAsync(int maxPoints = 2000, CancellationToken ct = default) =>
        Locked(() => ReadWaveformsAsync(Math.Max(1, maxPoints), ct));

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
    private async Task<WaveformFrame> ReadWaveformsAsync(int? maxPoints, CancellationToken ct)
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

            int step = maxPoints is { } max ? (int)Math.Max(1, Math.Ceiling(total / (double)max)) : 1;
            long count = (total + step - 1) / step;
            // NP must be non-zero: with NP 0 the scope ignores SP.
            await _scpi.WriteAsync($"WFSU SP,{step},NP,{count},FP,0", ct);
            var data = await _scpi.QueryBlockAsync($"C{ch}:WF? DAT2", ct);

            channels.Add(new ChannelWaveform(
                ch, vdiv, ofst, t0, step / sara, total,
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
        if (update.Source is { } newSource)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(newSource, "^(C[1-4]|EX|EX5|LINE)$"))
                throw new ArgumentException($"Invalid trigger source '{newSource}'.");
            await _scpi.WriteAsync($"TRSE EDGE,SR,{newSource},HT,OFF", ct);
        }
        var source = FindPair(ScpiValue.ParseList(await _scpi.QueryAsync("TRSE?", ct)), "SR") ?? "C1";
        if (update.Slope is { } slope) await _scpi.WriteAsync($"{source}:TRSL {Checked(slope, Slopes)}", ct);
        if (update.Coupling is { } cpl) await _scpi.WriteAsync($"{source}:TRCP {Checked(cpl, TriggerCouplings)}", ct);
        if (update.Level is { } level) await _scpi.WriteAsync($"{source}:TRLV {ScpiValue.Format(level, "V")}", ct);
        if (update.Mode is { } mode) await _scpi.WriteAsync($"TRMD {Checked(mode, TriggerModes)}", ct);
        return 0;
    });

    public Task AutoSetupAsync(CancellationToken ct = default) => WriteAllAsync(["ASET"], ct);

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
