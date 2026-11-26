using System.Diagnostics;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;
using Meloberry.Wrapper.Infrastructure.Common;

namespace Meloberry.Wrapper.Infrastructure.Workspace;

/// <summary>
/// Manages canonical CLI executables stored in %LOCALAPPDATA%\Meloberry\apps\ including
/// metadata-based universal import detection and clean uninstall.
/// </summary>
public sealed class ManagedAppsStore(IWrapperDataPaths paths) : IManagedAppsStore
{

    public Task<IReadOnlyList<ManagedAppEntry>> ScanAppsAsync(string? activeWorkspacePath, CancellationToken ct = default)
    {
        Directory.CreateDirectory(paths.AppsDirectory);
        var exeFiles = Directory.GetFiles(paths.AppsDirectory, "*.exe", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var workspaceExeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? wsMbyPath = null;
        string? wsMelomgPath = null;
        if (!string.IsNullOrWhiteSpace(activeWorkspacePath) && Directory.Exists(activeWorkspacePath))
        {
            foreach (var wsExe in Directory.GetFiles(activeWorkspacePath, "*.exe", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var wsName = Path.GetFileName(wsExe);
                workspaceExeNames.Add(wsName);
                if (IsMbyFileName(wsName))
                {
                    wsMbyPath = SelectNewerBinary(wsMbyPath, wsExe);
                }
                else if (IsMelomgFileName(wsName))
                {
                    wsMelomgPath = SelectNewerBinary(wsMelomgPath, wsExe);
                }
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
                matchedWsFileName: wsMbyPath is not null ? Path.GetFileName(wsMbyPath) : null,
                repoKey: EcosystemCatalog.Mby.Key,
                description: EcosystemCatalog.Mby.Description),
            BuildManagedSlot(
                id: "melomg",
                kind: ManagedAppKind.Melomg,
                displayName: "Meloberry melomg",
                defaultFileName: "meloberry-melomg.exe",
                installedPath: melomgPath,
                matchedWsFileName: wsMelomgPath is not null ? Path.GetFileName(wsMelomgPath) : null,
                repoKey: EcosystemCatalog.Melomg.Key,
                description: EcosystemCatalog.Melomg.Description)
        };

        foreach (var custom in customExes)
        {
            var fileName = Path.GetFileName(custom);
            var presentInWs = workspaceExeNames.Contains(fileName);
            entries.Add(new ManagedAppEntry(
                Id: $"custom:{fileName.ToLowerInvariant()}",
                Kind: ManagedAppKind.CustomExecutable,
                DisplayName: Path.GetFileNameWithoutExtension(custom),
                FileName: fileName,
                FullPath: custom,
                Version: ExtractVersion(custom),
                IsInstalled: true,
                IsPresentInWorkspace: presentInWs,
                RepoKey: null,
                Description: "Imported local executable.",
                WorkspaceFileName: presentInWs ? fileName : null));
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

        var (managedKey, resolvedFileName) = IdentifyExecutableMetadata(fullSource);
        Directory.CreateDirectory(paths.AppsDirectory);
        var destPath = Path.Combine(paths.AppsDirectory, resolvedFileName);

        if (!string.Equals(fullSource, destPath, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                AtomicFileOps.CopyFile(fullSource, destPath);
            }
            catch (Exception ex)
            {
                return Result<ManagedAppEntry>.Failure(WrapperErrors.IoFailure($"Failed to import '{resolvedFileName}': {ex.Message}"));
            }
        }

        if (managedKey is not null)
        {
            DeleteSupersededManagedBinaries(managedKey, keepFileName: resolvedFileName);
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

        try
        {
            File.Move(tempFilePath, destPath, overwrite: true);
        }
        catch (Exception ex)
        {
            return Result<ManagedAppEntry>.Failure(WrapperErrors.IoFailure($"Failed to install '{safeName}': {ex.Message}"));
        }

        DeleteSupersededManagedBinaries(toolKey, keepFileName: safeName);

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

        try
        {
            foreach (var target in targets)
            {
                File.Delete(target);
            }
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result<string>.Failure(WrapperErrors.IoFailure($"Failed to uninstall '{idOrFileName}': {ex.Message}")));
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

        var entry = entries.FirstOrDefault(e => e.Kind == targetKind);
        if (!string.IsNullOrWhiteSpace(workspacePath) && Directory.Exists(workspacePath))
        {
            if (!string.IsNullOrWhiteSpace(entry?.WorkspaceFileName))
            {
                var wsCandidate = Path.Combine(workspacePath, entry.WorkspaceFileName);
                if (File.Exists(wsCandidate))
                {
                    return Path.GetFullPath(wsCandidate);
                }
            }

            var direct = Directory.GetFiles(workspacePath, "*.exe", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f => targetKind == ManagedAppKind.Mby
                    ? IsMbyFileName(Path.GetFileName(f))
                    : IsMelomgFileName(Path.GetFileName(f)));
            if (direct is not null)
            {
                return Path.GetFullPath(direct);
            }
        }

        if (entry is { IsInstalled: true, FullPath: not null } && File.Exists(entry.FullPath))
        {
            return Path.GetFullPath(entry.FullPath);
        }

        return null;
    }

    private static (string? ManagedKey, string FileName) IdentifyExecutableMetadata(string fullSource)
    {
        var originalFileName = Path.GetFileName(fullSource);
        string? detectedKey = null;
        string? detectedVersion = null;

        // Passive PE FileVersionInfo metadata inspection only (never execute untrusted imported binaries).
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(fullSource);
            if ((!string.IsNullOrWhiteSpace(vi.ProductName) && vi.ProductName.Contains("Meloberry CLI", StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(vi.InternalName) && vi.InternalName.StartsWith("mby-cli", StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(vi.OriginalFilename) && vi.OriginalFilename.StartsWith("mby-cli", StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(vi.FileDescription) && vi.FileDescription.StartsWith("mby-cli", StringComparison.OrdinalIgnoreCase)))
            {
                detectedKey = "mby";
            }
            else if ((!string.IsNullOrWhiteSpace(vi.ProductName) && vi.ProductName.Contains("Meloberry Melomg", StringComparison.OrdinalIgnoreCase)) ||
                     string.Equals(vi.CompanyName, "melomg", StringComparison.OrdinalIgnoreCase) ||
                     (!string.IsNullOrWhiteSpace(vi.InternalName) && vi.InternalName.StartsWith("meloberry-melomg", StringComparison.OrdinalIgnoreCase)) ||
                     (!string.IsNullOrWhiteSpace(vi.OriginalFilename) && vi.OriginalFilename.StartsWith("meloberry-melomg", StringComparison.OrdinalIgnoreCase)))
            {
                detectedKey = "melomg";
            }

            var rawVer = !string.IsNullOrWhiteSpace(vi.ProductVersion) ? vi.ProductVersion : vi.FileVersion;
            if (!string.IsNullOrWhiteSpace(rawVer))
            {
                detectedVersion = SemverComparer.ExtractVersion(rawVer);
            }
        }
        catch
        {
            // Ignore unreadable PE version info.
        }

        // Fallback filename check.
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
                try { File.Delete(existing); } catch { }
            }
        }
    }

    private static ManagedAppEntry BuildManagedSlot(
        string id,
        ManagedAppKind kind,
        string displayName,
        string defaultFileName,
        string? installedPath,
        string? matchedWsFileName,
        string repoKey,
        string description)
    {
        var fileName = installedPath is not null
            ? Path.GetFileName(installedPath)
            : matchedWsFileName ?? defaultFileName;
        var inWorkspace = matchedWsFileName is not null;

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
            Description: description,
            WorkspaceFileName: matchedWsFileName);
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

        var cmp = SemverComparer.Compare(ExtractVersion(candidatePath), ExtractVersion(currentPath));
        if (cmp > 0)
        {
            return candidatePath;
        }

        if (cmp == 0 && File.GetLastWriteTimeUtc(candidatePath) > File.GetLastWriteTimeUtc(currentPath))
        {
            return candidatePath;
        }

        return currentPath;
    }

    internal static string? ExtractVersion(string fullPath)
    {
        var nameVer = SemverComparer.ExtractVersion(Path.GetFileNameWithoutExtension(fullPath));
        if (nameVer is not null)
        {
            return nameVer;
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(fullPath);
            var rawVer = !string.IsNullOrWhiteSpace(info.ProductVersion) ? info.ProductVersion : info.FileVersion;
            if (!string.IsNullOrWhiteSpace(rawVer))
            {
                var plus = rawVer.IndexOf('+');
                var cleaned = plus > 0 ? rawVer[..plus] : rawVer;
                return SemverComparer.ExtractVersion(cleaned) ?? cleaned;
            }
        }
        catch
        {
            // Ignore unreadable PE metadata.
        }

        return null;
    }

    private static async Task<bool> HasPeHeaderAsync(string path, CancellationToken ct)
    {
        var buffer = new byte[2];
        await using var fs = File.OpenRead(path);
        var read = await fs.ReadAsync(buffer.AsMemory(0, 2), ct).ConfigureAwait(false);
        return read == 2 && buffer[0] == (byte)'M' && buffer[1] == (byte)'Z';
    }
}
