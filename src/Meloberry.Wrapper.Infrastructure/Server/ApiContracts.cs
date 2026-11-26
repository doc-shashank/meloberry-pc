using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Infrastructure.Server;

internal sealed record ToolKeyRequest(string? Tool);

internal sealed record UninstallAppRequest(string? IdOrFileName);

internal sealed record PathRequest(string? Path);

internal sealed record UrlRequest(string? Url);

internal sealed record WorkspaceInitApiRequest(
    string WorkspacePath,
    IReadOnlyList<string>? SelectedAppFileNames,
    IReadOnlyList<StagedCopyItem>? StagedItems,
    ConflictPolicy ConflictPolicy = ConflictPolicy.SkipExisting);

internal sealed record ResetWorkspaceApiRequest(
    string? WorkspacePath,
    WorkspaceResetScope Scope = WorkspaceResetScope.ExecutablesOnly);

internal sealed record SavePromptApiRequest(string? Markdown);

internal sealed record SaveJsonApiRequest(string? RawJson, string? DefaultName);

internal sealed record BuildWorkspaceApiRequest(string WorkspaceId, string? OutputDir);

internal sealed record ExportPkgApiRequest(string WorkspaceId, string? Target, string? OutputPath);

internal sealed record DialogFolderRequest(string? Title, string? InitialDir);

internal sealed record DialogFilesRequest(
    string? Title,
    string? FilterLabel,
    string? FilterPattern,
    bool AllowMultiple = true,
    string? InitialDir = null);

internal sealed record UpdateSettingsApiRequest(
    string? ThemeMode,
    string? MbyExecutableOverride,
    string? MelomgExecutableOverride,
    string? DefaultBuildOutputDir,
    string? SelfUpdateRepoOwner,
    string? SelfUpdateRepoName,
    bool? OverrideWebAppTheme = null);

internal sealed record OverrideThemeApiRequest(bool OverrideWebAppTheme);
