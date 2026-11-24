namespace Meloberry.Wrapper.Domain.Models;

/// <summary>Categorizes executables discovered inside %LOCALAPPDATA%\Meloberry\apps\.</summary>
public enum ManagedAppKind
{
    Mby,
    Melomg,
    CustomExecutable
}

/// <summary>Represents an executable slot in the canonical apps store and shopping cart.</summary>
public sealed record ManagedAppEntry(
    string Id,
    ManagedAppKind Kind,
    string DisplayName,
    string FileName,
    string? FullPath,
    string? Version,
    bool IsInstalled,
    bool IsPresentInWorkspace,
    string? RepoKey = null,
    string? Description = null);

/// <summary>Type of user-staged source asset to copy into the Active Workspace.</summary>
public enum StagedCopyKind
{
    File,
    Folder
}

/// <summary>Represents a file or folder staged for copying into the Active Workspace.</summary>
public sealed record StagedCopyItem(
    string SourcePath,
    StagedCopyKind Kind,
    string DisplayName,
    string TargetRelativePath);

/// <summary>Conflict resolution strategy when copying into an existing Active Workspace.</summary>
public enum ConflictPolicy
{
    SkipExisting,
    OverwriteExisting
}

/// <summary>Describes a pre-existing non-identical file collision in the Active Workspace.</summary>
public sealed record WorkspaceCopyConflict(
    string SourcePath,
    string DestinationPath,
    string RelativeTargetPath,
    StagedCopyKind Kind);

/// <summary>Pre-flight copy plan generated before executing workspace initialization.</summary>
public sealed record WorkspaceInitPlan(
    string WorkspacePath,
    IReadOnlyList<string> SelectedAppFileNames,
    IReadOnlyList<StagedCopyItem> StagedItems,
    int PlannedCopiesCount,
    int SkippedIdenticalCount,
    IReadOnlyList<WorkspaceCopyConflict> Conflicts);

/// <summary>Outcome of initializing or updating the Active Workspace folder.</summary>
public sealed record WorkspaceInitResult(
    string WorkspacePath,
    IReadOnlyList<string> CopiedApps,
    IReadOnlyList<string> CopiedFiles,
    IReadOnlyList<string> CopiedFolders,
    int SkippedCount,
    int OverwrittenCount,
    string? WorkspaceMbyPath,
    string? WorkspaceMelomgPath,
    IReadOnlyList<string> CopiedRelativePaths);

/// <summary>Persisted state of the currently selected Active Workspace.</summary>
public sealed record ActiveWorkspaceState(
    string WorkspacePath,
    bool IsInitialized,
    DateTimeOffset? LastInitializedUtc = null,
    DateTimeOffset? AgentsMdExportedUtc = null,
    IReadOnlyList<string>? SelectedAppFileNames = null,
    IReadOnlyList<StagedCopyItem>? StagedItems = null,
    IReadOnlyList<string>? LastCopiedRelativePaths = null,
    IReadOnlyList<string>? WorkspaceApps = null);

/// <summary>Compiled WebApp folder auto-discovered inside the Active Workspace.</summary>
public sealed record DiscoveredWebApp(
    string Name,
    string Path,
    int ReportCount);

/// <summary>Controls what Active Workspace Reset deletes.</summary>
public enum WorkspaceResetScope
{
    ExecutablesOnly,
    ExecutablesAndAgentsMd
}

/// <summary>Preview of recognized executables (and optionally AGENTS.md) that Reset will remove.</summary>
public sealed record WorkspaceResetPlan(
    string WorkspacePath,
    WorkspaceResetScope Scope,
    IReadOnlyList<string> ExecutablesToRemove,
    string? AgentsMdToRemove)
{
    public int TotalCount => ExecutablesToRemove.Count + (string.IsNullOrWhiteSpace(AgentsMdToRemove) ? 0 : 1);
}

/// <summary>Result of executing Active Workspace Reset.</summary>
public sealed record WorkspaceResetResult(
    string WorkspacePath,
    IReadOnlyList<string> RemovedRelativePaths,
    bool RemovedAgentsMd);

/// <summary>Represents an older Meloberry executable found inside the Active Workspace compared against apps\.</summary>
public sealed record OutdatedWorkspaceTool(
    string ToolKey,
    string DisplayName,
    string WorkspaceFileName,
    string WorkspaceRelativePath,
    string WorkspaceFullPath,
    string WorkspaceVersion,
    string LatestAppFileName,
    string LatestAppVersion);

/// <summary>Result of replacing outdated executables inside the Active Workspace with latest copies from apps\.</summary>
public sealed record WorkspaceToolUpdateResult(
    string WorkspacePath,
    IReadOnlyList<string> RemovedOldRelativePaths,
    IReadOnlyList<string> CopiedNewFileNames);
