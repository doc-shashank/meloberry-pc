using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;
using Meloberry.Wrapper.Infrastructure.Common;

namespace Meloberry.Wrapper.Infrastructure.GitHub;

/// <summary>Shared HTTP allowlist, redirect validation, PE magic-byte, and SHA-256 download guards.</summary>
public sealed class GitHubHttpClientHelper
{
    public const long MaxDownloadBytes = 250L * 1024L * 1024L;

    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.github.com",
        "github.com",
        "objects.githubusercontent.com",
        "raw.githubusercontent.com",
        "release-assets.githubusercontent.com"
    };

    private readonly HttpClient _http;
    private readonly string _userAgent;

    public GitHubHttpClientHelper(IWrapperMetadataProvider metadataProvider, HttpMessageHandler? handler = null)
    {
        var meta = metadataProvider.GetMetadata();
        _userAgent = $"Meloberry-PC/{meta.Version}";
        _http = handler is not null
            ? new HttpClient(handler, disposeHandler: false)
            : new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: true);
        _http.Timeout = TimeSpan.FromMinutes(3);
    }

    public static bool IsAllowedGitHubUri(string url, out Uri? parsed)
    {
        parsed = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!AllowedHosts.Contains(uri.Host))
        {
            return false;
        }

        parsed = uri;
        return true;
    }

    public async Task<HttpResponseMessage> SendWithAllowlistedRedirectsAsync(
        string url,
        HttpCompletionOption completionOption,
        CancellationToken ct)
    {
        var currentUrl = url;
        for (var hop = 0; hop < 6; hop++)
        {
            if (!IsAllowedGitHubUri(currentUrl, out var uri) || uri is null)
            {
                throw new InvalidOperationException($"Blocked non-allowlisted download URL: {currentUrl}");
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            var response = await _http.SendAsync(req, completionOption, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308 &&
                response.Headers.Location is { } location)
            {
                var nextUri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                response.Dispose();
                currentUrl = nextUri.ToString();
                continue;
            }

            return response;
        }

        throw new InvalidOperationException("Too many HTTP redirects while contacting GitHub.");
    }

    public async Task<Result<string>> DownloadVerifiedExecutableAsync(
        string operationKey,
        string downloadUrl,
        string fileName,
        long? expectedSize,
        string? expectedSha256Hex,
        string destinationDirectory,
        Action<DownloadProgressSnapshot> progressCallback,
        CancellationToken ct)
    {
        if (!IsAllowedGitHubUri(downloadUrl, out _))
        {
            return Result<string>.Failure(WrapperErrors.InvalidUrl(downloadUrl));
        }

        try
        {
            using var resp = await SendWithAllowlistedRedirectsAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                return Result<string>.Failure(WrapperErrors.DownloadFailed("GitHub download was rate-limited or forbidden."));
            }

            if (!resp.IsSuccessStatusCode)
            {
                return Result<string>.Failure(WrapperErrors.DownloadFailed($"GitHub returned HTTP {(int)resp.StatusCode} while downloading {fileName}."));
            }

            var totalBytes = resp.Content.Headers.ContentLength ?? expectedSize;
            if (totalBytes is > MaxDownloadBytes)
            {
                return Result<string>.Failure(WrapperErrors.UpdateVerificationFailed($"Asset exceeds maximum allowed size ({MaxDownloadBytes / (1024 * 1024)} MB)."));
            }

            Directory.CreateDirectory(destinationDirectory);
            var safeName = Path.GetFileName(fileName);
            var tmpPath = Path.Combine(destinationDirectory, $"{safeName}.{Guid.NewGuid():N}.tmp");
            var succeeded = false;

            try
            {
                await using (var networkStream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var fs = File.Create(tmpPath))
                using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    var buffer = new byte[81920];
                    var peHeader = new byte[2];
                    var peHeaderRead = 0;
                    var peVerified = false;
                    long received = 0;
                    int read;
                    while ((read = await networkStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        received += read;
                        if (received > MaxDownloadBytes)
                        {
                            return Result<string>.Failure(WrapperErrors.UpdateVerificationFailed("Downloaded stream exceeded 250 MB safety cap."));
                        }

                        if (!peVerified)
                        {
                            var needed = Math.Min(2 - peHeaderRead, read);
                            for (var i = 0; i < needed; i++)
                            {
                                peHeader[peHeaderRead++] = buffer[i];
                            }

                            if (peHeaderRead == 2)
                            {
                                if (peHeader[0] != (byte)'M' || peHeader[1] != (byte)'Z')
                                {
                                    return Result<string>.Failure(WrapperErrors.UpdateVerificationFailed($"'{safeName}' does not have a valid Windows PE executable header (MZ)."));
                                }

                                peVerified = true;
                            }
                        }

                        await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        hasher.AppendData(buffer, 0, read);

                        progressCallback(new DownloadProgressSnapshot(
                            operationKey,
                            "downloading",
                            $"Downloading {safeName}...",
                            received,
                            totalBytes));
                    }

                    if (received < 2 || !peVerified)
                    {
                        return Result<string>.Failure(WrapperErrors.UpdateVerificationFailed($"Downloaded file '{safeName}' is empty or missing PE header."));
                    }

                    var actualHash = Convert.ToHexStringLower(hasher.GetHashAndReset());
                    if (!string.IsNullOrWhiteSpace(expectedSha256Hex))
                    {
                        var normExpected = expectedSha256Hex.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                            ? expectedSha256Hex["sha256:".Length..]
                            : expectedSha256Hex;
                        if (!string.Equals(actualHash, normExpected.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            return Result<string>.Failure(WrapperErrors.UpdateVerificationFailed(
                                $"SHA-256 verification failed for {safeName}. Expected {normExpected}, got {actualHash}."));
                        }
                    }
                }

                succeeded = true;
                return Result<string>.Success(tmpPath);
            }
            finally
            {
                if (!succeeded && File.Exists(tmpPath))
                {
                    try { File.Delete(tmpPath); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            return Result<string>.Failure(WrapperErrors.DownloadFailed($"Failed to download {fileName}: {ex.Message}"));
        }
    }

    public static string? ExtractVersionFromText(string text) =>
        SemverComparer.ExtractVersion(text);

    public static int CompareSemver(string? left, string? right) =>
        SemverComparer.Compare(left, right);
}

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

            // Fallback: inspect /contents/app for checked-in release binary.
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
