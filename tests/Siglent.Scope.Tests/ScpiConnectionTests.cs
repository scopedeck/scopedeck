using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Siglent.Scope.Tests;

public class ScpiConnectionTests
{
    /// <summary>
    /// A server that ignores the first <paramref name="drop"/> lines it receives (like the SDS1202X-E while it is
    /// busy) and answers every later query with "ok &lt;n&gt;".
    /// </summary>
    private static (int Port, Task Server) DroppingServer(int drop, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            listener.Stop();
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            int n = 0;
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (++n <= drop || !line.EndsWith('?')) continue;
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"ok {n}\n"), ct);
            }
        }, ct);
        return (port, server);
    }

    [Fact]
    public async Task ResendsAQueryTheScopeDropped()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (port, _) = DroppingServer(drop: 1, cts.Token);
        await using var scpi = await ScpiConnection.ConnectAsync("127.0.0.1", port);
        scpi.ResendAfter = TimeSpan.FromMilliseconds(200);

        Assert.Equal("ok 2", await scpi.QueryAsync("TDIV?"));
        // The connection keeps working after the timed-out read.
        Assert.Equal("ok 3", await scpi.QueryAsync("TDIV?"));
    }

    [Fact]
    public async Task GivesUpWhenTheResendIsDroppedToo()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (port, _) = DroppingServer(drop: 2, cts.Token);
        await using var scpi = await ScpiConnection.ConnectAsync("127.0.0.1", port);
        scpi.ResendAfter = TimeSpan.FromMilliseconds(200);
        scpi.Timeout = TimeSpan.FromMilliseconds(600);

        await Assert.ThrowsAsync<TimeoutException>(() => scpi.QueryAsync("TDIV?"));
    }
}
