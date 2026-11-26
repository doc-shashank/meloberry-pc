using System.Net;
using System.Text.Json;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;
using Meloberry.Wrapper.Infrastructure.Common;

namespace Meloberry.Wrapper.Infrastructure.GitHub;

/// <summary>Checks `doc-shashank/meloberry-pc` releases and downloads Inno Setup installers.</summary>
public sealed class GitHubSelfUpdater(
    GitHubHttpClientHelper httpHelper,
    IWrapperDataPaths paths) : IGitHubSelfUpdater
{
    public async Task<Result<AppUpdateCheckResult>> CheckForUpdateAsync(
        string owner,
        string repo,
        string currentVersion,
        CancellationToken ct = default)
    {
        if (!Application.UseCases.SettingsAndPresetsHandler.IsValidGitHubIdentifier(owner) ||
            !Application.UseCases.SettingsAndPresetsHandler.IsValidGitHubIdentifier(repo))
        {
            return Result<AppUpdateCheckResult>.Failure(WrapperErrors.InvalidUrl($"https://github.com/{owner}/{repo}"));
        }

        var slug = $"{owner.Trim()}/{repo.Trim()}";
        var releasePageUrl = $"https://github.com/{slug}/releases";
        var apiUrl = $"https://api.github.com/repos/{slug}/releases/latest";

        try
        {
            using var resp = await httpHelper.SendWithAllowlistedRedirectsAsync(apiUrl, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                return Result<AppUpdateCheckResult>.Success(new AppUpdateCheckResult(
                    CurrentVersion: currentVersion,
                    RepoSlug: slug,
                    ReleasePageUrl: releasePageUrl,
                    UpdateAvailable: false,
                    RepoNotPublished: true,
                    StatusMessage: $"No releases published yet for {slug}."));
            }

            if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                return Result<AppUpdateCheckResult>.Failure(WrapperErrors.RateLimited(slug, releasePageUrl));
            }

            if (!resp.IsSuccessStatusCode)
            {
                return Result<AppUpdateCheckResult>.Failure(
                    WrapperErrors.DownloadFailed($"GitHub returned HTTP {(int)resp.StatusCode} for {slug}.", $"Check {releasePageUrl}"));
            }

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
            var latestVer = GitHubHttpClientHelper.ExtractVersionFromText(tag) ?? tag.TrimStart('v', 'V');

            string? assetName = null;
            string? downloadUrl = null;
            long? sizeBytes = null;
            string? digest = null;
            string? bestVer = null;

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                    {
                        continue;
                    }

                    if (name.StartsWith("mby-pc-setup-", StringComparison.OrdinalIgnoreCase) &&
                        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        var candidateVer = GitHubHttpClientHelper.ExtractVersionFromText(name) ?? latestVer;
                        if (downloadUrl is null || GitHubHttpClientHelper.CompareSemver(candidateVer, bestVer) > 0)
                        {
                            assetName = name;
                            downloadUrl = url;
                            sizeBytes = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : null;
                            digest = asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                            bestVer = candidateVer;
                            latestVer = candidateVer;
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                return Result<AppUpdateCheckResult>.Success(new AppUpdateCheckResult(
                    CurrentVersion: currentVersion,
                    RepoSlug: slug,
                    ReleasePageUrl: releasePageUrl,
                    UpdateAvailable: false,
                    RepoNotPublished: false,
                    StatusMessage: $"Release '{tag}' exists on {slug}, but no mby-pc-setup-*.exe installer asset was attached.",
                    LatestVersion: latestVer));
            }

            var isNewer = GitHubHttpClientHelper.CompareSemver(latestVer, currentVersion) > 0;
            var status = isNewer
                ? $"Update v{latestVer} is available (current v{currentVersion})."
                : $"You are up to date on v{currentVersion} (latest: v{latestVer}).";

            return Result<AppUpdateCheckResult>.Success(new AppUpdateCheckResult(
                CurrentVersion: currentVersion,
                RepoSlug: slug,
                ReleasePageUrl: releasePageUrl,
                UpdateAvailable: isNewer,
                RepoNotPublished: false,
                StatusMessage: status,
                LatestVersion: latestVer,
                AssetFileName: assetName,
                DownloadUrl: downloadUrl,
                SizeBytes: sizeBytes,
                Sha256Hex: digest));
        }
        catch (Exception ex)
        {
            return Result<AppUpdateCheckResult>.Failure(
                WrapperErrors.DownloadFailed($"Failed to check {slug} for updates: {ex.Message}", $"Visit {releasePageUrl}"));
        }
    }

    public async Task<Result<string>> DownloadInstallerAsync(
        AppUpdateCheckResult check,
        Action<DownloadProgressSnapshot> progressCallback,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(check.DownloadUrl) || string.IsNullOrWhiteSpace(check.AssetFileName))
        {
            return Result<string>.Failure(WrapperErrors.DownloadFailed("No installer asset URL available."));
        }

        var tmpRes = await httpHelper.DownloadVerifiedExecutableAsync(
            "self-update",
            check.DownloadUrl,
            check.AssetFileName,
            check.SizeBytes,
            check.Sha256Hex,
            paths.UpdatesDirectory,
            progressCallback,
            ct).ConfigureAwait(false);

        if (tmpRes.IsFailure)
        {
            return tmpRes;
        }

        var finalFileName = Path.GetFileName(check.AssetFileName);
        var finalPath = Path.Combine(paths.UpdatesDirectory, finalFileName);
        try
        {
            AtomicFileOps.MoveTempIntoPlace(tmpRes.Value, finalPath);
            CleanupStaleInstallers(paths.UpdatesDirectory, finalFileName);
            return Result<string>.Success(finalPath);
        }
        catch (Exception ex)
        {
            return Result<string>.Failure(WrapperErrors.IoFailure($"Failed to finalize installer download: {ex.Message}"));
        }
    }

    private static void CleanupStaleInstallers(string updatesDirectory, string keepFileName)
    {
        if (!Directory.Exists(updatesDirectory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(updatesDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            if (string.Equals(name, keepFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isOldInstaller = name.StartsWith("mby-pc-setup-", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            var isOrphanTemp = name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
            if (!isOldInstaller && !isOrphanTemp)
            {
                continue;
            }

            try
            {
                File.Delete(file);
            }
            catch
            {
                // Best-effort cleanup; ignore files locked by OS or running installer.
            }
        }
    }
}
