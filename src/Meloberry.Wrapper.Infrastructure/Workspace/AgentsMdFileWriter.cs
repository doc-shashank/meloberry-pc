using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;
using Meloberry.Wrapper.Domain.Services;
using Meloberry.Wrapper.Infrastructure.Common;

namespace Meloberry.Wrapper.Infrastructure.Workspace;

/// <summary>Confined atomic reader/writer for Active Workspace AGENTS.md exports.</summary>
public sealed class AgentsMdFileWriter : IAgentsMdFileWriter
{
    private const long MaxPreviewBytes = 512 * 1024;

    public Task<Result<AgentsMdStatus>> GetStatusAsync(string workspacePath, CancellationToken ct = default)
        => GetStatusAsync(workspacePath, null, ct);

    public async Task<Result<AgentsMdStatus>> GetStatusAsync(
        string workspacePath,
        string? targetPathOverride,
        CancellationToken ct = default)
    {
        var pathRes = ResolveAgentsMdPath(workspacePath, targetPathOverride);
        if (pathRes.IsFailure)
        {
            return Result<AgentsMdStatus>.Failure(pathRes.Error!);
        }

        var (fullWs, agentsMdPath) = pathRes.Value;
        if (!File.Exists(agentsMdPath))
        {
            return Result<AgentsMdStatus>.Success(new AgentsMdStatus(fullWs, agentsMdPath, false));
        }

        try
        {
            var info = new FileInfo(agentsMdPath);
            string preview;
            int charCount;
            int approxTokens;
            if (info.Length <= MaxPreviewBytes)
            {
                preview = await File.ReadAllTextAsync(agentsMdPath, ct).ConfigureAwait(false);
                charCount = preview.Length;
                approxTokens = AgentsMdComposer.EstimateTokens(preview);
            }
            else
            {
                preview = "(AGENTS.md exceeds 512 KB preview limit — open the workspace folder to inspect.)";
                charCount = (int)Math.Min(int.MaxValue, info.Length);
                approxTokens = (int)Math.Max(1, info.Length / 4);
            }

            return Result<AgentsMdStatus>.Success(new AgentsMdStatus(
                fullWs,
                agentsMdPath,
                Exists: true,
                SizeBytes: info.Length,
                LastModifiedUtc: new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                Preview: preview,
                ApproximateTokens: approxTokens,
                CharacterCount: charCount));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result<AgentsMdStatus>.Failure(WrapperErrors.IoFailure($"Failed to read AGENTS.md: {ex.Message}"));
        }
    }

    public Task<Result<AgentsMdStatus>> WriteAsync(
        string workspacePath,
        string content,
        bool overwriteConfirmed,
        CancellationToken ct = default)
        => WriteAsync(workspacePath, null, content, overwriteConfirmed, ct);

    public async Task<Result<AgentsMdStatus>> WriteAsync(
        string workspacePath,
        string? targetDirectoryOverride,
        string content,
        bool overwriteConfirmed,
        CancellationToken ct = default)
    {
        var pathRes = ResolveAgentsMdPath(workspacePath, targetDirectoryOverride);
        if (pathRes.IsFailure)
        {
            return Result<AgentsMdStatus>.Failure(pathRes.Error!);
        }

        var (fullWs, agentsMdPath) = pathRes.Value;
        if (File.Exists(agentsMdPath) && !overwriteConfirmed)
        {
            return Result<AgentsMdStatus>.Failure(WrapperErrors.AgentsMdAlreadyExists(agentsMdPath));
        }

        try
        {
            await AtomicFileOps.WriteAllTextAsync(agentsMdPath, content, ct).ConfigureAwait(false);

            var info = new FileInfo(agentsMdPath);
            var preview = info.Length <= MaxPreviewBytes
                ? content
                : "(AGENTS.md exceeds 512 KB preview limit — open the workspace folder to inspect.)";

            return Result<AgentsMdStatus>.Success(new AgentsMdStatus(
                fullWs,
                agentsMdPath,
                Exists: true,
                SizeBytes: info.Length,
                LastModifiedUtc: new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                Preview: preview,
                ApproximateTokens: AgentsMdComposer.EstimateTokens(content),
                CharacterCount: content.Length));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result<AgentsMdStatus>.Failure(WrapperErrors.IoFailure($"Failed to write AGENTS.md: {ex.Message}"));
        }
    }

    private static Result<(string FullWs, string AgentsMdPath)> ResolveAgentsMdPath(string workspacePath, string? targetPathOverride)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return Result<(string, string)>.Failure(WrapperErrors.InvalidWorkspaceDir(workspacePath));
        }

        try
        {
            var fullWs = Path.GetFullPath(workspacePath.Trim());
            var normalizedWs = Path.TrimEndingDirectorySeparator(fullWs) + Path.DirectorySeparatorChar;
            string candidate;
            if (string.IsNullOrWhiteSpace(targetPathOverride))
            {
                candidate = Path.Combine(fullWs, "AGENTS.md");
            }
            else
            {
                var trimmed = targetPathOverride.Trim();
                var combined = Path.IsPathRooted(trimmed)
                    ? trimmed
                    : Path.Combine(fullWs, trimmed);
                candidate = trimmed.EndsWith("AGENTS.md", StringComparison.OrdinalIgnoreCase)
                    ? combined
                    : Path.Combine(combined, "AGENTS.md");
            }

            var target = Path.GetFullPath(candidate);
            if (!target.StartsWith(normalizedWs, StringComparison.OrdinalIgnoreCase))
            {
                return Result<(string, string)>.Failure(WrapperErrors.InvalidPath(target));
            }

            return Result<(string, string)>.Success((fullWs, target));
        }
        catch (Exception ex)
        {
            return Result<(string, string)>.Failure(WrapperErrors.InvalidWorkspaceDir($"{workspacePath} ({ex.Message})"));
        }
    }
}
