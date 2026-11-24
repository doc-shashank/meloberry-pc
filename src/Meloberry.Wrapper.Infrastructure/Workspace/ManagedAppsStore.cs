using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Infrastructure.Workspace;

/// <summary>
/// Manages canonical CLI executables stored in %LOCALAPPDATA%\Meloberry\apps\ including
/// metadata-based universal import detection and clean uninstall.
/// </summary>
public sealed partial class ManagedAppsStore(IWrapperDataPaths paths) : IManagedAppsStore
{
    [GeneratedRegex(@"(\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex SemverRegex();

    public Task<IReadOnlyList<ManagedAppEntry>> ScanAppsAsync(string? activeWorkspacePath, CancellationToken ct = default)
    {
        Directory.CreateDirectory(paths.AppsDirectory);
        var exeFiles = Directory.GetFiles(paths.AppsDirectory, "*.exe", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var workspaceExeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(activeWorkspacePath) && Directory.Exists(activeWorkspacePath))
        {
            foreach (var wsExe in Directory.GetFiles(activeWorkspacePath, "*.exe", SearchOption.TopDirectoryOnly))
            {
                workspaceExeNames.Add(Path.GetFileName(wsExe));
            }
        }

        string? mbyPath = null;
        string? melomgPath = null;
        var customExes = new List<string>();

        foreach (var full in exeFiles)
        {
            var name = Path.GetFileName(full);
            if (IsMbyFileName(name))
            {
                mbyPath = SelectNewerBinary(mbyPath, full);
            }
            else if (IsMelomgFileName(name))
            {
                melomgPath = SelectNewerBinary(melomgPath, full);
            }
            else
            {
                customExes.Add(full);
            }
        }

        var entries = new List<ManagedAppEntry>
        {
            BuildManagedSlot(
                id: "mby",
                kind: ManagedAppKind.Mby,
                displayName: "Meloberry cli",
                defaultFileName: "mby-cli.exe",
                installedPath: mbyPath,
                workspaceExeNames: workspaceExeNames,
                repoKey: EcosystemCatalog.Mby.Key,
                description: EcosystemCatalog.Mby.Description),
            BuildManagedSlot(
                id: "melomg",
                kind: ManagedAppKind.Melomg,
                displayName: "Meloberry melomg",
                defaultFileName: "meloberry-melomg.exe",
                installedPath: melomgPath,
                workspaceExeNames: workspaceExeNames,
                repoKey: EcosystemCatalog.Melomg.Key,
                description: EcosystemCatalog.Melomg.Description)
        };

        foreach (var custom in customExes)
        {
            var fileName = Path.GetFileName(custom);
            entries.Add(new ManagedAppEntry(
                Id: $"custom:{fileName.ToLowerInvariant()}",
                Kind: ManagedAppKind.CustomExecutable,
                DisplayName: Path.GetFileNameWithoutExtension(custom),
                FileName: fileName,
                FullPath: custom,
                Version: ExtractVersion(custom),
                IsInstalled: true,
                IsPresentInWorkspace: workspaceExeNames.Contains(fileName),
                RepoKey: null,
                Description: "Imported local executable."));
        }

        return Task.FromResult<IReadOnlyList<ManagedAppEntry>>(entries);
    }

    public async Task<Result<ManagedAppEntry>> ImportLocalExecutableAsync(string sourcePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return Result<ManagedAppEntry>.Failure(WrapperErrors.InvalidPath("Executable source path is empty."));
        }

        string fullSource;
        try
        {
            fullSource = Path.GetFullPath(sourcePath.Trim());
        }
        catch
        {
            return Result<ManagedAppEntry>.Failure(WrapperErrors.InvalidPath(sourcePath));
        }

        if (!File.Exists(fullSource) || !fullSource.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return Result<ManagedAppEntry>.Failure(WrapperErrors.InvalidPath($"'{sourcePath}' is not an existing .exe file."));
        }

        if (!await HasPeHeaderAsync(fullSource, ct).ConfigureAwait(false))
        {
            return Result<ManagedAppEntry>.Failure(WrapperErrors.UpdateVerificationFailed($"'{fullSource}' is not a valid Windows PE executable (missing MZ header)."));
        }

        var (managedKey, resolvedFileName) = await IdentifyExecutableMetadataAsync(fullSource, ct).ConfigureAwait(false);
        Directory.CreateDirectory(paths.AppsDirectory);
        var destPath = Path.Combine(paths.AppsDirectory, resolvedFileName);

        if (managedKey is not null)
        {
            DeleteSupersededManagedBinaries(managedKey, keepFileName: resolvedFileName);
        }

        if (!string.Equals(fullSource, destPath, StringComparison.OrdinalIgnoreCase))
        {
            var tmp = destPath + ".tmp";
            File.Copy(fullSource, tmp, overwrite: true);
            File.Move(tmp, destPath, overwrite: true);
        }

        return await BuildEntryForSavedFileAsync(managedKey, destPath, ct).ConfigureAwait(false);
    }

    public async Task<Result<ManagedAppEntry>> SaveDownloadedToolAsync(string toolKey, string fileName, string tempFilePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return Result<ManagedAppEntry>.Failure(WrapperErrors.InvalidPath($"Invalid downloaded binary name '{fileName}'."));
        }

        var safeName = Path.GetFileName(fileName);
        if (!File.Exists(tempFilePath))
        {
            return Result<ManagedAppEntry>.Failure(WrapperErrors.IoFailure($"Temporary download file missing: {tempFilePath}"));
        }

        Directory.CreateDirectory(paths.AppsDirectory);
        var destPath = Path.Combine(paths.AppsDirectory, safeName);

        DeleteSupersededManagedBinaries(toolKey, keepFileName: safeName);

        File.Move(tempFilePath, destPath, overwrite: true);
        return await BuildEntryForSavedFileAsync(toolKey, destPath, ct).ConfigureAwait(false);
    }

    public Task<Result<string>> UninstallAppAsync(string idOrFileName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idOrFileName))
        {
            return Task.FromResult(Result<string>.Failure(WrapperErrors.InvalidPath("App identifier cannot be empty.")));
        }

        if (!Directory.Exists(paths.AppsDirectory))
        {
            return Task.FromResult(Result<string>.Failure(WrapperErrors.CliNotFound(idOrFileName)));
        }

        var normalizedKey = idOrFileName.Trim();
        var appsRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.AppsDirectory)) + Path.DirectorySeparatorChar;
        var targets = new List<string>();

        foreach (var file in Directory.GetFiles(paths.AppsDirectory, "*.exe", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            var matches =
                string.Equals(normalizedKey, "mby", StringComparison.OrdinalIgnoreCase) && IsMbyFileName(name) ||
                string.Equals(normalizedKey, "melomg", StringComparison.OrdinalIgnoreCase) && IsMelomgFileName(name) ||
                string.Equals(normalizedKey, $"custom:{name}", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalizedKey, name, StringComparison.OrdinalIgnoreCase);

            if (matches)
            {
                var full = Path.GetFullPath(file);
                if (!full.StartsWith(appsRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(Result<string>.Failure(WrapperErrors.InvalidPath(full)));
                }

                targets.Add(full);
            }
        }

        if (targets.Count == 0)
        {
            return Task.FromResult(Result<string>.Failure(WrapperErrors.CliNotFound(idOrFileName)));
        }

        foreach (var target in targets)
        {
            File.Delete(target);
        }

        return Task.FromResult(Result<string>.Success(Path.GetFileName(targets[0])));
    }

    public async Task<string?> ResolveToolExecutableAsync(string toolKey, string? workspacePath, string? explicitOverride, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(explicitOverride) && File.Exists(explicitOverride))
        {
            return Path.GetFullPath(explicitOverride);
        }

        var entries = await ScanAppsAsync(workspacePath, ct).ConfigureAwait(false);
        var targetKind = string.Equals(toolKey, "melomg", StringComparison.OrdinalIgnoreCase)
            ? ManagedAppKind.Melomg
            : ManagedAppKind.Mby;

        var match = entries.FirstOrDefault(e => e.Kind == targetKind && e.IsInstalled && !string.IsNullOrWhiteSpace(e.FullPath));
        if (match?.FullPath is not null && File.Exists(match.FullPath))
        {
            if (!string.IsNullOrWhiteSpace(workspacePath) && Directory.Exists(workspacePath))
            {
                var wsCandidate = Path.Combine(workspacePath, match.FileName);
                if (File.Exists(wsCandidate))
                {
                    return Path.GetFullPath(wsCandidate);
                }
            }

            return Path.GetFullPath(match.FullPath);
        }

        if (!string.IsNullOrWhiteSpace(workspacePath) && Directory.Exists(workspacePath))
        {
            var direct = Directory.GetFiles(workspacePath, "*.exe", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f => targetKind == ManagedAppKind.Mby
                    ? IsMbyFileName(Path.GetFileName(f))
                    : IsMelomgFileName(Path.GetFileName(f)));
            if (direct is not null)
            {
                return Path.GetFullPath(direct);
            }
        }

        return null;
    }

    private static async Task<(string? ManagedKey, string FileName)> IdentifyExecutableMetadataAsync(string fullSource, CancellationToken ct)
    {
        var originalFileName = Path.GetFileName(fullSource);
        string? detectedKey = null;
        string? detectedVersion = null;

        // 1. Probe PE FileVersionInfo metadata first (zero process overhead).
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(fullSource);
            if (!string.IsNullOrWhiteSpace(vi.ProductName))
            {
                if (vi.ProductName.Contains("Meloberry CLI", StringComparison.OrdinalIgnoreCase))
                {
                    detectedKey = "mby";
                }
                else if (vi.ProductName.Contains("Meloberry Melomg", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(vi.CompanyName, "melomg", StringComparison.OrdinalIgnoreCase))
                {
                    detectedKey = "melomg";
                }
            }

            if (!string.IsNullOrWhiteSpace(vi.ProductVersion))
            {
                var plus = vi.ProductVersion.IndexOf('+');
                var v = plus > 0 ? vi.ProductVersion[..plus] : vi.ProductVersion;
                if (SemverRegex().IsMatch(v))
                {
                    detectedVersion = SemverRegex().Match(v).Groups[1].Value;
                }
            }
        }
        catch
        {
            // Ignore unreadable version info.
        }

        // 2. Execute `--version` with MBY_AGENT=1 for authoritative tool & version identification
        //    whenever PE metadata wasn't conclusive (e.g., renamed .exe).
        if (detectedKey is null || string.IsNullOrWhiteSpace(detectedVersion))
        {
            var cliProbe = await TryProbeCliVersionAsync(fullSource, ct).ConfigureAwait(false);
            if (cliProbe.ToolKey is not null)
            {
                detectedKey = cliProbe.ToolKey;
            }

            if (!string.IsNullOrWhiteSpace(cliProbe.Version))
            {
                detectedVersion = cliProbe.Version;
            }
        }

        // 3. Fallback filename check (preserves unit test dummy PE binaries).
        detectedKey ??= IsMbyFileName(originalFileName) ? "mby" : IsMelomgFileName(originalFileName) ? "melomg" : null;

        if (detectedKey is null)
        {
            return (null, originalFileName);
        }

        var alreadyMatchesCanonical = string.Equals(detectedKey, "mby", StringComparison.OrdinalIgnoreCase)
            ? IsMbyFileName(originalFileName)
            : IsMelomgFileName(originalFileName);

        if (alreadyMatchesCanonical)
        {
            return (detectedKey, originalFileName);
        }

        var verSuffix = !string.IsNullOrWhiteSpace(detectedVersion) ? $"-{detectedVersion}" : string.Empty;
        var canonical = string.Equals(detectedKey, "mby", StringComparison.OrdinalIgnoreCase)
            ? $"mby-cli{verSuffix}.exe"
            : $"meloberry-melomg{verSuffix}.exe";
        return (detectedKey, canonical);
    }

    private static async Task<(string? ToolKey, string? Version)> TryProbeCliVersionAsync(string exePath, CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.Environment["MBY_AGENT"] = "1";
            psi.ArgumentList.Add("--version");

            using var proc = new Process { StartInfo = psi };
            if (!proc.Start())
            {
                return (null, null);
            }

            var outTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var errTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var stdout = await outTask.ConfigureAwait(false);
            _ = await errTask.ConfigureAwait(false);

            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                return (null, null);
            }

            using var doc = JsonDocument.Parse(stdout);
            if (!doc.RootElement.TryGetProperty("data", out var data))
            {
                return (null, null);
            }

            string? key = null;
            if (data.TryGetProperty("executableName", out var exeEl) &&
                string.Equals(exeEl.GetString(), "mby", StringComparison.OrdinalIgnoreCase))
            {
                key = "mby";
            }
            else if (data.TryGetProperty("tool", out var toolEl) &&
                     string.Equals(toolEl.GetString(), "melomg", StringComparison.OrdinalIgnoreCase))
            {
                key = "melomg";
            }

            var ver = data.TryGetProperty("version", out var verEl) ? verEl.GetString() : null;
            return (key, ver);
        }
        catch
        {
            return (null, null);
        }
    }

    private async Task<Result<ManagedAppEntry>> BuildEntryForSavedFileAsync(string? toolKey, string destPath, CancellationToken ct)
    {
        var entries = await ScanAppsAsync(null, ct).ConfigureAwait(false);
        var fileName = Path.GetFileName(destPath);
        var found = entries.FirstOrDefault(e => string.Equals(e.FileName, fileName, StringComparison.OrdinalIgnoreCase))
            ?? entries.FirstOrDefault(e => string.Equals(e.Id, toolKey, StringComparison.OrdinalIgnoreCase) && e.IsInstalled);

        return found is not null
            ? Result<ManagedAppEntry>.Success(found)
            : Result<ManagedAppEntry>.Failure(WrapperErrors.IoFailure($"Saved binary '{destPath}' could not be indexed."));
    }

    private void DeleteSupersededManagedBinaries(string toolKey, string keepFileName)
    {
        if (!Directory.Exists(paths.AppsDirectory))
        {
            return;
        }

        var isMby = string.Equals(toolKey, "mby", StringComparison.OrdinalIgnoreCase);
        foreach (var existing in Directory.GetFiles(paths.AppsDirectory, "*.exe", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(existing);
            if (string.Equals(name, keepFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var matchesGroup = isMby ? IsMbyFileName(name) : IsMelomgFileName(name);
            if (matchesGroup)
            {
                File.Delete(existing);
            }
        }
    }

    private static ManagedAppEntry BuildManagedSlot(
        string id,
        ManagedAppKind kind,
        string displayName,
        string defaultFileName,
        string? installedPath,
        HashSet<string> workspaceExeNames,
        string repoKey,
        string description)
    {
        var fileName = installedPath is not null ? Path.GetFileName(installedPath) : defaultFileName;
        var inWorkspace = installedPath is not null
            ? workspaceExeNames.Contains(fileName)
            : workspaceExeNames.Any(n => kind == ManagedAppKind.Mby ? IsMbyFileName(n) : IsMelomgFileName(n));

        return new ManagedAppEntry(
            Id: id,
            Kind: kind,
            DisplayName: displayName,
            FileName: fileName,
            FullPath: installedPath,
            Version: installedPath is not null ? ExtractVersion(installedPath) : null,
            IsInstalled: installedPath is not null,
            IsPresentInWorkspace: inWorkspace,
            RepoKey: repoKey,
            Description: description);
    }

    internal static bool IsMbyFileName(string fileName) =>
        fileName.Equals("mby.exe", StringComparison.OrdinalIgnoreCase) ||
        fileName.StartsWith("mby-cli", StringComparison.OrdinalIgnoreCase);

    internal static bool IsMelomgFileName(string fileName) =>
        fileName.Equals("melomg.exe", StringComparison.OrdinalIgnoreCase) ||
        fileName.StartsWith("meloberry-melomg", StringComparison.OrdinalIgnoreCase);

    private static string? SelectNewerBinary(string? currentPath, string candidatePath)
    {
        if (currentPath is null)
        {
            return candidatePath;
        }

        var curVer = ParseVersionSafe(ExtractVersion(currentPath));
        var newVer = ParseVersionSafe(ExtractVersion(candidatePath));
        if (newVer > curVer)
        {
            return candidatePath;
        }

        if (newVer == curVer && File.GetLastWriteTimeUtc(candidatePath) > File.GetLastWriteTimeUtc(currentPath))
        {
            return candidatePath;
        }

        return currentPath;
    }

    private static string? ExtractVersion(string fullPath)
    {
        var match = SemverRegex().Match(Path.GetFileNameWithoutExtension(fullPath));
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(fullPath);
            if (!string.IsNullOrWhiteSpace(info.ProductVersion))
            {
                var plus = info.ProductVersion.IndexOf('+');
                return plus > 0 ? info.ProductVersion[..plus] : info.ProductVersion;
            }
        }
        catch
        {
            // Ignore unreadable PE metadata.
        }

        return null;
    }

    private static Version ParseVersionSafe(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new Version(0, 0, 0);
        }

        var dash = raw.IndexOf('-');
        var core = dash > 0 ? raw[..dash] : raw;
        return Version.TryParse(core, out var v) ? v : new Version(0, 0, 0);
    }

    private static async Task<bool> HasPeHeaderAsync(string path, CancellationToken ct)
    {
        var buffer = new byte[2];
        await using var fs = File.OpenRead(path);
        var read = await fs.ReadAsync(buffer.AsMemory(0, 2), ct).ConfigureAwait(false);
        return read == 2 && buffer[0] == (byte)'M' && buffer[1] == (byte)'Z';
    }
}
