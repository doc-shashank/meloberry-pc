using System.Net;
using System.Text.Json;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Infrastructure.GitHub;

/// <summary>Downloads `mby` and `melomg` executables from GitHub Releases API or repo `/contents/app` fallback.</summary>
public sealed class GitHubToolDownloader(
    GitHubHttpClientHelper httpHelper,
    IWrapperDataPaths paths) : IGitHubToolDownloader
{
    public async Task<Result<ToolReleaseCandidate>> CheckLatestToolAsync(string toolKey, CancellationToken ct = default)
    {
        var repo = EcosystemCatalog.FindByKey(toolKey);
        if (repo is null || !repo.Downloadable || string.IsNullOrWhiteSpace(repo.BinaryPrefix))
        {
            return Result<ToolReleaseCandidate>.Failure(WrapperErrors.DownloadFailed($"Unknown tool repository '{toolKey}'."));
        }

        var releaseApi = $"https://api.github.com/repos/{repo.Owner}/{repo.Repo}/releases/latest";
        try
        {
            using var resp = await httpHelper.SendWithAllowlistedRedirectsAsync(releaseApi, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                return Result<ToolReleaseCandidate>.Failure(WrapperErrors.RateLimited(repo.Slug, repo.ReleasesUrl));
            }

            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var fromRelease = TrySelectReleaseAsset(toolKey, repo.BinaryPrefix!, body);
                if (fromRelease is not null)
                {
                    return Result<ToolReleaseCandidate>.Success(fromRelease);
                }
            }
            else if (resp.StatusCode != HttpStatusCode.NotFound)
            {
                return Result<ToolReleaseCandidate>.Failure(
                    WrapperErrors.DownloadFailed($"GitHub Releases API returned HTTP {(int)resp.StatusCode}.", $"Check {repo.ReleasesUrl}"));
            }

            var contentsApi = $"https://api.github.com/repos/{repo.Owner}/{repo.Repo}/contents/app";
            using var contentsResp = await httpHelper.SendWithAllowlistedRedirectsAsync(contentsApi, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (contentsResp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                return Result<ToolReleaseCandidate>.Failure(WrapperErrors.RateLimited(repo.Slug, repo.ReleasesUrl));
            }

            if (!contentsResp.IsSuccessStatusCode)
            {
                return Result<ToolReleaseCandidate>.Failure(
                    WrapperErrors.ReleaseNotFound(repo.Slug));
            }

            var contentsBody = await contentsResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var fromContents = TrySelectContentsAppBinary(toolKey, repo.BinaryPrefix!, contentsBody);
            return fromContents is not null
                ? Result<ToolReleaseCandidate>.Success(fromContents)
                : Result<ToolReleaseCandidate>.Failure(WrapperErrors.ReleaseNotFound(repo.Slug));
        }
        catch (Exception ex)
        {
            return Result<ToolReleaseCandidate>.Failure(
                WrapperErrors.DownloadFailed($"Unable to query GitHub for {repo.Slug}: {ex.Message}", $"Visit {repo.ReleasesUrl}"));
        }
    }

    public Task<Result<string>> DownloadToTempFileAsync(
        ToolReleaseCandidate candidate,
        Action<DownloadProgressSnapshot> progressCallback,
        CancellationToken ct = default)
    {
        return httpHelper.DownloadVerifiedExecutableAsync(
            candidate.ToolKey,
            candidate.DownloadUrl,
            candidate.AssetFileName,
            candidate.SizeBytes,
            candidate.Sha256Hex,
            paths.AppsDirectory,
            progressCallback,
            ct);
    }

    private static ToolReleaseCandidate? TrySelectReleaseAsset(string toolKey, string prefix, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "latest" : "latest";
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        ToolReleaseCandidate? best = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
            var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            long? size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : null;
            var digest = asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            var candidate = new ToolReleaseCandidate(
                ToolKey: toolKey,
                Source: "releases/latest",
                TagOrRef: tag,
                AssetFileName: name,
                DownloadUrl: url,
                SizeBytes: size,
                Sha256Hex: digest,
                ExtractedVersion: GitHubHttpClientHelper.ExtractVersionFromText(name) ?? GitHubHttpClientHelper.ExtractVersionFromText(tag));

            if (best is null || GitHubHttpClientHelper.CompareSemver(candidate.ExtractedVersion, best.ExtractedVersion) > 0)
            {
                best = candidate;
            }
        }

        return best;
    }

    private static ToolReleaseCandidate? TrySelectContentsAppBinary(string toolKey, string prefix, string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        ToolReleaseCandidate? best = null;
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
            var url = item.TryGetProperty("download_url", out var u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            long? size = item.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : null;
            var ver = GitHubHttpClientHelper.ExtractVersionFromText(name);
            var candidate = new ToolReleaseCandidate(toolKey, "contents/app", "main", name, url, size, null, ver);

            if (best is null || GitHubHttpClientHelper.CompareSemver(candidate.ExtractedVersion, best.ExtractedVersion) > 0)
            {
                best = candidate;
            }
        }

        return best;
    }
}
