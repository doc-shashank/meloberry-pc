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

/// <summary>Result returned by generating a strict camelCase mby session/article/quiz JSON scaffold.</summary>
public sealed record SessionScaffoldOutput(
    string RawJson,
    bool RequiresPlaceholderImage,
    string SuggestedFileName,
    IReadOnlyList<string>? Warnings = null);

/// <summary>Parsed outcome from `mby scaffold --json`.</summary>
public sealed record ScaffoldCliResult(
    string Format,
    string Slug,
    string? RawJson,
    IReadOnlyDictionary<string, string>? MarkdownFiles,
    string? WrittenPath,
    bool RequiresPlaceholderImage,
    IReadOnlyList<string> PlaceholderSpecsUsed,
    IReadOnlyList<string> Warnings,
    string? AppliedTargetId);

/// <summary>Parsed outcome from `mby asset add --json`.</summary>
public sealed record AssetAddCliResult(
    string WorkspaceId,
    string RelativeAssetPath,
    string FullPath,
    bool AlreadyExisted);

/// <summary>Parsed outcome from `mby prime --format agents-md --json`.</summary>
public sealed record PrimeAgentsMdCliResult(
    string Markdown,
    int ApproximateTokens,
    int CharacterCount,
    IReadOnlyList<string> IncludedTopics,
    string? WrittenPath = null);

/// <summary>Parsed outcome from `mby analyze --ingest --json`.</summary>
public sealed record IngestAnalysisCliResult(
    string JsonPath,
    string MarkdownPath);

/// <summary>Parsed outcome from `mby clone --json`.</summary>
public sealed record CloneEntityCliResult(
    string NewId,
    string EntityKind,
    string TargetContainerId,
    int NewIndex,
    string? NewRef = null);

/// <summary>Request to extract a figure directly into the Active Workspace `assets/` directory via `melomg`.</summary>
public sealed record ExtractFigureRequest(
    string? WorkspacePath,
    string PdfPath,
    string Query,
    string? Quality = null,
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
