using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Application.Abstractions;

/// <summary>Resolves wrapper local AppData paths (respecting MBY_WRAPPER_DATA override).</summary>
public interface IWrapperDataPaths
{
    string BaseDirectory { get; }
    string AppsDirectory { get; }
    string PresetsDirectory { get; }
    string ComponentPresetsDirectory { get; }
    string UpdatesDirectory { get; }
    string WebView2UserDataDirectory { get; }
    string SettingsFilePath { get; }
    string ExePointerFilePath { get; }
    string RuntimeIpcFilePath { get; }
}

/// <summary>Manages the active-workspace-only `mby-pc.bat` shim replacing per-workspace tool copies.</summary>
public interface IWorkspaceShimWriter
{
    Task<Result<string>> WriteShimAsync(string workspacePath, bool overwriteExisting = true, CancellationToken ct = default);
    Task<Result<bool>> RemoveShimIfManagedAsync(string workspacePath, CancellationToken ct = default);
    string RenderShimContent();
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
    Task<Result<AgentsMdStatus>> GetStatusAsync(string workspacePath, string? targetPathOverride, CancellationToken ct = default);
    Task<Result<AgentsMdStatus>> WriteAsync(string workspacePath, string content, bool overwriteConfirmed, CancellationToken ct = default);
    Task<Result<AgentsMdStatus>> WriteAsync(string workspacePath, string? targetDirectoryOverride, string content, bool overwriteConfirmed, CancellationToken ct = default);
}

/// <summary>Live spec catalog plus embedded SVG icons resolved from `mby-cli`.</summary>
public sealed record SpecCatalogResult(
    IReadOnlyList<SpecTopicItem> Specs,
    IReadOnlyDictionary<string, string> SpecIcons);

/// <summary>Executes `mby` CLI commands out-of-process with MBY_AGENT=1.</summary>
public interface IMbyCliClient
{
    Task<Result<IReadOnlyList<SpecTopicItem>>> GetSpecCatalogAsync(string exePath, string? workingDir, CancellationToken ct = default);
    Task<Result<SpecCatalogResult>> GetSpecCatalogWithIconsAsync(string exePath, string? workingDir, CancellationToken ct = default);
    Task<Result<IReadOnlyList<AgentsMdSpecDetail>>> GetSpecDetailsAsync(string exePath, string? workingDir, IReadOnlyList<string> topics, CancellationToken ct = default);
    Task<Result<IReadOnlyList<WorkspaceSummary>>> ListWorkspacesAsync(string exePath, string? workingDir, CancellationToken ct = default);
    Task<Result<string>> BuildWorkspaceAsync(string exePath, string workspaceId, string outputDir, string? workingDir, CancellationToken ct = default);
    Task<Result<string>> ExportPackageAsync(string exePath, string workspaceId, string target, string outputPath, string? workingDir, CancellationToken ct = default);
    Task<Result<string>> ApplySpecJsonAsync(string exePath, string specFilePath, string? workingDir, CancellationToken ct = default);
    Task<Result<MbyBatchExecutionResult>> RunBatchAsync(string exePath, string batchJson, string? workingDir, CancellationToken ct = default);
}

/// <summary>Executes `melomg` CLI commands out-of-process with MBY_AGENT=1.</summary>
public interface IMelomgCliClient
{
    Task<Result<string>> GetSpecTextAsync(string exePath, string? workingDir, IReadOnlyList<string> topics, CancellationToken ct = default);
    Task<Result<IReadOnlyList<MelomgDocumentSummary>>> ListDocumentsAsync(string exePath, string? workingDir, CancellationToken ct = default);
    Task<Result<MelomgExtractResult>> ExtractFigureAsync(
        string exePath,
        string targetOrPdf,
        string selector,
        string outputDir,
        int? quality,
        bool maskAll,
        bool emitMeta,
        bool retainDb,
        bool offline,
        string? workingDir,
        CancellationToken ct = default);
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
}

/// <summary>Coordinates graceful Win32 app termination (used during Inno self-update).</summary>
public interface IAppShutdownCoordinator
{
    void RequestShutdown();
    void RequestShutdownAndLaunch(string executablePath, string arguments);
}

/// <summary>Persists wrapper-settings.json atomically.</summary>
public interface ISettingsStore
{
    Task<WrapperSettings> LoadAsync(CancellationToken ct = default);
    Task<Result> SaveAsync(WrapperSettings settings, CancellationToken ct = default);
    Task<Result<WrapperSettings>> UpdateAsync(
        Func<WrapperSettings, WrapperSettings> mutator,
        CancellationToken ct = default);
    Task<Result<WrapperSettings>> UpdateAsync(
        Func<WrapperSettings, Result<WrapperSettings>> mutator,
        CancellationToken ct = default);
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
    Task<Result<WorkspaceResetPlan>> PlanResetAsync(string workspacePath, WorkspaceResetScope scope, IReadOnlyList<string>? trackedReadyArtifacts = null, CancellationToken ct = default);
    Task<Result<WorkspaceResetResult>> ExecuteResetAsync(string workspacePath, WorkspaceResetScope scope, IReadOnlyList<string>? trackedReadyArtifacts = null, CancellationToken ct = default);
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

/// <summary>Writes or copies files atomically via temporary sibling files with guaranteed cleanup.</summary>
public interface IAtomicFileWriter
{
    Task WriteAllTextAsync(string destinationPath, string content, CancellationToken ct = default);
    Task WriteAllBytesAsync(string destinationPath, byte[] bytes, CancellationToken ct = default);
    void CopyFile(string sourcePath, string destinationPath, string tempSuffix = ".tmp");
    void WriteStream(string destinationPath, Stream contentStream, string tempSuffix = ".tmp");
}

/// <summary>Reads and writes `.mbypreset` files for whole workflows and individual component cards.</summary>
public interface IPresetFileStore
{
    Task<IReadOnlyList<PromptPreset>> LoadWorkflowPresetsAsync(CancellationToken ct = default);
    Task<Result<PromptPreset>> SaveWorkflowPresetAsync(PromptPreset preset, CancellationToken ct = default);
    Task<Result<(PromptPreset Preset, string SavedPath)>> SaveWorkflowPresetToFileAsync(string filePath, PromptPreset preset, CancellationToken ct = default);
    Task<Result> DeleteWorkflowPresetAsync(string presetId, CancellationToken ct = default);
    Task<Result<string>> WritePresetFileAsync(string filePath, MbyPresetFile presetFile, CancellationToken ct = default);
    Task<Result<MbyPresetFile>> ReadPresetFileAsync(string filePath, CancellationToken ct = default);
}

/// <summary>Enumerates immediate folders and files inside a directory for the Active Workspace tree popup.</summary>
public interface IWorkspaceDirectoryBrowser
{
    Task<Result<WorkspaceDirectoryListing>> ListDirectoryAsync(string directoryPath, CancellationToken ct = default);
}

/// <summary>Persists the live per-workspace authoring draft inside .meloberry/pc-draft.json.</summary>
public interface IWorkspaceDraftStore
{
    Task<PromptDraft> LoadAsync(string workspacePath, CancellationToken ct = default);
    Task<Result<PromptDraft>> SaveAsync(string workspacePath, PromptDraft draft, CancellationToken ct = default);
}

/// <summary>Active GUI loopback IPC endpoint metadata stored in %LOCALAPPDATA%\Meloberry\runtime-ipc.json.</summary>
public sealed record RuntimeIpcInfo(
    int Pid,
    int ShellPort,
    int ViewerPort,
    string Token,
    DateTimeOffset StartedUtc);

/// <summary>Tracks the active Meloberry PC GUI HTTP instance for --cli live synchronization.</summary>
public interface IRuntimeIpcRegistry
{
    Task WriteCurrentAsync(int shellPort, int viewerPort, string token, CancellationToken ct = default);
    RuntimeIpcInfo? TryReadActive();
    void CleanupCurrent();
}

/// <summary>Event emitted whenever the live workspace draft is mutated from UI or CLI.</summary>
public sealed record LiveDraftChangedEvent(
    string WorkspacePath,
    PromptDraft Draft,
    string Source,
    string Summary,
    string? TargetComponentId = null,
    string? TargetSpecId = null);

/// <summary>Surgical draft mutation command executed via GUI or `mby-pc.bat draft`.</summary>
public sealed record LiveDraftMutationRequest(
    string Op,
    string? Source = "agent",
    string? TargetId = null,
    ContainerKind? Container = null,
    string? Title = null,
    string? Description = null,
    AuthoringFormat? AuthoringFormat = null,
    WorkflowMode? WorkflowMode = null,
    bool? AllowPlaceholdersOnFinalize = null,
    ComponentKind? ComponentKind = null,
    string? Topic = null,
    string? Subtype = null,
    int? Index = null,
    int? Count = null,
    SpecContentMode? ContentMode = null,
    string? RawText = null,
    string? MarkdownFilePath = null,
    string? Notes = null);

/// <summary>Result returned by `LiveDraftHandler.MutateAsync`.</summary>
public sealed record LiveDraftMutationResult(
    PromptDraft Draft,
    string Summary,
    string? TargetComponentId = null,
    string? TargetSpecId = null);
