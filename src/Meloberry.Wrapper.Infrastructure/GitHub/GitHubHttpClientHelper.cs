using System.Net;
using System.Security.Cryptography;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;

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
