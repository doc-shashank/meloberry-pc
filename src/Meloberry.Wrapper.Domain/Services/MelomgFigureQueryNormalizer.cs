using System.Text.RegularExpressions;

namespace Meloberry.Wrapper.Domain.Services;

/// <summary>Converts natural user figure queries (e.g. "Figure 1.1") into valid `melomg` selectors.</summary>
public static partial class MelomgFigureQueryNormalizer
{
    [GeneratedRegex(@"^(?:fig(?:ure)?|diagram|chart|plate|table|exhibit)\s*\.?\s*#?\s*(?<sel>[A-Za-z0-9][A-Za-z0-9._-]*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrefixRegex();

    public static string Normalize(string? rawQuery)
    {
        if (string.IsNullOrWhiteSpace(rawQuery))
        {
            return string.Empty;
        }

        var trimmed = rawQuery.Trim();
        if (string.Equals(trimmed, "all", StringComparison.OrdinalIgnoreCase))
        {
            return "all";
        }

        if (trimmed.StartsWith("n:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("f", StringComparison.OrdinalIgnoreCase) && trimmed.Length > 1 && int.TryParse(trimmed[1..], out _) ||
            trimmed.StartsWith("p", StringComparison.OrdinalIgnoreCase) && trimmed.Length > 1 && int.TryParse(trimmed[1..], out _))
        {
            return trimmed;
        }

        if (trimmed.StartsWith('#'))
        {
            return trimmed[1..].Trim();
        }

        var match = PrefixRegex().Match(trimmed);
        return match.Success ? match.Groups["sel"].Value : trimmed;
    }
}
