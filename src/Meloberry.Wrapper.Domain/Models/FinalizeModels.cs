namespace Meloberry.Wrapper.Domain.Models;

/// <summary>Summary of an ingested PDF document returned by `melomg ls --json`.</summary>
public sealed record MelomgDocumentSummary(
    string DocumentId,
    string Title,
    int PageCount,
    int FigureCount,
    string StorageDirectory);

/// <summary>Extracted figure image artifact returned by `melomg extract --json`.</summary>
public sealed record MelomgExtractedArtifact(
    string FigureId,
    string? FigureNumber,
    int PageNumber,
    string? Caption,
    string OutputImagePath,
    string? MetadataPath,
    int PixelWidth,
    int PixelHeight);

/// <summary>Parsed response from `melomg extract --json`.</summary>
public sealed record MelomgExtractResult(
    string DocumentId,
    string OutputDirectory,
    IReadOnlyList<MelomgExtractedArtifact> Artifacts);

/// <summary>Parsed outcome from `mby batch - --json`.</summary>
public sealed record MbyBatchExecutionResult(
    string? WorkspaceId,
    string? OutputDirectory,
    int StepCount);

/// <summary>Request to extract a figure directly into the Active Workspace `assets/` directory via `melomg`.</summary>
public sealed record ExtractFigureRequest(
    string? WorkspacePath,
    string PdfPath,
    string Query,
    int? Quality = null,
    bool MaskAll = false,
    bool EmitMeta = false);

/// <summary>Result returned to the GUI after extracting a figure via `melomg`.</summary>
public sealed record ExtractFigureResult(
    string DocumentId,
    string Selector,
    string RelativeImagePath,
    string FullImagePath,
    string? RelativeMetaPath,
    string? Caption,
    int PixelWidth,
    int PixelHeight);

/// <summary>Identifies a spec tile that has no authored content yet during non-placeholder Finalize.</summary>
public sealed record MissingSpecContentItem(
    string ComponentId,
    string ComponentTitle,
    string SpecId,
    string Topic);

/// <summary>Result of assembling a `PromptDraft` and attached `.md` files into a modular `mby` Markdown directory.</summary>
public sealed record AssembledWorkspaceFiles(
    string Slug,
    IReadOnlyDictionary<string, string> Files,
    IReadOnlyList<string> ReferencedAssetPaths,
    IReadOnlyList<MissingSpecContentItem> MissingContentSpecs,
    IReadOnlyList<string> PlaceholderSpecsUsed,
    bool RequiresPlaceholderImage);

/// <summary>Request to compile and mount the current workspace draft via `mby batch -`.</summary>
public sealed record FinalizeRequest(
    string? WorkspacePath = null,
    bool AllowPlaceholders = false,
    string? OutputDir = null,
    bool VerifyStaticOnly = true,
    string? ExportPackageTarget = null);

/// <summary>Outcome of a transactional Finalize build and Viewer mount.</summary>
public sealed record FinalizeResult(
    string WorkspaceId,
    string BuildSourceDirectory,
    string WebAppDirectory,
    string SlotId,
    string ViewerUrl,
    IReadOnlyList<string> PlaceholderSpecsUsed,
    string? ExportedPackagePath = null);
