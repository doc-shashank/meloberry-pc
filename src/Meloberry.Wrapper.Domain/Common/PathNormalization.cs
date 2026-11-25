namespace Meloberry.Wrapper.Domain.Common;

/// <summary>Safe directory path normalization and equality helpers preventing trailing-slash mismatches.</summary>
public static class PathNormalization
{
    public static string NormalizeDirectoryPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
    }

    public static bool TryNormalizeDirectoryPath(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
            return !string.IsNullOrWhiteSpace(normalized);
        }
        catch
        {
            normalized = string.Empty;
            return false;
        }
    }

    public static bool AreSameDirectory(string? left, string? right)
    {
        if (!TryNormalizeDirectoryPath(left, out var normLeft) ||
            !TryNormalizeDirectoryPath(right, out var normRight))
        {
            return false;
        }

        return string.Equals(normLeft, normRight, StringComparison.OrdinalIgnoreCase);
    }
}
