using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Infrastructure.Workspace;

/// <summary>Confined atomic reader/writer for Active Workspace AGENTS.md exports.</summary>
public sealed class AgentsMdFileWriter : IAgentsMdFileWriter
{
    private const long MaxPreviewBytes = 512 * 1024;

    public async Task<Result<AgentsMdStatus>> GetStatusAsync(string workspacePath, CancellationToken ct = default)
    {
        var pathRes = ResolveAgentsMdPath(workspacePath);
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
            var preview = info.Length <= MaxPreviewBytes
                ? await File.ReadAllTextAsync(agentsMdPath, ct).ConfigureAwait(false)
                : "(AGENTS.md exceeds 512 KB preview limit — open the workspace folder to inspect.)";

            return Result<AgentsMdStatus>.Success(new AgentsMdStatus(
                fullWs,
                agentsMdPath,
                Exists: true,
                SizeBytes: info.Length,
                LastModifiedUtc: new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                Preview: preview));
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

    public async Task<Result<AgentsMdStatus>> WriteAsync(
        string workspacePath,
        string content,
        bool overwriteConfirmed,
        CancellationToken ct = default)
    {
        var pathRes = ResolveAgentsMdPath(workspacePath);
        if (pathRes.IsFailure)
        {
            return Result<AgentsMdStatus>.Failure(pathRes.Error!);
        }

        var (fullWs, agentsMdPath) = pathRes.Value;
        if (File.Exists(agentsMdPath) && !overwriteConfirmed)
        {
            return Result<AgentsMdStatus>.Failure(WrapperErrors.AgentsMdAlreadyExists(agentsMdPath));
        }

        string? tmpPath = null;
        try
        {
            Directory.CreateDirectory(fullWs);
            tmpPath = $"{agentsMdPath}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(tmpPath, content, ct).ConfigureAwait(false);
            File.Move(tmpPath, agentsMdPath, overwrite: true);

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
                Preview: preview));
        }
        catch (Exception ex)
        {
            if (tmpPath is not null && File.Exists(tmpPath))
            {
                try { File.Delete(tmpPath); } catch { }
            }

            return Result<AgentsMdStatus>.Failure(WrapperErrors.IoFailure($"Failed to write AGENTS.md: {ex.Message}"));
        }
    }

    private static Result<(string FullWs, string AgentsMdPath)> ResolveAgentsMdPath(string workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return Result<(string, string)>.Failure(WrapperErrors.InvalidWorkspaceDir(workspacePath));
        }

        try
        {
            var fullWs = Path.GetFullPath(workspacePath.Trim());
            var target = Path.GetFullPath(Path.Combine(fullWs, "AGENTS.md"));
            var normalizedWs = Path.TrimEndingDirectorySeparator(fullWs) + Path.DirectorySeparatorChar;
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
