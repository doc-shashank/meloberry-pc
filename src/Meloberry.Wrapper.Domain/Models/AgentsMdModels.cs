namespace Meloberry.Wrapper.Domain.Models;

/// <summary>Individual field definition returned by `mby spec`.</summary>
public sealed record AgentsMdSpecField(
    string Name,
    string Type,
    bool Required,
    string Description,
    IReadOnlyList<string>? AllowedValues = null,
    int? MaxLength = null,
    string? Pattern = null,
    int? MinValue = null,
    int? MaxValue = null);

/// <summary>Full live schema metadata returned by `mby spec &lt;topics&gt; --json`.</summary>
public sealed record AgentsMdSpecDetail(
    string Topic,
    string Scope,
    string Category,
    string Summary,
    string CompactSignature,
    string Command,
    bool DisallowUnmappedProperties,
    IReadOnlyList<AgentsMdSpecField> Fields,
    string? ExampleJson);

/// <summary>Current AGENTS.md presence status inside a workspace.</summary>
public sealed record AgentsMdStatus(
    string WorkspacePath,
    string AgentsMdPath,
    bool Exists,
    long? SizeBytes = null,
    DateTimeOffset? LastModifiedUtc = null,
    string? Preview = null);

/// <summary>Inputs required to deterministically render a tailored workspace AGENTS.md.</summary>
public sealed record AgentsMdComposeInput(
    string WorkspacePath,
    ContainerKind Container,
    AuthoringFormat AuthoringFormat,
    string? WorkspaceMbyFileName,
    string? WorkspaceMelomgFileName,
    IReadOnlyList<string> ExtraWorkspaceExecutables,
    IReadOnlyList<string> CopiedFiles,
    IReadOnlyList<string> CopiedFolders,
    IReadOnlyList<AgentsMdSpecDetail> SelectedSpecs,
    string? MelomgSpecsText,
    bool IncludeLinkBlock,
    IReadOnlyList<string>? ExternalAttachments = null);

/// <summary>Result returned after exporting AGENTS.md into the Active Workspace.</summary>
public sealed record AgentsMdExportResult(
    string OutputPath,
    bool OverwroteExisting,
    int BytesWritten,
    int ApproximateTokens,
    IReadOnlyList<string> IncludedTopics,
    string MarkdownPreview,
    IReadOnlyList<string>? Warnings = null);
