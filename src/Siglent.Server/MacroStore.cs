using System.Text.Json;
using System.Text.RegularExpressions;

namespace Siglent.Server;

/// <summary>One step of a macro: an API call (POST path + JSON body) or, with WaitMs alone, a pause.</summary>
/// <param name="DelayMs">Time since the previous step while recording (used with "recorded timing").</param>
public sealed record MacroStep(string? Path, JsonElement? Body, int? WaitMs, int? DelayMs);

public sealed record Macro(string Name, IReadOnlyList<MacroStep> Steps);

/// <summary>
/// Recorded macros, stored next to the user settings in macros.json. Only calls that change the scope through
/// ScopeDeck's own endpoints are allowed (<see cref="AllowedPath"/>), so a macro file cannot reach anything else.
/// </summary>
public sealed partial class MacroStore(ILogger<MacroStore> logger, string? filePath = null)
{
    public const int MaxSteps = 2000;
    public const int MaxNameLength = 60;
    public const int MaxWaitMs = 3_600_000;

    private readonly string _filePath = filePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "ScopeDeck", "macros.json");

    private readonly Lock _lock = new();
    private List<Macro>? _macros;

    /// <summary>POST endpoints a macro may call: settings, keys, pass/fail, history, setup recall and SCPI.</summary>
    [GeneratedRegex(@"^/api/(channel/\d|timebase|acquisition|trigger|trigger/set50|autosetup|panel|passfail|history|history/frame|setup/memory/\d{1,2}/recall|scpi)$")]
    public static partial Regex AllowedPath();

    public IReadOnlyList<Macro> List()
    {
        lock (_lock) return [.. Load()];
    }

    /// <summary>Adds or replaces the macro with this name.</summary>
    public void Save(Macro macro)
    {
        Validate(macro);
        lock (_lock)
        {
            var macros = Load();
            int i = macros.FindIndex(m => m.Name.Equals(macro.Name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) macros[i] = macro;
            else macros.Add(macro);
            Write(macros);
        }
    }

    public bool Delete(string name)
    {
        lock (_lock)
        {
            var macros = Load();
            if (macros.RemoveAll(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) == 0) return false;
            Write(macros);
            return true;
        }
    }

    public static void Validate(Macro macro)
    {
        if (string.IsNullOrWhiteSpace(macro.Name) || macro.Name.Length > MaxNameLength)
            throw new ArgumentException($"A macro needs a name of 1–{MaxNameLength} characters.");
        if (macro.Steps is null || macro.Steps.Count is 0 or > MaxSteps)
            throw new ArgumentException($"A macro has 1–{MaxSteps} steps.");
        foreach (var step in macro.Steps)
        {
            if (step.Path is null)
            {
                if (step.WaitMs is not (>= 0 and <= MaxWaitMs))
                    throw new ArgumentException("A pause must be 0–3600 s.");
                continue;
            }
            if (!AllowedPath().IsMatch(step.Path))
                throw new ArgumentException($"A macro cannot call {step.Path}.");
            if (step.Path == "/api/scpi" && Command(step) is var command && (command is null || IsMemoryWrite(command)))
                throw new ArgumentException("A macro cannot store setups or masks in the scope's memory.");
        }
    }

    private static string? Command(MacroStep step) =>
        step.Body is { ValueKind: JsonValueKind.Object } body
        && (body.TryGetProperty("command", out var c) || body.TryGetProperty("Command", out c))
        && c.ValueKind == JsonValueKind.String ? c.GetString() : null;

    /// <summary>*SAV overwrites a setup memory and PFSL …SAVE the stored mask: not something to replay unattended.</summary>
    private static bool IsMemoryWrite(string command)
    {
        var c = command.Trim().ToUpperInvariant();
        return c.StartsWith("*SAV") || c.StartsWith("PFSL") && c.Contains("SAVE") || c.StartsWith("PF_SAVELOAD") && c.Contains("SAVE");
    }

    private List<Macro> Load()
    {
        if (_macros is not null) return _macros;
        try
        {
            _macros = File.Exists(_filePath) ? JsonSerializer.Deserialize<List<Macro>>(File.ReadAllText(_filePath), Json) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "Could not read {Path}; starting without macros.", _filePath);
        }
        return _macros ??= [];
    }

    private void Write(List<Macro> macros)
    {
        _macros = macros;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(macros, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "Could not write {Path}.", _filePath);
            throw new InvalidOperationException("The macro could not be saved.", e);
        }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
