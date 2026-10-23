using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace Siglent.Scope;

/// <summary>
/// Raw SCPI over a TCP socket (port 5025 on SDS1000X-E). No VISA required.
/// Not thread-safe: callers must serialize access.
/// </summary>
public sealed class ScpiConnection : IAsyncDisposable
{
    public const int DefaultPort = 5025;

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly byte[] _buffer = new byte[64 * 1024];
    private int _pos;
    private int _len;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan BinaryTimeout { get; set; } = TimeSpan.FromSeconds(60);

    private ScpiConnection(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    public static async Task<ScpiConnection> ConnectAsync(string host, int port = DefaultPort, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var client = new TcpClient { NoDelay = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(3));
        try
        {
            await client.ConnectAsync(host, port, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException($"Could not connect to {host}:{port} (timeout).");
        }
        catch
        {
            client.Dispose();
            throw;
        }
        return new ScpiConnection(client);
    }

    public async Task WriteAsync(string command, CancellationToken ct = default)
    {
        DiscardPending();
        await WithTimeout(Timeout, ct, async token =>
        {
            await _stream.WriteAsync(Encoding.ASCII.GetBytes(command + "\n"), token);
            return 0;
        });
    }

    /// <summary>Sends a query and returns the single-line response without the terminator.</summary>
    public async Task<string> QueryAsync(string query, CancellationToken ct = default)
    {
        await WriteAsync(query, ct);
        return await WithTimeout(Timeout, ct, async token =>
        {
            // Binary responses end with extra "\n" bytes; a real response is never empty.
            string line;
            do line = await ReadLineAsync(token);
            while (line.Length == 0);
            return line;
        });
    }

    /// <summary>
    /// Sends a query that answers with an IEEE 488.2 definite-length block, e.g.
    /// "C1:WF DAT2,#9000001400&lt;data&gt;\n\n", and returns the data bytes.
    /// </summary>
    public async Task<byte[]> QueryBlockAsync(string query, CancellationToken ct = default)
    {
        await WriteAsync(query, ct);
        return await WithTimeout(BinaryTimeout, ct, async token =>
        {
            while (await ReadByteAsync(token) != '#') { }
            int digits = await ReadByteAsync(token) - '0';
            if (digits is < 1 or > 9)
                throw new InvalidDataException("Malformed binary block header.");
            var lengthBytes = new byte[digits];
            await ReadExactlyAsync(lengthBytes, token);
            int length = int.Parse(Encoding.ASCII.GetString(lengthBytes));
            var data = new byte[length];
            await ReadExactlyAsync(data, token);
            return data;
        });
    }

    /// <summary>Sends a query that answers with a raw BMP file (SCDP) and returns the file bytes.</summary>
    public async Task<byte[]> QueryBitmapAsync(string query, CancellationToken ct = default)
    {
        await WriteAsync(query, ct);
        return await WithTimeout(BinaryTimeout, ct, async token =>
        {
            var header = new byte[6];
            await ReadExactlyAsync(header, token);
            if (header[0] != 'B' || header[1] != 'M')
                throw new InvalidDataException("Screen dump is not a BMP image.");
            int size = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(2));
            var file = new byte[size];
            header.CopyTo(file, 0);
            await ReadExactlyAsync(file.AsMemory(header.Length), token);
            // The scope appends a trailing "\n"; DiscardPending drops it before the next command.
            return file;
        });
    }

    private void DiscardPending()
    {
        _pos = _len = 0;
        while (_client.Available > 0)
            _stream.ReadExactly(_buffer.AsSpan(0, Math.Min(_client.Available, _buffer.Length)));
    }

    private async Task<string> ReadLineAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        while (true)
        {
            int b = await ReadByteAsync(ct);
            if (b == '\n') return sb.ToString().TrimEnd('\r');
            sb.Append((char)b);
        }
    }

    private async ValueTask<int> ReadByteAsync(CancellationToken ct)
    {
        if (_pos == _len) await FillAsync(ct);
        return _buffer[_pos++];
    }

    private async Task ReadExactlyAsync(Memory<byte> destination, CancellationToken ct)
    {
        int buffered = Math.Min(_len - _pos, destination.Length);
        _buffer.AsMemory(_pos, buffered).CopyTo(destination);
        _pos += buffered;
        if (buffered < destination.Length)
            await _stream.ReadExactlyAsync(destination[buffered..], ct);
    }

    private async Task FillAsync(CancellationToken ct)
    {
        _pos = 0;
        _len = await _stream.ReadAsync(_buffer, ct);
        if (_len == 0) throw new IOException("The oscilloscope closed the connection.");
    }

    private static async Task<T> WithTimeout<T>(TimeSpan timeout, CancellationToken ct, Func<CancellationToken, Task<T>> action)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await action(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("The oscilloscope did not respond in time.");
        }
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
