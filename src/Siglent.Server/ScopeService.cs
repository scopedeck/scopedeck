using System.Net.Sockets;
using Siglent.Scope;

namespace Siglent.Server;

public sealed class ScopeNotConnectedException() : Exception("The oscilloscope is not connected.");

/// <summary>Owns the single connection to the oscilloscope and drops it after a communication failure.</summary>
public sealed class ScopeService(ILogger<ScopeService> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private SiglentScope? _scope;

    public string? Host { get; private set; }
    public int Port { get; private set; } = ScpiConnection.DefaultPort;
    public ScopeIdentity? Identity => _scope?.Identity;

    public async Task<ScopeIdentity> ConnectAsync(string host, int port, CancellationToken ct)
    {
        await _connectLock.WaitAsync(ct);
        try
        {
            await DropAsync();
            _scope = await SiglentScope.ConnectAsync(host, port, ct);
            (Host, Port) = (host, port);
            logger.LogInformation("Connected to {Model} ({Serial}) at {Host}:{Port}", _scope.Identity.Model, _scope.Identity.Serial, host, port);
            return _scope.Identity;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _connectLock.WaitAsync();
        try { await DropAsync(); }
        finally { _connectLock.Release(); }
    }

    /// <summary>Runs an operation; a socket error or timeout closes the connection so the UI can reconnect cleanly.</summary>
    public async Task<T> RunAsync<T>(Func<SiglentScope, Task<T>> action)
    {
        var scope = _scope ?? throw new ScopeNotConnectedException();
        try
        {
            return await action(scope);
        }
        catch (Exception e) when (e is IOException or SocketException or TimeoutException or InvalidDataException)
        {
            logger.LogWarning(e, "Communication with the oscilloscope failed; disconnecting.");
            await _connectLock.WaitAsync();
            try
            {
                if (ReferenceEquals(_scope, scope)) await DropAsync();
            }
            finally
            {
                _connectLock.Release();
            }
            throw;
        }
    }

    public Task RunAsync(Func<SiglentScope, Task> action) => RunAsync(async s =>
    {
        await action(s);
        return true;
    });

    private async Task DropAsync()
    {
        if (_scope is null) return;
        await _scope.DisposeAsync();
        _scope = null;
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
