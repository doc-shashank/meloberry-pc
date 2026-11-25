using Meloberry.Wrapper.Domain.Common;

namespace Meloberry.Wrapper.UnitTests;

internal static class TestRepoPaths
{
    public static string? FindMbyExecutable() =>
        FindToolExecutable(Path.Combine("Project", "CLI", "app"), "mby-cli-*.exe")
        ?? FindToolExecutable(Path.Combine("CLI", "app"), "mby-cli-*.exe");

    public static string? FindMelomgExecutable() =>
        FindToolExecutable(Path.Combine("Project", "toolings", "melomg", "app"), "meloberry-melomg-*.exe")
        ?? FindToolExecutable(Path.Combine("toolings", "melomg", "app"), "meloberry-melomg-*.exe");

    private static string? FindToolExecutable(string relativeAppDir, string searchPattern)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 12 && dir is not null; depth++)
        {
            var candidateDir = Path.Combine(dir.FullName, relativeAppDir);
            if (Directory.Exists(candidateDir))
            {
                var matches = Directory.GetFiles(candidateDir, searchPattern, SearchOption.TopDirectoryOnly);
                if (matches.Length > 0)
                {
                    return matches
                        .OrderByDescending(p => Path.GetFileNameWithoutExtension(p), Comparer<string>.Create(SemverComparer.Compare))
                        .First();
                }
            }

            dir = dir.Parent;
        }

        return null;
    }
}
