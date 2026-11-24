using System.Collections.Concurrent;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Application.UseCases;

/// <summary>Manages scanning, downloading, updating, importing, and uninstalling tools inside apps\.</summary>
public sealed class ManagedAppsHandler(
    IManagedAppsStore appsStore,
    IGitHubToolDownloader downloader,
    ISettingsStore settingsStore)
{
    private readonly ConcurrentDictionary<string, DownloadProgressSnapshot> _progress = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<ManagedAppEntry>> ListAppsAsync(CancellationToken ct = default)
    {
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        return await appsStore.ScanAppsAsync(settings.ActiveWorkspace?.WorkspacePath, ct).ConfigureAwait(false);
    }

    public DownloadProgressSnapshot? GetDownloadStatus(string toolKey) =>
        _progress.TryGetValue(toolKey, out var snap) ? snap : null;

    public async Task<Result<ManagedAppEntry>> ImportLocalExecutableAsync(string sourcePath, CancellationToken ct = default) =>
        await appsStore.ImportLocalExecutableAsync(sourcePath, ct).ConfigureAwait(false);

    public async Task<Result<string>> UninstallAppAsync(string idOrFileName, CancellationToken ct = default) =>
        await appsStore.UninstallAppAsync(idOrFileName, ct).ConfigureAwait(false);

    public async Task<Result<ToolUpdateCheckResult>> CheckToolUpdateAsync(string toolKey, CancellationToken ct = default)
    {
        if (!string.Equals(toolKey, "mby", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(toolKey, "melomg", StringComparison.OrdinalIgnoreCase))
        {
            return Result<ToolUpdateCheckResult>.Failure(WrapperErrors.DownloadFailed($"Unsupported tool '{toolKey}'."));
        }

        var apps = await ListAppsAsync(ct).ConfigureAwait(false);
        var current = apps.FirstOrDefault(a => string.Equals(a.Id, toolKey, StringComparison.OrdinalIgnoreCase) && a.IsInstalled);
        var installedVer = current?.Version;

        var check = await downloader.CheckLatestToolAsync(toolKey, ct).ConfigureAwait(false);
        if (check.IsFailure)
        {
            return Result<ToolUpdateCheckResult>.Failure(check.Error!);
        }

        var candidate = check.Value;
        var latestVer = candidate.ExtractedVersion;
        var isNewer = current is null || CompareSemver(latestVer, installedVer) > 0;
        var message = isNewer
            ? $"Update v{latestVer ?? candidate.TagOrRef} available."
            : $"Up to date (v{installedVer}).";

        return Result<ToolUpdateCheckResult>.Success(new ToolUpdateCheckResult(
            ToolKey: toolKey,
            IsInstalled: current is not null,
            InstalledVersion: installedVer,
            UpdateAvailable: isNewer,
            LatestVersion: latestVer,
            AssetFileName: candidate.AssetFileName,
            StatusMessage: message));
    }

    public async Task<Result<ManagedAppEntry>> DownloadToolAsync(string toolKey, CancellationToken ct = default)
    {
        if (!string.Equals(toolKey, "mby", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(toolKey, "melomg", StringComparison.OrdinalIgnoreCase))
        {
            return Result<ManagedAppEntry>.Failure(WrapperErrors.DownloadFailed($"Unsupported downloadable tool '{toolKey}'."));
        }

        _progress[toolKey] = new DownloadProgressSnapshot(toolKey, "checking", $"Checking GitHub for latest {toolKey} release...");

        var check = await downloader.CheckLatestToolAsync(toolKey, ct).ConfigureAwait(false);
        if (check.IsFailure)
        {
            _progress[toolKey] = new DownloadProgressSnapshot(toolKey, "error", check.Error!.Description, IsCompleted: true, IsError: true);
            return Result<ManagedAppEntry>.Failure(check.Error!);
        }

        var candidate = check.Value;
        _progress[toolKey] = new DownloadProgressSnapshot(toolKey, "downloading", $"Downloading {candidate.AssetFileName}...", 0, candidate.SizeBytes);

        var tempDownload = await downloader.DownloadToTempFileAsync(
            candidate,
            snap => _progress[toolKey] = snap,
            ct).ConfigureAwait(false);

        if (tempDownload.IsFailure)
        {
            _progress[toolKey] = new DownloadProgressSnapshot(toolKey, "error", tempDownload.Error!.Description, IsCompleted: true, IsError: true);
            return Result<ManagedAppEntry>.Failure(tempDownload.Error!);
        }

        var saved = await appsStore.SaveDownloadedToolAsync(toolKey, candidate.AssetFileName, tempDownload.Value, ct).ConfigureAwait(false);
        if (saved.IsFailure)
        {
            _progress[toolKey] = new DownloadProgressSnapshot(toolKey, "error", saved.Error!.Description, IsCompleted: true, IsError: true);
            return Result<ManagedAppEntry>.Failure(saved.Error!);
        }

        _progress[toolKey] = new DownloadProgressSnapshot(
            toolKey,
            "completed",
            $"Installed {saved.Value.FileName} to apps\\",
            candidate.SizeBytes ?? 0,
            candidate.SizeBytes,
            saved.Value.FullPath,
            IsCompleted: true,
            IsError: false);

        return saved;
    }

    public async Task<string?> ResolveExecutableAsync(string toolKey, string? workspacePathOverride = null, CancellationToken ct = default)
    {
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var wsPath = workspacePathOverride ?? settings.ActiveWorkspace?.WorkspacePath;
        var explicitOverride = string.Equals(toolKey, "mby", StringComparison.OrdinalIgnoreCase)
            ? settings.MbyExecutableOverride
            : settings.MelomgExecutableOverride;

        return await appsStore.ResolveToolExecutableAsync(toolKey, wsPath, explicitOverride, ct).ConfigureAwait(false);
    }

    private static int CompareSemver(string? left, string? right)
    {
        return ParseCore(left).CompareTo(ParseCore(right));

        static Version ParseCore(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new Version(0, 0, 0);
            }

            var t = raw.Trim().TrimStart('v', 'V');
            var dash = t.IndexOf('-');
            if (dash > 0)
            {
                t = t[..dash];
            }

            return Version.TryParse(t, out var v) ? v : new Version(0, 0, 0);
        }
    }
}
