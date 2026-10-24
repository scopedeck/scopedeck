using System.Text.Json;

namespace Siglent.Server;

/// <summary>
/// Per-user settings remembered between runs (e.g. the last scope address), stored in
/// %LocalAppData%\ScopeDeck\settings.json (~/.local/share/ScopeDeck on Linux, ~/Library/Application Support on macOS).
/// Kept on the server because the desktop app's browser storage is lost with every new port.
/// </summary>
public sealed class UserSettings(ILogger<UserSettings> logger)
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "ScopeDeck", "settings.json");

    private readonly Lock _lock = new();
    private Data? _data;

    public string? LastHost
    {
        get { lock (_lock) return Load().LastHost; }
        set
        {
            lock (_lock)
            {
                if (Load().LastHost == value) return;
                _data = _data! with { LastHost = value };
                Save();
            }
        }
    }

    private Data Load()
    {
        if (_data is not null) return _data;
        try
        {
            _data = File.Exists(FilePath) ? JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "Could not read {Path}; using defaults.", FilePath);
        }
        return _data ??= new Data(null);
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "Could not write {Path}.", FilePath);
        }
    }

    private sealed record Data(string? LastHost);
}
