using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Siglent.Scope;

namespace Siglent.Simulator;

/// <summary>
/// A fake SDS1202X-E that speaks the SCPI subset used by <see cref="SiglentScope"/> on a TCP port.
/// Response framing mirrors a real unit on FW 1.3.27 (block "\n\n" tail, RGB565 BMP plus a trailing byte,
/// XML panel setups). C1 carries a 1 kHz 3 Vpp sine; C2 a 1 kHz 0–3.3 V square wave.
/// </summary>
public sealed class FakeScopeServer : IAsyncDisposable
{
    private const double MaxSampleRate = 1e9;
    private const int HistoryFrames = 40;

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly Lock _state = new();
    private readonly Random _noise = new(1);
    private readonly Dictionary<int, Settings> _memories = [];

    private bool _headers = true;
    private int _sp, _np, _fp;
    private bool _history;
    private string _video = "STAN,NTSC,SYNC,ANY";
    private string _pattern = "C1,X,C2,X,STATE,AND";
    private int _frame;
    private Settings _s = Settings.Default;

    private sealed record Channel(bool On, double Vdiv, double Ofst, double Attn, string Cpl = "D1M", bool Bwl = false, bool Inv = false, string Unit = "V", double Skew = 0, double TrigLevel = 0);

    /// <summary>Everything a panel setup (PNSU / *SAV) stores.</summary>
    private sealed record Settings(
        double Tdiv, double Trdl, string Trmd,
        string TrigType, string TrigSource, string HoldType, double HoldValue, double HoldValue2,
        string TrigSlope, string TrigCoupling, double TrigLevel2,
        string AcqMode, int Averages, string MemoryDepth, bool SinX,
        Channel[] Channels)
    {
        public static Settings Default => new(
            200e-6, 0, "AUTO", "EDGE", "C1", "OFF", 0, 0, "POS", "DC", -0.5,
            "SAMPLING", 16, "700K", true, [new(true, 1, 0, 1), new(true, 1, -1.5, 1)]);
    }

    public int Port { get; }

    public FakeScopeServer(int port = ScpiConnection.DefaultPort, IPAddress? address = null)
    {
        _listener = new TcpListener(address ?? IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoop();
    }

    private async Task AcceptLoop()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Serve(client);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task Serve(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        var reader = new StreamReader(stream, Encoding.ASCII);
        try
        {
            while (await reader.ReadLineAsync(_cts.Token) is { } line)
            {
                // "PNSU #9<len><data>": the XML data contains newlines, so read the rest of the block explicitly.
                var block = Regex.Match(line, @"^PNSU #9(\d{9})");
                if (block.Success)
                {
                    int length = int.Parse(block.Groups[1].Value);
                    var data = line[block.Length..];
                    if (data.Length < length)
                    {
                        // ReadLine consumed a newline that belongs to the data; read the rest, then the terminator.
                        var rest = new char[length - data.Length - 1];
                        await reader.ReadBlockAsync(rest, _cts.Token);
                        data += "\n" + new string(rest);
                        await reader.ReadLineAsync(_cts.Token);
                    }
                    lock (_state) LoadSetup(data[..length]);
                    continue;
                }
                var response = Handle(line.Trim());
                if (response is not null) await stream.WriteAsync(response, _cts.Token);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException) { }
    }

    private byte[]? Handle(string line)
    {
        if (line.Length == 0) return null;
        int space = line.IndexOf(' ');
        string head = (space < 0 ? line : line[..space]).ToUpperInvariant();
        string arg = space < 0 ? "" : line[(space + 1)..].Trim();
        var mead = Regex.Match(head, @"^C(\d)-C(\d):MEAD\?$");
        if (mead.Success) lock (_state) return Delay(mead.Groups[1].Value[0] - '0', mead.Groups[2].Value[0] - '0', arg.ToUpperInvariant());
        int ch = 0;
        if (head.Length > 3 && head[0] == 'C' && char.IsDigit(head[1]) && head[2] == ':')
        {
            ch = head[1] - '0';
            head = head[3..];
        }
        if (ch > _s.Channels.Length) return null;

        lock (_state)
        {
            CountPassFail();
            switch (head)
            {
                case "*IDN?": return Text("Siglent Technologies,SDS1202X-E,SIMULATOR00001,1.3.27");
                case "*OPC?": return Text("1");
                case "*SAV": _memories[int.Parse(arg)] = _s with { Channels = [.. _s.Channels] }; return null;
                case "*RCL":
                    int slot = int.Parse(arg);
                    if (slot == 0) _s = Settings.Default;
                    else if (_memories.TryGetValue(slot, out var saved)) _s = saved with { Channels = [.. saved.Channels] };
                    return null;
                case "CHDR": _headers = !arg.Equals("OFF", StringComparison.OrdinalIgnoreCase); return null;
                case "CHDR?": return Line("CHDR", _headers ? "SHORT" : "OFF");
                case "TDIV": _s = _s with { Tdiv = ParseTime(arg) }; return null;
                case "TDIV?": return Line("TDIV", E(_s.Tdiv) + "S");
                case "TRDL": _s = _s with { Trdl = ParseTime(arg) }; return null;
                case "TRDL?": return Line("TRDL", E(_s.Trdl) + "S");
                case "SARA?": return Line("SARA", E(SampleRate) + "Sa/s");
                case "SANU?": return Line("SANU", E(Points) + "pts");
                case "SAST?": return Line("SAST", _history || _s.Trmd == "STOP" ? "Stop" : _s.Trmd == "AUTO" ? "Auto" : "Trig'd");
                case "TRMD": _s = _s with { Trmd = arg.ToUpperInvariant() }; return null;
                case "TRMD?": return Line("TRMD", _s.Trmd);
                case "STOP": _s = _s with { Trmd = "STOP" }; return null;
                case "ASET": _s = Settings.Default; return null;
                case "SET50" when _s.TrigSource is "C1" or "C2": SetChannel(_s.TrigSource[1] - '0', _s.Channels[_s.TrigSource[1] - '1'] with { TrigLevel = _s.TrigSource == "C2" ? 1.65 : 0 }); return null;
                case "TRSE": SetTrigger(arg); return null;
                case "TRSE?": return Line("TRSE", TriggerText());
                case "TRPA": _pattern = arg.ToUpperInvariant().Replace(" ", ""); return null;
                case "TRPA?": return Line("TRPA", _pattern);
                case "ACQW":
                    var acqw = arg.Split(',', StringSplitOptions.TrimEntries);
                    _s = _s with { AcqMode = acqw[0].ToUpperInvariant(), Averages = acqw.Length > 1 ? int.Parse(acqw[1]) : _s.Averages };
                    return null;
                case "ACQW?": return Line("ACQW", _s.AcqMode == "AVERAGE" ? $"AVERAGE,{_s.Averages}" : _s.AcqMode);
                case "AVGA": _s = _s with { Averages = int.Parse(arg), AcqMode = "AVERAGE" }; return null;
                // Mirrors FW 1.3.27: the AVGA? query (and AVGA) switches the scope into average mode.
                case "AVGA?":
                    _s = _s with { AcqMode = "AVERAGE" };
                    return Line("AVGA", _s.Averages.ToString(CultureInfo.InvariantCulture));
                case "MSIZ": _s = _s with { MemoryDepth = arg.ToUpperInvariant() }; return null;
                case "MSIZ?": return Line("MSIZ", _s.MemoryDepth);
                case "SXSA": _s = _s with { SinX = arg.Equals("ON", StringComparison.OrdinalIgnoreCase) }; return null;
                case "SXSA?": return Line("SXSA", _s.SinX ? "ON" : "OFF");
                case "BWL":
                    var bwl = arg.Split(',', StringSplitOptions.TrimEntries);
                    var channels = _s.Channels.ToArray();
                    for (int i = 0; i + 1 < bwl.Length; i += 2)
                        channels[bwl[i][1] - '1'] = channels[bwl[i][1] - '1'] with { Bwl = bwl[i + 1].Equals("ON", StringComparison.OrdinalIgnoreCase) };
                    _s = _s with { Channels = channels };
                    return null;
                case "BWL?": return Line("BWL", string.Join(',', _s.Channels.Select((c, i) => $"C{i + 1},{(c.Bwl ? "ON" : "OFF")}")));
                case "HSMD":
                    // History is not available in average mode; the real scope ignores HSMD ON there.
                    _history = arg.Equals("ON", StringComparison.OrdinalIgnoreCase) && _s.AcqMode != "AVERAGE";
                    _frame = HistoryFrames;
                    return null;
                case "HSMD?": return Line("HSMD", _history ? "ON" : "OFF");
                case "FRAM": if (_history) _frame = Math.Clamp(int.Parse(arg), 1, HistoryFrames); return null;
                case "FRAM?": return Line("FRAM", (_history ? _frame : 0).ToString(CultureInfo.InvariantCulture));
                // The real scope only answers FTIM? in history mode.
                case "FTIM?": return _history ? Line("FTIM", $"00: 12: {10 + _frame:00}. {_frame * 1234:000000}") : null;
                case "PNSU?": return Block("PNSU", Encoding.ASCII.GetBytes(SetupXml()));
                // Gate commands are accepted; like FW 1.3.27, MEGS? gets no answer.
                case "MEGS" or "MEGA" or "MEGB": return null;
                // Front-panel keys: CH1/CH2 work like on the scope, the others are accepted without effect.
                case "$$SY_FP": PressKey(arg); return null;
                // Pass/fail: like FW 1.3.27, PFCM and PFOP only work while the test is enabled (PFEN, not in the
                // guide), PFDS? reports the message display, and starting the test resets the counters.
                case "PFEN": _pf.Enabled = OnArg(arg); if (!_pf.Enabled) _pf.Running = false; return null;
                case "PFEN?": return Line("PFEN", _pf.Enabled ? "ON" : "OFF");
                case "PFDS": if (arg is "ON" or "OFF") _pf.Message = OnArg(arg); return null;
                case "PFDS?": return Line("PFDS", _pf.Message ? "ON" : "OFF");
                case "PFSC": _pf.Source = arg.ToUpperInvariant(); return null;
                case "PFSC?": return Line("PFSC", _pf.Source);
                case "PFST":
                    if (!_pf.Enabled) return null;
                    var pfst = arg.Split(',', StringSplitOptions.TrimEntries);
                    (_pf.X, _pf.Y) = (double.Parse(pfst[1], CultureInfo.InvariantCulture), double.Parse(pfst[3], CultureInfo.InvariantCulture));
                    return null;
                case "PFST?": return Line("PFST", FormattableString.Invariant($"XMASK,{_pf.X:0.00},YMASK,{_pf.Y:0.00}"));
                case "PFFS": _pf.StopOnFail = OnArg(arg); return null;
                case "PFFS?": return Line("PFFS", _pf.StopOnFail ? "ON" : "OFF");
                case "PFBF": _pf.Beep = OnArg(arg); return null;
                case "PFBF?": return Line("PFBF", _pf.Beep ? "ON" : "OFF");
                case "PFCM": if (_pf.Enabled) _pf.Mask = SourceChannel(); return null;
                case "PFOP":
                    if (!_pf.Enabled) return null;
                    bool start = OnArg(arg);
                    if (start && !_pf.Running) (_pf.Fail, _pf.Pass, _pf.At) = (0, 0, DateTime.UtcNow);
                    _pf.Running = start;
                    return null;
                case "PFOP?": return Line("PFOP", _pf.Running ? "ON" : "OFF");
                case "PFDD?":
                    return Line("PFDD", FormattableString.Invariant($"FAIL,{_pf.Fail},PASS,{_pf.Pass},TOTAL,{_pf.Fail + _pf.Pass}"));
                case "WFSU":
                    var p = arg.Split(',', StringSplitOptions.TrimEntries);
                    for (int i = 0; i + 1 < p.Length; i += 2)
                    {
                        int v = int.Parse(p[i + 1], CultureInfo.InvariantCulture);
                        switch (p[i].ToUpperInvariant()) { case "SP": _sp = v; break; case "NP": _np = v; break; case "FP": _fp = v; break; }
                    }
                    return null;
                case "WFSU?": return Line("WFSU", $"SP,{_sp},NP,{_np},FP,{_fp}");
                case "SCDP": return Screenshot();
            }

            if (ch == 0) return null;
            // Like FW 1.3.27: no answer to slope queries in window, pattern and video mode, nor to level and
            // coupling queries in pattern and video mode.
            if (head == "TRSL?" && _s.TrigType is "WIN" or "PA" or "TV") return null;
            if (head is "TRLV?" or "TRLV2?" or "TRCP?" && _s.TrigType is "PA" or "TV") return null;
            var c = _s.Channels[ch - 1];
            string prefix = $"C{ch}:";
            switch (head)
            {
                case "TRA": SetChannel(ch, c with { On = arg.Equals("ON", StringComparison.OrdinalIgnoreCase) }); return null;
                case "TRA?": return Line(prefix + "TRA", c.On ? "ON" : "OFF");
                case "VDIV": SetChannel(ch, c with { Vdiv = ParseVolts(arg) }); return null;
                case "VDIV?": return Line(prefix + "VDIV", E(c.Vdiv) + "V");
                case "OFST": SetChannel(ch, c with { Ofst = ParseVolts(arg) }); return null;
                case "OFST?": return Line(prefix + "OFST", E(c.Ofst) + "V");
                case "CPL": SetChannel(ch, c with { Cpl = arg.ToUpperInvariant() }); return null;
                case "CPL?": return Line(prefix + "CPL", c.Cpl);
                case "ATTN": SetChannel(ch, c with { Attn = double.Parse(arg, CultureInfo.InvariantCulture) }); return null;
                case "ATTN?": return Line(prefix + "ATTN", c.Attn.ToString(CultureInfo.InvariantCulture));
                case "INVS": SetChannel(ch, c with { Inv = arg.Equals("ON", StringComparison.OrdinalIgnoreCase) }); return null;
                case "INVS?": return Line(prefix + "INVS", c.Inv ? "ON" : "OFF");
                case "UNIT": SetChannel(ch, c with { Unit = arg.ToUpperInvariant() }); return null;
                case "UNIT?": return Line(prefix + "UNIT", c.Unit);
                case "SKEW": SetChannel(ch, c with { Skew = Math.Clamp(ParseTime(arg), -100e-9, 100e-9) }); return null;
                case "SKEW?": return Line(prefix + "SKEW", E(c.Skew) + "S");
                case "TRSL": _s = _s with { TrigSlope = arg.ToUpperInvariant() }; return null;
                case "TRSL?": return Line(prefix + "TRSL", _s.TrigSlope);
                case "TRCP": _s = _s with { TrigCoupling = arg.ToUpperInvariant() }; return null;
                case "TRCP?": return Line(prefix + "TRCP", _s.TrigCoupling);
                case "TRLV": SetChannel(ch, c with { TrigLevel = ParseVolts(arg) }); return null;
                case "TRLV?": return Line(prefix + "TRLV", E(c.TrigLevel) + "V");
                case "TRLV2": _s = _s with { TrigLevel2 = ParseVolts(arg) }; return null;
                case "TRLV2?": return Line(prefix + "TRLV2", E(_s.TrigLevel2) + "V");
                case "WF?": return Waveform(ch);
                case "PAVA?": return Line(prefix + "PAVA", Measurements(ch));
            }
            return null;
        }
    }

    private int _selected = 1;

    private sealed class PassFail
    {
        public bool Enabled, Running, Message, StopOnFail, Beep;
        public string Source = "C1";
        public double X = 0.2, Y = 0.2;
        public Channel? Mask;
        public long Fail, Pass;
        public DateTime At;
    }

    private readonly PassFail _pf = new();

    private static bool OnArg(string arg) => arg.Trim().Equals("ON", StringComparison.OrdinalIgnoreCase);

    private Channel SourceChannel() => _s.Channels[_pf.Source[1] - '1'];

    /// <summary>~15 tested waveforms per second while running; they fail while the source channel's vertical
    /// settings differ from those at mask creation (the waveform moved on screen).</summary>
    private void CountPassFail()
    {
        if (!_pf.Running) return;
        var now = DateTime.UtcNow;
        long n = (long)((now - _pf.At).TotalSeconds * 15);
        if (n == 0) return;
        _pf.At = now;
        var c = SourceChannel();
        bool fails = _pf.Mask is not { } m || m.Vdiv != c.Vdiv || m.Ofst != c.Ofst || m.Skew != c.Skew || !c.On;
        if (!fails)
        {
            _pf.Pass += n;
            return;
        }
        if (_pf.StopOnFail)
        {
            _pf.Fail += 1;
            _pf.Running = false;
            _s = _s with { Trmd = "STOP" };
        }
        else _pf.Fail += n;
    }

    /// <summary>CH1/CH2 keys (39/40): an off channel is switched on and selected, an unselected one is selected,
    /// the selected one is switched off and stays selected (as on the SDS1202X-E).</summary>
    private void PressKey(string arg)
    {
        int n = arg.Split(',')[0].Trim() switch { "39" => 1, "40" => 2, _ => 0 };
        if (n == 0) return;
        var c = _s.Channels[n - 1];
        if (c.On && _selected == n) SetChannel(n, c with { On = false });
        else
        {
            SetChannel(n, c with { On = true });
            _selected = n;
        }
    }

    private void SetChannel(int ch, Channel channel)
    {
        var channels = _s.Channels.ToArray();
        channels[ch - 1] = channel;
        _s = _s with { Channels = channels };
    }

    /// <summary>TRSE pairs may be partial: "EDGE,SR,C2" changes only the source (as on the real scope).</summary>
    private void SetTrigger(string arg)
    {
        var p = arg.Split(',', StringSplitOptions.TrimEntries);
        var type = p[0].ToUpperInvariant() switch { "WINDOW" or "WINDOWS" => "WIN", "PATTERN" or "PATT" or "PAT" => "PA", var t => t };
        var s = _s with { TrigType = type };
        if (type == "TV") _video = string.Join(',', p.Skip(3)).ToUpperInvariant();
        for (int i = 1; i + 1 < p.Length; i += 2)
        {
            switch (p[i].ToUpperInvariant())
            {
                case "SR": s = s with { TrigSource = p[i + 1].ToUpperInvariant() }; break;
                case "HT": s = s with { HoldType = p[i + 1].ToUpperInvariant() }; break;
                case "HV": s = s with { HoldValue = ParseTime(p[i + 1]) }; break;
                case "HV2": s = s with { HoldValue2 = ParseTime(p[i + 1]) }; break;
            }
        }
        _s = s;
    }

    private string TriggerText()
    {
        // Like FW 1.3.27: window and pattern triggers are reported without their parameters.
        if (_s.TrigType is "WIN" or "PA") return _s.TrigType;
        if (_s.TrigType == "TV") return $"TV,SR,{_s.TrigSource},{_video}";
        var text = $"{_s.TrigType},SR,{_s.TrigSource},HT,{_s.HoldType}";
        if (_s.HoldType != "OFF") text += $",HV,{E(_s.HoldValue)}s";
        if (_s.HoldType is "P2" or "P1" or "I2" or "I1") text += $",HV2,{E(_s.HoldValue2)}s";
        return text;
    }

    /// <summary>Both channels rise at t = 0 (plus their skew), so delay = skew difference and phase follows at 1 kHz.</summary>
    private byte[] Delay(int a, int b, string type)
    {
        string prefix = $"C{a}-C{b}:MEAD";
        if (a > _s.Channels.Length || b > _s.Channels.Length) return Line(prefix, $"{type},****");
        double delay = _s.Channels[b - 1].Skew - _s.Channels[a - 1].Skew;
        return type switch
        {
            "PHA" => Line(prefix, $"PHA,{E(delay * 1000 * 360)}degree"),
            "FRR" or "SKEW" => Line(prefix, $"{type},{E(delay)}S"),
            _ => Line(prefix, $"{type},****"),
        };
    }

    /// <summary>A small stand-in for the scope's nsp_config XML.</summary>
    private string SetupXml() =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <nsp_config>
          <tdiv>{E(_s.Tdiv)}</tdiv>
          <trdl>{E(_s.Trdl)}</trdl>
          <ch1_vdiv>{E(_s.Channels[0].Vdiv)}</ch1_vdiv>
          <ch2_vdiv>{E(_s.Channels[1].Vdiv)}</ch2_vdiv>
          <acq_mode>{_s.AcqMode}</acq_mode>
        </nsp_config>

        """;

    private void LoadSetup(string xml)
    {
        double Get(string tag, double fallback) =>
            Regex.Match(xml, $"<{tag}>([^<]+)</{tag}>") is { Success: true } m ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : fallback;
        var channels = _s.Channels.ToArray();
        channels[0] = channels[0] with { Vdiv = Get("ch1_vdiv", channels[0].Vdiv) };
        channels[1] = channels[1] with { Vdiv = Get("ch2_vdiv", channels[1].Vdiv) };
        var acq = Regex.Match(xml, "<acq_mode>([^<]+)</acq_mode>");
        _s = _s with
        {
            Tdiv = Get("tdiv", _s.Tdiv),
            Trdl = Get("trdl", _s.Trdl),
            Channels = channels,
            AcqMode = acq.Success ? acq.Groups[1].Value : _s.AcqMode,
        };
    }

    private double Depth => ScpiValue.ParseNumber(_s.MemoryDepth.Replace("K", "kpts").Replace("M", "Mpts"));
    private double SampleRate => Math.Min(MaxSampleRate, Depth / (_s.Tdiv * WaveformDecoder.HorizontalDivisions));
    private long Points => (long)Math.Round(SampleRate * _s.Tdiv * WaveformDecoder.HorizontalDivisions);

    /// <summary>Signal at time t; history frames are shifted in phase so they differ visibly.</summary>
    private double Signal(int ch, double t)
    {
        if (_history) t += (HistoryFrames - _frame) * 37e-6;
        double phase = t * 1000 - Math.Floor(t * 1000);
        return ch == 1 ? 1.5 * Math.Sin(2 * Math.PI * phase) : phase < 0.5 ? 3.3 : 0;
    }

    private sbyte Code(int ch, double t)
    {
        var c = _s.Channels[ch - 1];
        double v = Signal(ch, t - c.Skew) + (_noise.NextDouble() - 0.5) * 0.02;
        // Like the real scope, an inverted channel is transferred inverted.
        if (c.Inv) v = -v;
        return (sbyte)Math.Clamp(Math.Round((v + c.Ofst) * WaveformDecoder.CodesPerDivision / c.Vdiv), -127, 127);
    }

    private byte[] Waveform(int ch)
    {
        // Matches the observed firmware: SP is honored when NP is non-zero.
        int step = Math.Max(1, _sp);
        long available = (Points - _fp + step - 1) / step;
        long count = _np > 0 ? Math.Min(_np, available) : available;
        double t0 = WaveformDecoder.FirstPointTime(_s.Tdiv, -_s.Trdl);
        var data = new byte[count];
        for (long i = 0; i < count; i++)
            data[i] = (byte)Code(ch, t0 + (_fp + i * step) / SampleRate);
        return Block($"{(_headers ? $"C{ch}:WF " : "")}DAT2,", data);
    }

    private static byte[] Block(string header, byte[] data) =>
        [.. Encoding.ASCII.GetBytes($"{header}#9{data.Length:D9}"), .. data, (byte)'\n', (byte)'\n'];

    /// <summary>Response to Cn:PAVA? ALL (subset of the real parameter list; "****" where undefined).</summary>
    private static string Measurements(int ch)
    {
        double max = ch == 1 ? 1.5 : 3.3, min = ch == 1 ? -1.5 : 0;
        double rms = ch == 1 ? 1.5 / Math.Sqrt(2) : 3.3 / Math.Sqrt(2);
        (string, string)[] values =
        [
            ("MAX", E(max)), ("MIN", E(min)), ("PKPK", E(max - min)), ("TOP", E(max)), ("BASE", E(min)),
            ("AMPL", E(max - min)), ("MEAN", E(ch == 1 ? 0 : 1.65)), ("RMS", E(rms)), ("PER", E(1e-3)),
            ("FREQ", E(1e3)), ("PWID", E(5e-4)), ("NWID", E(5e-4)), ("RISE", ch == 1 ? E(2.95e-4) : E(1e-9)),
            ("FALL", ch == 1 ? E(2.95e-4) : E(1e-9)), ("DUTY", E(50)), ("OVSP", "****"),
        ];
        return string.Join(',', values.Select(v => $"{v.Item1},{v.Item2}"));
    }

    private byte[] Screenshot()
    {
        const int w = 800, h = 480, x0 = 12, y0 = 18, divPx = 50;
        var pixels = new ushort[w * h];
        for (int y = 0; y < 8 * divPx; y += 10)
            for (int x = 0; x <= 14 * divPx; x += divPx) pixels[(y0 + y) * w + x0 + x] = 0x4208;
        for (int y = 0; y <= 8 * divPx; y += divPx)
            for (int x = 0; x < 14 * divPx; x += 10) pixels[(y0 + y) * w + x0 + x] = 0x4208;
        ushort[] colors = [0xFFE0, 0xF81F];
        double t0 = WaveformDecoder.FirstPointTime(_s.Tdiv, -_s.Trdl);
        for (int ch = 1; ch <= _s.Channels.Length; ch++)
        {
            if (!_s.Channels[ch - 1].On) continue;
            for (int x = 0; x < 14 * divPx; x++)
            {
                double divs = Code(ch, t0 + x * _s.Tdiv / divPx) / WaveformDecoder.CodesPerDivision;
                int y = y0 + 4 * divPx - (int)Math.Round(divs * divPx);
                if (y >= y0 && y < y0 + 8 * divPx) pixels[y * w + x0 + x] = colors[ch - 1];
            }
        }

        // BITMAPINFOHEADER + RGB565 bit masks, top-down rows: the layout the real scope sends.
        const int offset = 66;
        var file = new byte[offset + w * h * 2 + 1];
        "BM"u8.CopyTo(file);
        var s = file.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(s[2..], file.Length - 1);
        BinaryPrimitives.WriteInt32LittleEndian(s[10..], offset);
        BinaryPrimitives.WriteInt32LittleEndian(s[14..], 40);
        BinaryPrimitives.WriteInt32LittleEndian(s[18..], w);
        BinaryPrimitives.WriteInt32LittleEndian(s[22..], -h);
        BinaryPrimitives.WriteInt16LittleEndian(s[26..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(s[28..], 16);
        BinaryPrimitives.WriteInt32LittleEndian(s[30..], 3);
        BinaryPrimitives.WriteInt32LittleEndian(s[34..], w * h * 2);
        BinaryPrimitives.WriteInt32LittleEndian(s[54..], 0xF800);
        BinaryPrimitives.WriteInt32LittleEndian(s[58..], 0x07E0);
        BinaryPrimitives.WriteInt32LittleEndian(s[62..], 0x001F);
        for (int i = 0; i < pixels.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(s[(offset + i * 2)..], pixels[i]);
        file[^1] = (byte)'\n';
        return file;
    }

    private byte[] Line(string header, string value) => Text(_headers ? $"{header} {value}" : value);
    private static byte[] Text(string s) => Encoding.ASCII.GetBytes(s + "\n");
    private static string E(double v) => v.ToString("0.00E+00", CultureInfo.InvariantCulture);
    private static double ParseTime(string s) => ScpiValue.ParseNumber(s.EndsWith('S') || s.EndsWith('s') ? s : s + "S");
    private static double ParseVolts(string s) => ScpiValue.ParseNumber(s.EndsWith('V') || s.EndsWith('v') ? s : s + "V");

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        await _acceptLoop;
        _cts.Dispose();
    }
}
