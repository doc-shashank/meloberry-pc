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

internal sealed record SaveComponentPresetApiRequest(PromptComponentPlan Component, string? SuggestedName);

internal sealed record LoadComponentPresetApiRequest(ComponentKind ExpectedKind);

internal sealed record LiveDraftSaveApiRequest(
    string? WorkspacePath,
    PromptDraft? Draft,
    string? Source = "ui",
    string? Summary = "Updated draft");

internal sealed record LiveMutateApiRequest(
    string? WorkspacePath,
    string? Op,
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
    string? Notes = null,
    string? ImagePath = null,
    string? Caption = null,
    string? AltText = null,
    IReadOnlyList<string>? Modes = null);

internal sealed record LiveFinalizeApiRequest(
    string? WorkspacePath = null,
    bool? AllowPlaceholders = null,
    string? OutputDir = null,
    bool VerifyStaticOnly = true);

internal sealed record LiveUiApiRequest(
    string? Action,
    string? View = null,
    string? Path = null,
    string? Message = null,
    string? Level = null,
    string? Id = null,
    string? State = null,
    string? Detail = null,
    int? ExitCode = null,
    long? DurationMs = null);

internal sealed record ExtractFigureApiRequest(
    string? WorkspacePath,
    string PdfPath,
    string Query,
    string? Quality = null,
    bool MaskAll = false,
    bool EmitMeta = false);

internal sealed record ImportAssetApiRequest(
    string? WorkspacePath,
    string SourcePath,
    string? Kind = "image");
