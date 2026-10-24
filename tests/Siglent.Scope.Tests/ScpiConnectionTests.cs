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

    [Theory]
    [InlineData("C1:VDIV 2V", true)]
    [InlineData("c2:ofst -1.5V", true)]
    [InlineData("C1:CPL A1M", true)]
    [InlineData("C2:ATTN 10", true)]
    [InlineData("C1:TRA ON", true)]
    [InlineData("BWL C1,ON", true)]
    [InlineData("C1:VDIV?", false)]
    [InlineData("TDIV 1MS", false)]
    [InlineData("C1:TRLV 1.5V", false)]
    [InlineData("WFSU SP,1,NP,0,FP,0", false)]
    public void KnowsWhichCommandsSwitchTheInputRelays(string command, bool slow) =>
        Assert.Equal(slow, ScpiConnection.IsSlowCommand(command) && !command.Contains('?'));

    [Fact]
    public async Task WaitsAfterACommandBeforeTheNextOne()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (port, _) = DroppingServer(drop: 0, cts.Token);
        await using var scpi = await ScpiConnection.ConnectAsync("127.0.0.1", port);
        scpi.CommandGap = TimeSpan.FromMilliseconds(100);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await scpi.WriteAsync("TDIV 1MS");
        await scpi.QueryAsync("TDIV?");
        Assert.True(sw.ElapsedMilliseconds >= 95, $"took {sw.ElapsedMilliseconds} ms");

        sw.Restart();
        await scpi.QueryAsync("TDIV?"); // no command in between: no pause
        Assert.True(sw.ElapsedMilliseconds < 80, $"took {sw.ElapsedMilliseconds} ms");
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
