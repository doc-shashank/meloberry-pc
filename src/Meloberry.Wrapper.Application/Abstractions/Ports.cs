using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Application.Abstractions;

/// <summary>Resolves wrapper local AppData paths (respecting MBY_WRAPPER_DATA override).</summary>
public interface IWrapperDataPaths
{
    string BaseDirectory { get; }
    string AppsDirectory { get; }
    string UpdatesDirectory { get; }
    string WebView2UserDataDirectory { get; }
    string SettingsFilePath { get; }
}

/// <summary>Manages %LOCALAPPDATA%\Meloberry\apps\ executables and local .exe imports.</summary>
public interface IManagedAppsStore
{
    Task<IReadOnlyList<ManagedAppEntry>> ScanAppsAsync(string? activeWorkspacePath, CancellationToken ct = default);
    Task<Result<ManagedAppEntry>> ImportLocalExecutableAsync(string sourcePath, CancellationToken ct = default);
    Task<Result<ManagedAppEntry>> SaveDownloadedToolAsync(string toolKey, string fileName, string tempFilePath, CancellationToken ct = default);
    Task<Result<string>> UninstallAppAsync(string idOrFileName, CancellationToken ct = default);
    Task<string?> ResolveToolExecutableAsync(string toolKey, string? workspacePath, string? explicitOverride, CancellationToken ct = default);
}

/// <summary>Plans and performs non-destructive Active Workspace copying.</summary>
public interface IWorkspaceInitializer
{
    Task<Result<WorkspaceInitPlan>> PlanAsync(
        string workspacePath,
        IReadOnlyList<string> selectedAppFileNames,
        IReadOnlyList<StagedCopyItem> stagedItems,
        CancellationToken ct = default);

    Task<Result<WorkspaceInitResult>> InitializeAsync(
        string workspacePath,
        IReadOnlyList<string> selectedAppFileNames,
        IReadOnlyList<StagedCopyItem> stagedItems,
        ConflictPolicy conflictPolicy,
        CancellationToken ct = default);

    Task<IReadOnlyList<DiscoveredWebApp>> DiscoverWebAppsAsync(string workspacePath, CancellationToken ct = default);
}

/// <summary>Checks and writes AGENTS.md cleanly inside the Active Workspace.</summary>
public interface IAgentsMdFileWriter
{
    Task<Result<AgentsMdStatus>> GetStatusAsync(string workspacePath, CancellationToken ct = default);
    Task<Result<AgentsMdStatus>> WriteAsync(string workspacePath, string content, bool overwriteConfirmed, CancellationToken ct = default);
}

/// <summary>Executes `mby` CLI commands out-of-process with MBY_AGENT=1.</summary>
public interface IMbyCliClient
{
    Task<Result<IReadOnlyList<SpecTopicItem>>> GetSpecCatalogAsync(string exePath, string? workingDir, CancellationToken ct = default);
    Task<Result<IReadOnlyList<AgentsMdSpecDetail>>> GetSpecDetailsAsync(string exePath, string? workingDir, IReadOnlyList<string> topics, CancellationToken ct = default);
    Task<Result<IReadOnlyList<WorkspaceSummary>>> ListWorkspacesAsync(string exePath, string? workingDir, CancellationToken ct = default);
    Task<Result<string>> BuildWorkspaceAsync(string exePath, string workspaceId, string outputDir, string? workingDir, CancellationToken ct = default);
    Task<Result<string>> ExportPackageAsync(string exePath, string workspaceId, string target, string outputPath, string? workingDir, CancellationToken ct = default);
    Task<Result<string>> ApplySpecJsonAsync(string exePath, string specFilePath, string? workingDir, CancellationToken ct = default);
}

/// <summary>Executes `melomg` CLI commands out-of-process with MBY_AGENT=1.</summary>
public interface IMelomgCliClient
{
    Task<Result<string>> GetSpecTextAsync(string exePath, string? workingDir, IReadOnlyList<string> topics, CancellationToken ct = default);
}

/// <summary>Downloads verified CLI executables from GitHub Releases or contents/app.</summary>
public interface IGitHubToolDownloader
{
    Task<Result<ToolReleaseCandidate>> CheckLatestToolAsync(string toolKey, CancellationToken ct = default);
    Task<Result<string>> DownloadToTempFileAsync(
        ToolReleaseCandidate candidate,
        Action<DownloadProgressSnapshot> progressCallback,
        CancellationToken ct = default);
}

/// <summary>Checks and applies Meloberry PC self-updates via Inno Setup installer from GitHub Releases.</summary>
public interface IGitHubSelfUpdater
{
    Task<Result<AppUpdateCheckResult>> CheckForUpdateAsync(string owner, string repo, string currentVersion, CancellationToken ct = default);
    Task<Result<string>> DownloadInstallerAsync(
        AppUpdateCheckResult check,
        Action<DownloadProgressSnapshot> progressCallback,
        CancellationToken ct = default);
    Result LaunchInstallerSilent(string installerPath);
}

/// <summary>Coordinates graceful Win32 app termination (used during Inno self-update).</summary>
public interface IAppShutdownCoordinator
{
    void RequestShutdown();
}

/// <summary>Persists wrapper-settings.json atomically.</summary>
public interface ISettingsStore
{
    Task<WrapperSettings> LoadAsync(CancellationToken ct = default);
    Task<Result> SaveAsync(WrapperSettings settings, CancellationToken ct = default);
}

/// <summary>Writes captured WebApp study telemetry into &lt;webDir&gt;/reports/.</summary>
public interface IWebAppAnalysisStore
{
    Task<Result<string>> SaveAnalysisAsync(string webAppDir, string? reportJson, string? markdown, CancellationToken ct = default);
    int CountReports(string webAppDir);
}

/// <summary>Invokes native OS file/folder pickers.</summary>
public interface INativeDialogService
{
    Task<string?> PickFolderAsync(string title, string? initialDir = null, CancellationToken ct = default);
    Task<IReadOnlyList<string>> PickFilesAsync(string title, string filterLabel, string filterPattern, bool allowMultiple = true, string? initialDir = null, CancellationToken ct = default);
    Task<string?> SaveFileAsync(string title, string defaultFileName, string filterLabel, string filterExtension, string? initialDir = null, CancellationToken ct = default);
}

/// <summary>Launches allowlisted HTTPS links or opens local folders in Windows Explorer.</summary>
public interface IExternalUrlLauncher
{
    Result OpenAllowlistedUrl(string url);
    Result OpenFolder(string folderPath);
}

/// <summary>Mounts compiled WebApp folders into localhost static routes.</summary>
public interface IWebAppMountRegistry
{
    Result<string> Mount(string webAppDir);
    bool TryResolve(string slotId, out string webAppDir);
    string? ActiveSlotId { get; }
}

/// <summary>Plans/executes recursive workspace Reset and offline outdated-tool detection against apps\.</summary>
public interface IWorkspaceMaintenanceService
{
    Task<Result<WorkspaceResetPlan>> PlanResetAsync(string workspacePath, WorkspaceResetScope scope, CancellationToken ct = default);
    Task<Result<WorkspaceResetResult>> ExecuteResetAsync(string workspacePath, WorkspaceResetScope scope, CancellationToken ct = default);
    Task<Result<IReadOnlyList<OutdatedWorkspaceTool>>> DetectOutdatedToolsAsync(string workspacePath, CancellationToken ct = default);
    Task<Result<WorkspaceToolUpdateResult>> UpdateOutdatedToolsAsync(string workspacePath, CancellationToken ct = default);
}

/// <summary>Real-time change event emitted when executables or AGENTS.md are added/removed externally.</summary>
public sealed record WorkspaceFileChangeEvent(
    string Scope,
    string ChangeKind,
    string FileName,
    string FullPath,
    DateTimeOffset TimestampUtc);

/// <summary>Monitors %LOCALAPPDATA%\Meloberry\apps\ and the Active Workspace for external changes.</summary>
public interface IWorkspaceFileWatcher
{
    event Action<WorkspaceFileChangeEvent>? Changed;
    void UpdateActiveWorkspace(string? workspacePath);
    IDisposable SuppressScope(string scope, int postDisposeGraceMs = 1500);
}
