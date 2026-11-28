using Meloberry.Wrapper.Domain.Common;

namespace Meloberry.Wrapper.Domain.Errors;

/// <summary>Central catalog of domain error factories for Meloberry PC.</summary>
public static class WrapperErrors
{
    public static Error CliNotFound(string toolName, string? hint = null) => new(
        "Cli.NotFound",
        $"Executable for '{toolName}' was not found.",
        ErrorType.NotFound,
        hint ?? $"Download {toolName} from GitHub or import a local .exe into the managed apps folder.");

    public static Error CliExecutionFailed(string toolName, int exitCode, string detail) => new(
        "Cli.ExecutionFailed",
        $"{toolName} exited with code {exitCode}: {detail}",
        ErrorType.ExternalProcess);

    public static Error CliIncompatible(string installedVersion, string minimumVersion, string detail) => new(
        "Cli.Incompatible",
        string.IsNullOrWhiteSpace(detail)
            ? $"Installed mby-cli ({installedVersion}) is older than required v{minimumVersion}+."
            : detail,
        ErrorType.Validation,
        $"Update mby-cli to v{minimumVersion}+ from Setup or click 'Update mby-cli Now'.");

    public static Error InvalidWebAppDir(string path) => new(
        "Viewer.InvalidWebAppDir",
        $"The folder '{path}' does not contain a compiled Meloberry WebApp (missing index.html or app.js).",
        ErrorType.Validation,
        "Run 'mby build' first or select a folder containing index.html and app.js.");

    public static Error InvalidWorkspaceDir(string path) => new(
        "Workspace.InvalidDir",
        $"The workspace path '{path}' is invalid or inaccessible.",
        ErrorType.Validation);

    public static Error RecursiveCopyBlocked(string sourceDir, string destinationDir) => new(
        "Workspace.RecursiveCopyBlocked",
        $"Cannot copy folder '{sourceDir}' into '{destinationDir}' because the destination is the source itself or inside it.",
        ErrorType.Security,
        "Choose a different Active Workspace folder outside the staged source folder.");

    public static Error AgentsMdAlreadyExists(string agentsMdPath) => new(
        "Workspace.AgentsMdExists",
        $"AGENTS.md already exists at '{agentsMdPath}'. Confirm overwrite to replace it.",
        ErrorType.Conflict,
        "Confirm overwrite to replace the existing AGENTS.md with the newly generated schema.");

    public static Error InvalidPromptDraft(string message) => new(
        "Prompt.InvalidDraft",
        message,
        ErrorType.Validation);

    public static Error PresetNotFound(string presetId) => new(
        "Preset.NotFound",
        $"Prompt preset '{presetId}' was not found.",
        ErrorType.NotFound);

    public static Error InvalidPresetFile(string detail) => new(
        "Preset.InvalidFile",
        detail,
        ErrorType.Validation);

    public static Error DownloadFailed(string message, string? hint = null) => new(
        "GitHub.DownloadFailed",
        message,
        ErrorType.Network,
        hint);

    public static Error RateLimited(string repoSlug, string releaseUrl) => new(
        "GitHub.RateLimited",
        $"GitHub API rate limit reached while querying '{repoSlug}'.",
        ErrorType.Network,
        $"Open {releaseUrl} in your browser to download manually.");

    public static Error ReleaseNotFound(string repoSlug) => new(
        "GitHub.ReleaseNotFound",
        $"No published release was found for '{repoSlug}'.",
        ErrorType.NotFound);

    public static Error UpdateVerificationFailed(string message) => new(
        "Update.VerificationFailed",
        message,
        ErrorType.Security);

    public static Error InvalidUrl(string url) => new(
        "Security.InvalidUrl",
        $"URL '{url}' is not on the Meloberry external link allowlist.",
        ErrorType.Security);

    public static Error InvalidPath(string path) => new(
        "Security.InvalidPath",
        $"Path '{path}' failed path validation or confinement.",
        ErrorType.Security);

    public static Error IoFailure(string message) => new(
        "Io.Failure",
        message,
        ErrorType.Io);
}
