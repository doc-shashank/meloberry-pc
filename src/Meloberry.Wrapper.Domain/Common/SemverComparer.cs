using System.Text.RegularExpressions;

namespace Meloberry.Wrapper.Domain.Common;

/// <summary>Zero-dependency SemVer 2.0.0 comparator supporting prerelease precedence.</summary>
public static partial class SemverComparer
{
    [GeneratedRegex(@"(\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex SemverRegex();

    public static string? ExtractVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var cleaned = text.Trim();
        if (cleaned.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[..^4];
        }

        var plus = cleaned.IndexOf('+');
        if (plus > 0)
        {
            cleaned = cleaned[..plus];
        }

        var match = SemverRegex().Match(cleaned);
        return match.Success ? match.Groups[1].Value : null;
    }

    public static int Compare(string? left, string? right)
    {
        var l = Parse(left);
        var r = Parse(right);

        var cmp = l.Major.CompareTo(r.Major);
        if (cmp != 0) return cmp;
        cmp = l.Minor.CompareTo(r.Minor);
        if (cmp != 0) return cmp;
        cmp = l.Patch.CompareTo(r.Patch);
        if (cmp != 0) return cmp;

        var lHasPre = l.Prerelease.Length > 0;
        var rHasPre = r.Prerelease.Length > 0;
        if (!lHasPre && !rHasPre) return 0;
        if (!lHasPre) return 1;  // Release > prerelease
        if (!rHasPre) return -1;

        var minLen = Math.Min(l.Prerelease.Length, r.Prerelease.Length);
        for (var i = 0; i < minLen; i++)
        {
            var li = l.Prerelease[i];
            var ri = r.Prerelease[i];
            var lIsNum = int.TryParse(li, out var lNum);
            var rIsNum = int.TryParse(ri, out var rNum);

            if (lIsNum && rIsNum)
            {
                cmp = lNum.CompareTo(rNum);
                if (cmp != 0) return cmp;
            }
            else if (lIsNum != rIsNum)
            {
                // Numeric identifiers always have lower precedence than non-numeric.
                return lIsNum ? -1 : 1;
            }
            else
            {
                cmp = string.CompareOrdinal(li, ri);
                if (cmp != 0) return cmp;
            }
        }

        return l.Prerelease.Length.CompareTo(r.Prerelease.Length);
    }

    private readonly record struct SemverParts(int Major, int Minor, int Patch, string[] Prerelease);

    private static SemverParts Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new SemverParts(0, 0, 0, []);
        }

        var trimmed = raw.Trim().TrimStart('v', 'V');
        var plus = trimmed.IndexOf('+');
        if (plus >= 0)
        {
            trimmed = trimmed[..plus];
        }

        string core;
        string[] prerelease = [];
        var dash = trimmed.IndexOf('-');
        if (dash > 0)
        {
            core = trimmed[..dash];
            var prePart = trimmed[(dash + 1)..];
            if (!string.IsNullOrWhiteSpace(prePart))
            {
                prerelease = prePart.Split('.', StringSplitOptions.RemoveEmptyEntries);
            }
        }
        else
        {
            core = trimmed;
        }

        var segments = core.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var major = segments.Length > 0 && int.TryParse(segments[0], out var maj) ? maj : 0;
        var minor = segments.Length > 1 && int.TryParse(segments[1], out var min) ? min : 0;
        var patch = segments.Length > 2 && int.TryParse(segments[2], out var pat) ? pat : 0;

        if (segments.Length == 0 || !int.TryParse(segments[0], out _))
        {
            return new SemverParts(0, 0, 0, []);
        }

        return new SemverParts(major, minor, patch, prerelease);
    }
}
