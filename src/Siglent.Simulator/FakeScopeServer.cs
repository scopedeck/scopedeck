using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Siglent.Scope;

namespace Siglent.Simulator;

/// <summary>
/// A fake SDS1202X-E that speaks the SCPI subset used by <see cref="SiglentScope"/> on a TCP port.
/// Response framing mirrors a real unit on FW 1.3.27 (block "\n\n" tail, RGB565 BMP plus a trailing byte).
/// C1 carries a 1 kHz 3 Vpp sine; C2 a 1 kHz 0–3.3 V square wave.
/// </summary>
public sealed class FakeScopeServer : IAsyncDisposable
{
    private const double MemoryDepth = 1.4e6;
    private const double MaxSampleRate = 1e9;

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly Lock _state = new();
    private readonly Random _noise = new(1);

    private bool _headers = true;
    private double _tdiv = 200e-6;
    private double _trdl;
    private string _trmd = "AUTO";
    private string _trigSource = "C1";
    private string _trigSlope = "POS";
    private string _trigCoupling = "DC";
    private double _trigLevel;
    private int _sp, _np, _fp;
    private readonly Channel[] _channels = [new(true, 1, 0, 1), new(true, 1, -1.5, 1)];

    private sealed record Channel(bool On, double Vdiv, double Ofst, double Attn, string Cpl = "D1M");

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
        int ch = 0;
        if (head.Length > 3 && head[0] == 'C' && char.IsDigit(head[1]) && head[2] == ':')
        {
            ch = head[1] - '0';
            head = head[3..];
        }
        if (ch > _channels.Length) return null;

        lock (_state)
        {
            switch (head)
            {
                case "*IDN?": return Text("Siglent Technologies,SDS1202X-E,SIMULATOR00001,1.3.27");
                case "CHDR": _headers = !arg.Equals("OFF", StringComparison.OrdinalIgnoreCase); return null;
                case "CHDR?": return Line("CHDR", _headers ? "SHORT" : "OFF");
                case "TDIV": _tdiv = ParseTime(arg); return null;
                case "TDIV?": return Line("TDIV", E(_tdiv) + "S");
                case "TRDL": _trdl = ParseTime(arg); return null;
                case "TRDL?": return Line("TRDL", E(_trdl) + "S");
                case "SARA?": return Line("SARA", E(SampleRate) + "Sa/s");
                case "SANU?": return Line("SANU", E(Points) + "pts");
                case "SAST?": return Line("SAST", _trmd == "STOP" ? "Stop" : _trmd == "AUTO" ? "Auto" : "Trig'd");
                case "TRMD": _trmd = arg.ToUpperInvariant(); return null;
                case "TRMD?": return Line("TRMD", _trmd);
                case "STOP": _trmd = "STOP"; return null;
                case "ASET": Reset(); return null;
                case "TRSE": _trigSource = arg.Split(',')[2].Trim().ToUpperInvariant(); return null;
                case "TRSE?": return Line("TRSE", $"EDGE,SR,{_trigSource},HT,OFF");
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
            var c = _channels[ch - 1];
            string prefix = $"C{ch}:";
            switch (head)
            {
                case "TRA": _channels[ch - 1] = c with { On = arg.Equals("ON", StringComparison.OrdinalIgnoreCase) }; return null;
                case "TRA?": return Line(prefix + "TRA", c.On ? "ON" : "OFF");
                case "VDIV": _channels[ch - 1] = c with { Vdiv = ParseVolts(arg) }; return null;
                case "VDIV?": return Line(prefix + "VDIV", E(c.Vdiv) + "V");
                case "OFST": _channels[ch - 1] = c with { Ofst = ParseVolts(arg) }; return null;
                case "OFST?": return Line(prefix + "OFST", E(c.Ofst) + "V");
                case "CPL": _channels[ch - 1] = c with { Cpl = arg.ToUpperInvariant() }; return null;
                case "CPL?": return Line(prefix + "CPL", c.Cpl);
                case "ATTN": _channels[ch - 1] = c with { Attn = double.Parse(arg, CultureInfo.InvariantCulture) }; return null;
                case "ATTN?": return Line(prefix + "ATTN", c.Attn.ToString(CultureInfo.InvariantCulture));
                case "TRSL": _trigSlope = arg.ToUpperInvariant(); return null;
                case "TRSL?": return Line(prefix + "TRSL", _trigSlope);
                case "TRCP": _trigCoupling = arg.ToUpperInvariant(); return null;
                case "TRCP?": return Line(prefix + "TRCP", _trigCoupling);
                case "TRLV": _trigLevel = ParseVolts(arg); return null;
                case "TRLV?": return Line(prefix + "TRLV", E(_trigLevel) + "V");
                case "WF?": return Waveform(ch);
                case "PAVA?": return Line(prefix + "PAVA", Measurements(ch));
            }
            return null;
        }
    }

    private void Reset()
    {
        _tdiv = 200e-6;
        _trdl = 0;
        _trmd = "AUTO";
        _channels[0] = new(true, 1, 0, 1);
        _channels[1] = new(true, 1, -1.5, 1);
    }

    private double SampleRate => Math.Min(MaxSampleRate, MemoryDepth / (_tdiv * WaveformDecoder.HorizontalDivisions));
    private long Points => (long)Math.Round(SampleRate * _tdiv * WaveformDecoder.HorizontalDivisions);

    private static double Signal(int ch, double t)
    {
        double phase = t * 1000 - Math.Floor(t * 1000);
        return ch == 1 ? 1.5 * Math.Sin(2 * Math.PI * phase) : phase < 0.5 ? 3.3 : 0;
    }

    private sbyte Code(int ch, double t)
    {
        var c = _channels[ch - 1];
        double v = Signal(ch, t) + (_noise.NextDouble() - 0.5) * 0.02;
        return (sbyte)Math.Clamp(Math.Round((v + c.Ofst) * WaveformDecoder.CodesPerDivision / c.Vdiv), -127, 127);
    }

    private byte[] Waveform(int ch)
    {
        // Matches the observed firmware: SP is honored when NP is non-zero.
        int step = Math.Max(1, _sp);
        long available = (Points - _fp + step - 1) / step;
        long count = _np > 0 ? Math.Min(_np, available) : available;
        double t0 = WaveformDecoder.FirstPointTime(_tdiv, -_trdl);
        var data = new byte[count];
        for (long i = 0; i < count; i++)
            data[i] = (byte)Code(ch, t0 + (_fp + i * step) / SampleRate);

        var head = Encoding.ASCII.GetBytes($"{(_headers ? $"C{ch}:WF " : "")}DAT2,#9{count:D9}");
        return [.. head, .. data, (byte)'\n', (byte)'\n'];
    }

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
        double t0 = WaveformDecoder.FirstPointTime(_tdiv, -_trdl);
        for (int ch = 1; ch <= _channels.Length; ch++)
        {
            if (!_channels[ch - 1].On) continue;
            for (int x = 0; x < 14 * divPx; x++)
            {
                double divs = Code(ch, t0 + x * _tdiv / divPx) / WaveformDecoder.CodesPerDivision;
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
