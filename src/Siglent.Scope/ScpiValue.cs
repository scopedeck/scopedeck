using System.Globalization;
using System.Text.RegularExpressions;

namespace Siglent.Scope;

/// <summary>Parsing and formatting of Siglent SCPI values such as "C1:VDIV 2.00E-01V" or "SARA 1.00GSa/s".</summary>
public static partial class ScpiValue
{
    [GeneratedRegex(@"^(?:[A-Z]+\d*:)?[A-Z*][A-Z0-9_?]*$")]
    private static partial Regex HeaderToken();

    [GeneratedRegex(@"^([-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?)\s*([A-Za-zµ/%]*)$")]
    private static partial Regex NumberWithUnit();

    /// <summary>Removes a response header ("C1:VDIV ") that is present unless CHDR OFF is active.</summary>
    public static string StripHeader(string response)
    {
        var s = response.Trim();
        int space = s.IndexOf(' ');
        return space > 0 && HeaderToken().IsMatch(s[..space]) ? s[(space + 1)..].Trim() : s;
    }

    public static double ParseNumber(string response)
    {
        var text = StripHeader(response);
        var m = NumberWithUnit().Match(text);
        if (!m.Success)
            throw new FormatException($"Not a numeric SCPI value: '{response}'.");
        double value = double.Parse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
        return value * PrefixMultiplier(m.Groups[2].Value, response);
    }

    /// <summary>Like <see cref="ParseNumber"/> but returns null for placeholders such as "****".</summary>
    public static double? TryParseNumber(string response)
    {
        try { return ParseNumber(response); }
        catch (FormatException) { return null; }
    }

    public static bool ParseBool(string response) => StripHeader(response).ToUpperInvariant() is "ON" or "1";

    public static string[] ParseList(string response) =>
        StripHeader(response).Split(',', StringSplitOptions.TrimEntries);

    /// <summary>Formats a value as a plain decimal with a unit, e.g. 0.0005 → "0.0005V".</summary>
    public static string Format(double value, string unit) =>
        value.ToString("0.############", CultureInfo.InvariantCulture) + unit;

    /// <summary>Formats a timebase in the scope's own notation, e.g. 5e-4 → "500US".</summary>
    public static string FormatTimeDiv(double seconds)
    {
        var (scale, suffix) = seconds switch
        {
            < 1e-6 => (1e9, "NS"),
            < 1e-3 => (1e6, "US"),
            < 1 => (1e3, "MS"),
            _ => (1.0, "S"),
        };
        return Math.Round(seconds * scale, 3).ToString("0.###", CultureInfo.InvariantCulture) + suffix;
    }

    private static double PrefixMultiplier(string unit, string response)
    {
        string prefix = unit;
        bool timeOrVolt = false;
        if (unit.EndsWith("Sa/s", StringComparison.OrdinalIgnoreCase)) prefix = unit[..^4];
        else if (unit.EndsWith("Hz", StringComparison.OrdinalIgnoreCase)) prefix = unit[..^2];
        else if (unit.EndsWith("pts", StringComparison.OrdinalIgnoreCase)) prefix = unit[..^3];
        else if (unit.EndsWith('%')) prefix = unit[..^1];
        else if (unit.EndsWith('V') || unit.EndsWith('v') || unit.EndsWith('S') || unit.EndsWith('s'))
        {
            prefix = unit[..^1];
            timeOrVolt = true;
        }

        return prefix switch
        {
            "" => 1,
            "G" => 1e9,
            // Siglent writes milliseconds/millivolts as "MS"/"MV".
            "M" => timeOrVolt ? 1e-3 : 1e6,
            "k" or "K" => 1e3,
            "m" => 1e-3,
            "u" or "U" or "µ" => 1e-6,
            "n" or "N" => 1e-9,
            "p" or "P" => 1e-12,
            _ => throw new FormatException($"Unknown unit '{unit}' in SCPI value '{response}'."),
        };
    }
}
