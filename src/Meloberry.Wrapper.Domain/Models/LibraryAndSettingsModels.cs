namespace Meloberry.Wrapper.Domain.Models;

/// <summary>Represents a compiled WebApp folder tracked in the user's Recent library.</summary>
public sealed record WebAppLibraryEntry(
    string Path,
    string Name,
    DateTimeOffset LastOpenedUtc,
    int ReportCount = 0);

/// <summary>Summary of an authored mby SQLite workspace returned by `mby ls --all --json`.</summary>
public sealed record WorkspaceSummary(
    string Id,
    string Kind,
    string SubType,
    string StorageDirectory,
    int ItemCount);

/// <summary>Root persisted settings stored in %LOCALAPPDATA%\Meloberry\wrapper-settings.json.</summary>
public sealed record WrapperSettings(
    ActiveWorkspaceState? ActiveWorkspace = null,
    IReadOnlyList<string>? RecentWorkspaces = null,
    string? MbyExecutableOverride = null,
    string? MelomgExecutableOverride = null,
    string? SelfUpdateRepoOwner = null,
    string? SelfUpdateRepoName = null,
    string ThemeMode = "berry",
    string? DefaultBuildOutputDir = null,
    IReadOnlyList<WebAppLibraryEntry>? RecentWebApps = null,
    IReadOnlyList<PromptPreset>? CustomPresets = null,
    bool OverrideWebAppTheme = true);
