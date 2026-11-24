using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;
using Meloberry.Wrapper.Infrastructure.GitHub;

namespace Meloberry.Wrapper.Infrastructure.Workspace;

/// <summary>
/// Implements recursive Meloberry executable Reset across the Active Workspace + subfolders
/// and offline outdated-executable detection/updating against %LOCALAPPDATA%\Meloberry\apps\.
/// </summary>
public sealed class WorkspaceMaintenanceService(
    IWrapperDataPaths dataPaths,
    IManagedAppsStore appsStore) : IWorkspaceMaintenanceService
{
    public async Task<Result<WorkspaceResetPlan>> PlanResetAsync(
        string workspacePath,
        WorkspaceResetScope scope,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return Result<WorkspaceResetPlan>.Failure(WrapperErrors.InvalidWorkspaceDir(workspacePath));
        }

        string fullWs;
        try
        {
            fullWs = Path.GetFullPath(workspacePath.Trim());
        }
        catch
        {
            return Result<WorkspaceResetPlan>.Failure(WrapperErrors.InvalidWorkspaceDir(workspacePath));
        }

        if (!Directory.Exists(fullWs))
        {
            return Result<WorkspaceResetPlan>.Success(new WorkspaceResetPlan(fullWs, scope, [], null));
        }

        var scannedApps = await appsStore.ScanAppsAsync(null, ct).ConfigureAwait(false);
        var knownAppNames = new HashSet<string>(
            scannedApps.Where(a => a.IsInstalled).Select(a => a.FileName),
            StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(dataPaths.AppsDirectory))
        {
            foreach (var exe in Directory.GetFiles(dataPaths.AppsDirectory, "*.exe", SearchOption.TopDirectoryOnly))
            {
                knownAppNames.Add(Path.GetFileName(exe));
            }
        }

        var toRemove = new List<string>();
        foreach (var file in EnumerateWorkspaceExes(fullWs))
        {
            var name = Path.GetFileName(file);
            if (ManagedAppsStore.IsMbyFileName(name) ||
                ManagedAppsStore.IsMelomgFileName(name) ||
                knownAppNames.Contains(name))
            {
                toRemove.Add(Path.GetRelativePath(fullWs, file).Replace('\\', '/'));
            }
        }

        toRemove.Sort(StringComparer.OrdinalIgnoreCase);
        var agentsPath = Path.Combine(fullWs, "AGENTS.md");
        var agentsRel = scope == WorkspaceResetScope.ExecutablesAndAgentsMd && File.Exists(agentsPath)
            ? "AGENTS.md"
            : null;

        return Result<WorkspaceResetPlan>.Success(new WorkspaceResetPlan(fullWs, scope, toRemove, agentsRel));
    }

    public async Task<Result<WorkspaceResetResult>> ExecuteResetAsync(
        string workspacePath,
        WorkspaceResetScope scope,
        CancellationToken ct = default)
    {
        var planRes = await PlanResetAsync(workspacePath, scope, ct).ConfigureAwait(false);
        if (planRes.IsFailure)
        {
            return Result<WorkspaceResetResult>.Failure(planRes.Error!);
        }

        var plan = planRes.Value;
        var fullRoot = Path.TrimEndingDirectorySeparator(plan.WorkspacePath) + Path.DirectorySeparatorChar;
        var removed = new List<string>();

        foreach (var rel in plan.ExecutablesToRemove)
        {
            var candidate = Path.GetFullPath(Path.Combine(plan.WorkspacePath, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                return Result<WorkspaceResetResult>.Failure(WrapperErrors.InvalidPath(candidate));
            }

            if (File.Exists(candidate))
            {
                File.Delete(candidate);
                removed.Add(rel);
            }
        }

        var removedAgents = false;
        if (!string.IsNullOrWhiteSpace(plan.AgentsMdToRemove))
        {
            var agentsTarget = Path.GetFullPath(Path.Combine(plan.WorkspacePath, "AGENTS.md"));
            if (agentsTarget.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(agentsTarget))
            {
                File.Delete(agentsTarget);
                removedAgents = true;
            }
        }

        return Result<WorkspaceResetResult>.Success(new WorkspaceResetResult(plan.WorkspacePath, removed, removedAgents));
    }

    public async Task<Result<IReadOnlyList<OutdatedWorkspaceTool>>> DetectOutdatedToolsAsync(
        string workspacePath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return Result<IReadOnlyList<OutdatedWorkspaceTool>>.Failure(WrapperErrors.InvalidWorkspaceDir(workspacePath));
        }

        var fullWs = Path.GetFullPath(workspacePath.Trim());
        if (!Directory.Exists(fullWs))
        {
            return Result<IReadOnlyList<OutdatedWorkspaceTool>>.Success([]);
        }

        var apps = await appsStore.ScanAppsAsync(null, ct).ConfigureAwait(false);
        var latestMby = apps.FirstOrDefault(a => a.Kind == ManagedAppKind.Mby && a.IsInstalled && !string.IsNullOrWhiteSpace(a.Version));
        var latestMelomg = apps.FirstOrDefault(a => a.Kind == ManagedAppKind.Melomg && a.IsInstalled && !string.IsNullOrWhiteSpace(a.Version));

        var outdated = new List<OutdatedWorkspaceTool>();
        foreach (var file in EnumerateWorkspaceExes(fullWs))
        {
            var name = Path.GetFileName(file);
            var rel = Path.GetRelativePath(fullWs, file).Replace('\\', '/');

            if (latestMby is not null && ManagedAppsStore.IsMbyFileName(name))
            {
                var wsVer = GitHubHttpClientHelper.ExtractVersionFromText(name) ?? "0.0.0";
                if (GitHubHttpClientHelper.CompareSemver(latestMby.Version, wsVer) > 0)
                {
                    outdated.Add(new OutdatedWorkspaceTool(
                        ToolKey: "mby",
                        DisplayName: "Meloberry CLI (mby)",
                        WorkspaceFileName: name,
                        WorkspaceRelativePath: rel,
                        WorkspaceFullPath: file,
                        WorkspaceVersion: wsVer,
                        LatestAppFileName: latestMby.FileName,
                        LatestAppVersion: latestMby.Version!));
                }
            }
            else if (latestMelomg is not null && ManagedAppsStore.IsMelomgFileName(name))
            {
                var wsVer = GitHubHttpClientHelper.ExtractVersionFromText(name) ?? "0.0.0";
                if (GitHubHttpClientHelper.CompareSemver(latestMelomg.Version, wsVer) > 0)
                {
                    outdated.Add(new OutdatedWorkspaceTool(
                        ToolKey: "melomg",
                        DisplayName: "Meloberry Melomg (melomg)",
                        WorkspaceFileName: name,
                        WorkspaceRelativePath: rel,
                        WorkspaceFullPath: file,
                        WorkspaceVersion: wsVer,
                        LatestAppFileName: latestMelomg.FileName,
                        LatestAppVersion: latestMelomg.Version!));
                }
            }
        }

        return Result<IReadOnlyList<OutdatedWorkspaceTool>>.Success(outdated);
    }

    public async Task<Result<WorkspaceToolUpdateResult>> UpdateOutdatedToolsAsync(
        string workspacePath,
        CancellationToken ct = default)
    {
        var detectRes = await DetectOutdatedToolsAsync(workspacePath, ct).ConfigureAwait(false);
        if (detectRes.IsFailure)
        {
            return Result<WorkspaceToolUpdateResult>.Failure(detectRes.Error!);
        }

        var fullWs = Path.GetFullPath(workspacePath.Trim());
        var fullRoot = Path.TrimEndingDirectorySeparator(fullWs) + Path.DirectorySeparatorChar;
        var apps = await appsStore.ScanAppsAsync(null, ct).ConfigureAwait(false);

        var removed = new List<string>();
        var toolKeysToCopy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in detectRes.Value)
        {
            var fullTarget = Path.GetFullPath(item.WorkspaceFullPath);
            if (!fullTarget.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                return Result<WorkspaceToolUpdateResult>.Failure(WrapperErrors.InvalidPath(fullTarget));
            }

            if (File.Exists(fullTarget))
            {
                File.Delete(fullTarget);
                removed.Add(item.WorkspaceRelativePath);
            }

            toolKeysToCopy.Add(item.ToolKey);
        }

        var copiedNew = new List<string>();
        foreach (var key in toolKeysToCopy)
        {
            var kind = string.Equals(key, "melomg", StringComparison.OrdinalIgnoreCase)
                ? ManagedAppKind.Melomg
                : ManagedAppKind.Mby;
            var latest = apps.FirstOrDefault(a => a.Kind == kind && a.IsInstalled && !string.IsNullOrWhiteSpace(a.FullPath));
            if (latest?.FullPath is null || !File.Exists(latest.FullPath))
            {
                continue;
            }

            var destPath = Path.Combine(fullWs, latest.FileName);
            var tmpPath = destPath + ".mbytmp";
            File.Copy(latest.FullPath, tmpPath, overwrite: true);
            File.Move(tmpPath, destPath, overwrite: true);
            copiedNew.Add(latest.FileName);
        }

        return Result<WorkspaceToolUpdateResult>.Success(new WorkspaceToolUpdateResult(fullWs, removed, copiedNew));
    }

    private static IEnumerable<string> EnumerateWorkspaceExes(string workspaceRoot)
    {
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true
        };

        foreach (var file in Directory.EnumerateFiles(workspaceRoot, "*.exe", opts))
        {
            var rel = Path.GetRelativePath(workspaceRoot, file);
            var segments = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(s => string.Equals(s, ".git", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            yield return file;
        }
    }
}
