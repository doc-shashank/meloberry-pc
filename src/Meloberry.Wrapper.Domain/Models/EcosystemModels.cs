using Meloberry.Wrapper.Domain.Common;

namespace Meloberry.Wrapper.Domain.Models;

/// <summary>Describes an official sibling Meloberry GitHub repository.</summary>
public sealed record EcosystemRepository(
    string Key,
    string DisplayName,
    string Owner,
    string Repo,
    string RepoUrl,
    string Description,
    string? BinaryPrefix = null,
    bool Downloadable = false)
{
    public string Slug => $"{Owner}/{Repo}";
    public string ReleasesUrl => $"{RepoUrl}/releases";
}

/// <summary>Canonical catalog of Meloberry repositories surfaced in UI links and downloaders.</summary>
public static class EcosystemCatalog
{
    public static readonly EcosystemRepository Mby = new(
        Key: "mby",
        DisplayName: "Meloberry CLI",
        Owner: "doc-shashank",
        Repo: "meloberry-cli",
        RepoUrl: "https://github.com/doc-shashank/meloberry-cli",
        Description: "Stateless .NET 10 Study WebApp & .mbypkg v2.0 compiler (`mby`).",
        BinaryPrefix: "mby-cli-",
        Downloadable: true);

    public static readonly EcosystemRepository Melomg = new(
        Key: "melomg",
        DisplayName: "Meloberry Melomg",
        Owner: "doc-shashank",
        Repo: "meloberry-melomg",
        RepoUrl: "https://github.com/doc-shashank/meloberry-melomg",
        Description: "Crop-only PDF figure/diagram/chart extractor & label masker (`melomg`).",
        BinaryPrefix: "meloberry-melomg-",
        Downloadable: true);

    public static readonly EcosystemRepository Android = new(
        Key: "android",
        DisplayName: "Meloberry Android",
        Owner: "doc-shashank",
        Repo: "meloberry-android-viewer",
        RepoUrl: "https://github.com/doc-shashank/meloberry-android-viewer",
        Description: "Offline-first Kotlin Jetpack Compose player for `.mbypkg` packages.",
        BinaryPrefix: null,
        Downloadable: false);

    public static readonly EcosystemRepository Pc = new(
        Key: "pc",
        DisplayName: "Meloberry PC",
        Owner: "doc-shashank",
        Repo: "meloberry-pc",
        RepoUrl: "https://github.com/doc-shashank/meloberry-pc",
        Description: "Lightweight Win32 + WebView2 desktop companion & installer (`mby-pc`).",
        BinaryPrefix: "mby-pc-setup-",
        Downloadable: true);

    public static IReadOnlyList<EcosystemRepository> Repositories { get; } =
    [
        Mby,
        Melomg,
        Android,
        Pc
    ];

    public static EcosystemRepository? FindByKey(string key) =>
        Repositories.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase));

    public static bool TryValidateAllowlistedUrl(string? url, out Uri? validatedUri)
    {
        validatedUri = null;
        if (string.IsNullOrWhiteSpace(url) || url.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        var unescapedPath = Uri.UnescapeDataString(uri.AbsolutePath);
        if (unescapedPath.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        var isAllowedRepo = string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
            Repositories.Any(r =>
            {
                var repoPath = new Uri(r.RepoUrl).AbsolutePath.TrimEnd('/');
                var reqPath = uri.AbsolutePath.TrimEnd('/');
                return string.Equals(reqPath, repoPath, StringComparison.OrdinalIgnoreCase) ||
                       uri.AbsolutePath.StartsWith(repoPath + "/", StringComparison.OrdinalIgnoreCase);
            });

        var isMicrosoftRuntime = string.Equals(uri.Host, "developer.microsoft.com", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(uri.Host, "dotnet.microsoft.com", StringComparison.OrdinalIgnoreCase);

        if (!isAllowedRepo && !isMicrosoftRuntime)
        {
            return false;
        }

        validatedUri = uri;
        return true;
    }
}

/// <summary>Represents a candidate executable or installer discovered from GitHub Releases or contents.</summary>
public sealed record ToolReleaseCandidate(
    string ToolKey,
    string Source,
    string TagOrRef,
    string AssetFileName,
    string DownloadUrl,
    long? SizeBytes = null,
    string? Sha256Hex = null,
    string? ExtractedVersion = null);

/// <summary>Live progress state for a tool or self-update download operation.</summary>
public sealed record DownloadProgressSnapshot(
    string OperationKey,
    string Status,
    string Message,
    long BytesReceived = 0,
    long? TotalBytes = null,
    string? SavedFilePath = null,
    bool IsCompleted = false,
    bool IsError = false)
{
    public double? Percent => TotalBytes is > 0
        ? Math.Clamp(Math.Round((double)BytesReceived / TotalBytes.Value * 100.0, 1), 0, 100)
        : null;
}

/// <summary>Result of checking the Meloberry PC repository for a newer installer release.</summary>
public sealed record AppUpdateCheckResult(
    string CurrentVersion,
    string RepoSlug,
    string ReleasePageUrl,
    bool UpdateAvailable,
    bool RepoNotPublished,
    string StatusMessage,
    string? LatestVersion = null,
    string? AssetFileName = null,
    string? DownloadUrl = null,
    long? SizeBytes = null,
    string? Sha256Hex = null);

/// <summary>Result of checking a managed CLI tool in apps\ against GitHub Releases.</summary>
public sealed record ToolUpdateCheckResult(
    string ToolKey,
    bool IsInstalled,
    string? InstalledVersion,
    bool UpdateAvailable,
    string? LatestVersion,
    string? AssetFileName,
    string StatusMessage);

/// <summary>Parsed `mby version --json` metadata and compatibility evaluation against Wrapper's minimum CLI floor.</summary>
public sealed record MbyCliVersionInfo(
    string Version,
    IReadOnlyList<string> Capabilities,
    bool IsCompatible,
    string? IncompatibilityReason = null);

/// <summary>Minimum `mby-cli` version and capability contract required by Meloberry PC.</summary>
public static class MbyCliCompatibility
{
    public const string MinimumDisplayVersion = "2.2.0";
    public const string MinimumSemverFloor = "2.2.0-0";

    public static readonly IReadOnlyList<string> RequiredCapabilities =
    [
        "spec.modeGroups",
        "scaffold.cli",
        "asset.cli",
        "prime.agentsMd",
        "build.allThemes"
    ];

    public static MbyCliVersionInfo Evaluate(string? version, IReadOnlyList<string>? capabilities)
    {
        var normalizedVersion = (version ?? string.Empty).Trim();
        var caps = (capabilities ?? [])
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (string.IsNullOrWhiteSpace(normalizedVersion) ||
            SemverComparer.Compare(normalizedVersion, MinimumSemverFloor) < 0)
        {
            var displayInstalled = string.IsNullOrWhiteSpace(normalizedVersion) ? "unknown" : $"v{normalizedVersion}";
            return new MbyCliVersionInfo(
                normalizedVersion,
                caps,
                IsCompatible: false,
                IncompatibilityReason: $"Meloberry PC requires mby-cli v{MinimumDisplayVersion}+ (installed: {displayInstalled}).");
        }

        var capSet = caps.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = RequiredCapabilities.Where(req => !capSet.Contains(req)).ToArray();
        if (missing.Length > 0)
        {
            return new MbyCliVersionInfo(
                normalizedVersion,
                caps,
                IsCompatible: false,
                IncompatibilityReason: $"Installed mby-cli v{normalizedVersion} is missing required capability token(s): {string.Join(", ", missing)}. Update to mby-cli v{MinimumDisplayVersion}+.");
        }

        return new MbyCliVersionInfo(normalizedVersion, caps, IsCompatible: true, IncompatibilityReason: null);
    }
}
