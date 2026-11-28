using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;
using Meloberry.Wrapper.Domain.Services;

namespace Meloberry.Wrapper.Application.UseCases;

/// <summary>Request payload for exporting a targeted AGENTS.md into the Active Workspace.</summary>
public sealed record ReadyWorkspaceRequest(
    PromptDraft Draft,
    bool OverwriteConfirmed = false,
    string? TargetDirectory = null);

/// <summary>
/// Exports a tailored <c>AGENTS.md</c> file by delegating core CLI/schema rendering to
/// <c>mby prime --format agents-md</c> and appending Wrapper-specific staged inputs &amp; live-control hints.
/// </summary>
public sealed class ReadyWorkspaceHandler(
    IAgentsMdFileWriter agentsMdWriter,
    IManagedAppsStore appsStore,
    IMbyCliClient mbyClient,
    IMelomgCliClient melomgClient,
    ISettingsStore settingsStore,
    LoadSpecCatalogHandler specLoader)
{
    public async Task<Result<AgentsMdStatus>> GetStatusAsync(
        string? workspacePathOverride,
        string? targetDirectoryOverride = null,
        CancellationToken ct = default)
    {
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var wsPath = !string.IsNullOrWhiteSpace(workspacePathOverride)
            ? workspacePathOverride
            : settings.ActiveWorkspace?.WorkspacePath;

        if (string.IsNullOrWhiteSpace(wsPath))
        {
            return Result<AgentsMdStatus>.Failure(WrapperErrors.InvalidWorkspaceDir("No Active Workspace selected."));
        }

        string? resolvedOverride = targetDirectoryOverride;
        if (string.IsNullOrWhiteSpace(resolvedOverride)
            && settings.ActiveWorkspace is not null
            && PathNormalization.AreSameDirectory(settings.ActiveWorkspace.WorkspacePath, wsPath)
            && !string.IsNullOrWhiteSpace(settings.ActiveWorkspace.LastAgentsMdPath)
            && File.Exists(settings.ActiveWorkspace.LastAgentsMdPath))
        {
            resolvedOverride = settings.ActiveWorkspace.LastAgentsMdPath;
        }

        var statusRes = await agentsMdWriter.GetStatusAsync(wsPath, resolvedOverride, ct).ConfigureAwait(false);
        if (statusRes.IsSuccess && !statusRes.Value.Exists && !string.IsNullOrWhiteSpace(resolvedOverride) && string.IsNullOrWhiteSpace(targetDirectoryOverride))
        {
            return await agentsMdWriter.GetStatusAsync(wsPath, null, ct).ConfigureAwait(false);
        }

        return statusRes;
    }

    public async Task<Result<AgentsMdExportResult>> ReadyWorkspaceAsync(
        ReadyWorkspaceRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var wsPath = !string.IsNullOrWhiteSpace(request.Draft.WorkingDirectory)
            ? request.Draft.WorkingDirectory!
            : settings.ActiveWorkspace?.WorkspacePath;

        if (string.IsNullOrWhiteSpace(wsPath))
        {
            return Result<AgentsMdExportResult>.Failure(WrapperErrors.InvalidWorkspaceDir("Select an Active Workspace before calling Ready Workspace."));
        }

        if (!PathNormalization.TryNormalizeDirectoryPath(wsPath, out var fullWs))
        {
            return Result<AgentsMdExportResult>.Failure(WrapperErrors.InvalidWorkspaceDir(wsPath));
        }

        string? validatedTargetDir = null;
        if (!string.IsNullOrWhiteSpace(request.TargetDirectory))
        {
            var candidate = Path.IsPathRooted(request.TargetDirectory.Trim())
                ? request.TargetDirectory.Trim()
                : Path.Combine(fullWs, request.TargetDirectory.Trim());
            if (!PathNormalization.TryNormalizeDirectoryPath(candidate, out var fullTarget))
            {
                return Result<AgentsMdExportResult>.Failure(WrapperErrors.InvalidPath(request.TargetDirectory));
            }

            var wsPrefix = Path.TrimEndingDirectorySeparator(fullWs) + Path.DirectorySeparatorChar;
            if (!PathNormalization.AreSameDirectory(fullTarget, fullWs)
                && !fullTarget.StartsWith(wsPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return Result<AgentsMdExportResult>.Failure(WrapperErrors.InvalidPath(fullTarget));
            }

            validatedTargetDir = fullTarget;
        }

        var preStatus = await agentsMdWriter.GetStatusAsync(fullWs, validatedTargetDir, ct).ConfigureAwait(false);
        if (preStatus.IsFailure)
        {
            return Result<AgentsMdExportResult>.Failure(preStatus.Error!);
        }

        var alreadyExisted = preStatus.Value.Exists;
        if (alreadyExisted && !request.OverwriteConfirmed)
        {
            return Result<AgentsMdExportResult>.Failure(WrapperErrors.AgentsMdAlreadyExists(preStatus.Value.AgentsMdPath));
        }

        var mbyExePath = await appsStore.ResolveToolExecutableAsync("mby", fullWs, settings.MbyExecutableOverride, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(mbyExePath))
        {
            return Result<AgentsMdExportResult>.Failure(WrapperErrors.CliNotFound("mby"));
        }

        var verRes = await mbyClient.GetVersionInfoAsync(mbyExePath, ct).ConfigureAwait(false);
        if (verRes.IsFailure)
        {
            return Result<AgentsMdExportResult>.Failure(WrapperErrors.CliIncompatible(
                "unknown",
                MbyCliCompatibility.MinimumDisplayVersion,
                verRes.Error!.Description));
        }

        if (!verRes.Value.IsCompatible)
        {
            return Result<AgentsMdExportResult>.Failure(WrapperErrors.CliIncompatible(
                verRes.Value.Version,
                MbyCliCompatibility.MinimumDisplayVersion,
                verRes.Value.IncompatibilityReason ?? string.Empty));
        }

        var apps = await appsStore.ScanAppsAsync(fullWs, ct).ConfigureAwait(false);
        var workspaceMelomg = apps.FirstOrDefault(a => a.Kind == ManagedAppKind.Melomg && a.IsPresentInWorkspace)?.WorkspaceFileName;
        var extraWorkspaceExes = apps
            .Where(a => a.Kind == ManagedAppKind.CustomExecutable && a.IsPresentInWorkspace)
            .Select(a => a.WorkspaceFileName ?? a.FileName)
            .ToList();
        var melomgExePath = await appsStore.ResolveToolExecutableAsync("melomg", fullWs, settings.MelomgExecutableOverride, ct).ConfigureAwait(false);

        var (liveCatalog, _, _) = await specLoader.LoadAsync(false, ct).ConfigureAwait(false);
        var catalogByTopic = SpecCatalogDefaults.BuiltInFallbackCatalog.ToDictionary(s => s.Topic, StringComparer.OrdinalIgnoreCase);
        foreach (var item in liveCatalog)
        {
            catalogByTopic[item.Topic] = item;
        }

        var topicSet = new List<string>();
        void AddUnique(string t)
        {
            if (!topicSet.Contains(t, StringComparer.OrdinalIgnoreCase))
            {
                topicSet.Add(t);
            }
        }

        switch (request.Draft.Container)
        {
            case ContainerKind.Session:
                AddUnique("session");
                AddUnique("article");
                AddUnique("quiz");
                break;
            case ContainerKind.StandaloneArticle:
                AddUnique("article");
                break;
            case ContainerKind.StandaloneQuiz:
                AddUnique("quiz");
                break;
        }

        if (request.Draft.AuthoringFormat is AuthoringFormat.MarkdownDirectory or AuthoringFormat.SingleMarkdown)
        {
            AddUnique("markdown");
        }

        var allSelections = request.Draft.Components is { Count: > 0 }
            ? PromptComposer.ResolveEffectiveComponents(request.Draft, catalogByTopic)
                .SelectMany(c => c.Selections)
            : request.Draft.Selections.Where(s => s.Enabled);

        foreach (var sel in allSelections)
        {
            var category = catalogByTopic.TryGetValue(sel.Topic, out var meta)
                ? meta.Category
                : SpecCatalogDefaults.GetCategoryForTopic(sel.Topic);
            if (PromptComposer.IsAllowedInContainer(request.Draft.Container, category))
            {
                AddUnique(sel.Topic);
            }
        }

        if (request.Draft.Container == ContainerKind.StandaloneQuiz &&
            !topicSet.Any(t => (catalogByTopic.TryGetValue(t, out var m) ? m.Category : SpecCatalogDefaults.GetCategoryForTopic(t)) == "question"))
        {
            AddUnique("mcq");
        }

        var normalizedWsPrefix = Path.TrimEndingDirectorySeparator(fullWs) + Path.DirectorySeparatorChar;
        var usesShim = File.Exists(Path.Combine(fullWs, "mby-pc.bat"));
        var cliPrefix = usesShim
            ? ".\\mby-pc.bat mby"
            : WrapperAgentsAppendixComposer.FormatCliCommand(
                FormatResolvedToolForAgentsMd(mbyExePath, fullWs, normalizedWsPrefix),
                "mby");

        var containerArg = request.Draft.Container switch
        {
            ContainerKind.StandaloneArticle => "article",
            ContainerKind.StandaloneQuiz => "quiz",
            _ => "session"
        };
        var authoringFmtArg = request.Draft.AuthoringFormat switch
        {
            AuthoringFormat.SingleMarkdown => "singleMarkdown",
            AuthoringFormat.Json => "json",
            _ => "markdownDirectory"
        };

        var primeRes = await mbyClient.PrimeAgentsMdAsync(
            mbyExePath,
            topicSet,
            containerArg,
            authoringFmtArg,
            cliPrefix,
            fullWs,
            ct).ConfigureAwait(false);
        if (primeRes.IsFailure)
        {
            return Result<AgentsMdExportResult>.Failure(primeRes.Error!);
        }

        var warnings = new List<string>();
        string? melomgText = null;
        var needsMelomg = !string.IsNullOrWhiteSpace(workspaceMelomg)
            || request.Draft.UseMelomgForPdfs
            || topicSet.Contains("relabel", StringComparer.OrdinalIgnoreCase)
            || request.Draft.Attachments.Any(a => a.FilePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));

        if (needsMelomg && !string.IsNullOrWhiteSpace(melomgExePath))
        {
            var melomgRes = await melomgClient.GetSpecTextAsync(melomgExePath, fullWs, ["extract", "mask"], ct).ConfigureAwait(false);
            if (melomgRes.IsSuccess)
            {
                melomgText = melomgRes.Value;
            }
            else
            {
                warnings.Add($"Failed to fetch live melomg specs: {melomgRes.Error!.Description}");
            }
        }

        var stagedFiles = new List<string>();
        if (settings.ActiveWorkspace is not null && PathNormalization.AreSameDirectory(settings.ActiveWorkspace.WorkspacePath, fullWs))
        {
            foreach (var stagedFile in (settings.ActiveWorkspace.StagedItems ?? []).Where(i => i.Kind == StagedCopyKind.File))
            {
                if (TryResolveExistingWorkspaceFile(fullWs, normalizedWsPrefix, stagedFile.TargetRelativePath, out var relFile))
                {
                    stagedFiles.Add(relFile);
                }
            }
        }

        var externalAttachments = new List<string>();
        foreach (var att in request.Draft.Attachments)
        {
            if (string.IsNullOrWhiteSpace(att.FilePath))
            {
                continue;
            }

            try
            {
                var fullAtt = Path.GetFullPath(att.FilePath.Trim());
                if (fullAtt.StartsWith(normalizedWsPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(fullAtt))
                    {
                        stagedFiles.Add(Path.GetRelativePath(fullWs, fullAtt).Replace('\\', '/'));
                    }
                }
                else
                {
                    externalAttachments.Add(fullAtt);
                }
            }
            catch
            {
                externalAttachments.Add(att.FilePath);
            }
        }

        var distinctStagedFiles = stagedFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var distinctExternalAttachments = externalAttachments.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var candidateFolders = (settings.ActiveWorkspace is not null && PathNormalization.AreSameDirectory(settings.ActiveWorkspace.WorkspacePath, fullWs)
                ? settings.ActiveWorkspace.StagedItems ?? []
                : [])
            .Where(i => i.Kind == StagedCopyKind.Folder)
            .Select(i => i.TargetRelativePath)
            .Concat(request.Draft.CopiedFolders ?? []);

        var stagedFolders = new List<string>();
        foreach (var candidateFolder in candidateFolders)
        {
            if (TryResolveExistingWorkspaceFolder(fullWs, normalizedWsPrefix, candidateFolder, out var relFolder))
            {
                stagedFolders.Add(relFolder);
            }
        }
        stagedFolders = stagedFolders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var appendixInput = new WrapperAgentsAppendixInput(
            WorkspacePath: fullWs,
            WorkspaceMelomgFileName: FormatResolvedToolForAgentsMd(melomgExePath, fullWs, normalizedWsPrefix),
            ExtraWorkspaceExecutables: extraWorkspaceExes,
            CopiedFiles: distinctStagedFiles,
            CopiedFolders: stagedFolders,
            MelomgSpecsText: melomgText,
            ExternalAttachments: distinctExternalAttachments,
            UsesWorkspaceShim: usesShim,
            WorkflowMode: request.Draft.WorkflowMode);

        var markdown = WrapperAgentsAppendixComposer.CombineWithBaseAgentsMd(primeRes.Value.Markdown, appendixInput);
        var writeRes = await agentsMdWriter.WriteAsync(fullWs, validatedTargetDir, markdown, request.OverwriteConfirmed, ct).ConfigureAwait(false);
        if (writeRes.IsFailure)
        {
            return Result<AgentsMdExportResult>.Failure(writeRes.Error!);
        }

        if (settings.ActiveWorkspace is not null && PathNormalization.AreSameDirectory(settings.ActiveWorkspace.WorkspacePath, fullWs))
        {
            var relArtifact = Path.GetRelativePath(fullWs, writeRes.Value.AgentsMdPath).Replace('\\', '/');
            await settingsStore.UpdateAsync(current =>
            {
                if (current.ActiveWorkspace is null || !PathNormalization.AreSameDirectory(current.ActiveWorkspace.WorkspacePath, fullWs))
                {
                    return current;
                }

                var artifacts = (current.ActiveWorkspace.LastReadyWorkspaceArtifacts ?? [])
                    .Concat([relArtifact])
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var updatedState = current.ActiveWorkspace with
                {
                    AgentsMdExportedUtc = DateTimeOffset.UtcNow,
                    LastAgentsMdPath = writeRes.Value.AgentsMdPath,
                    LastReadyWorkspaceArtifacts = artifacts
                };
                return current with { ActiveWorkspace = updatedState };
            }, ct).ConfigureAwait(false);
        }

        var included = primeRes.Value.IncludedTopics.Count > 0
            ? primeRes.Value.IncludedTopics.ToList()
            : topicSet;

        return Result<AgentsMdExportResult>.Success(new AgentsMdExportResult(
            OutputPath: writeRes.Value.AgentsMdPath,
            OverwroteExisting: alreadyExisted,
            BytesWritten: System.Text.Encoding.UTF8.GetByteCount(markdown),
            ApproximateTokens: TokenEstimator.EstimateTokens(markdown),
            IncludedTopics: included,
            MarkdownPreview: markdown,
            Warnings: warnings.Count > 0 ? warnings : null,
            CharacterCount: markdown.Length));
    }

    private static string? FormatResolvedToolForAgentsMd(string? resolvedExePath, string fullWs, string normalizedWsPrefix)
    {
        if (string.IsNullOrWhiteSpace(resolvedExePath))
        {
            return null;
        }

        try
        {
            var fullExe = Path.GetFullPath(resolvedExePath.Trim());
            if (fullExe.StartsWith(normalizedWsPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetRelativePath(fullWs, fullExe);
            }

            return fullExe;
        }
        catch
        {
            return resolvedExePath;
        }
    }

    private static bool TryResolveExistingWorkspaceFile(
        string fullWs,
        string normalizedWsPrefix,
        string? relativePath,
        out string normalizedRelative)
    {
        normalizedRelative = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        try
        {
            var candidate = Path.GetFullPath(Path.Combine(fullWs, relativePath.Trim()));
            if (!candidate.StartsWith(normalizedWsPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate))
            {
                return false;
            }

            normalizedRelative = Path.GetRelativePath(fullWs, candidate).Replace('\\', '/');
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryResolveExistingWorkspaceFolder(
        string fullWs,
        string normalizedWsPrefix,
        string? relativePath,
        out string normalizedRelative)
    {
        normalizedRelative = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        try
        {
            var candidate = Path.GetFullPath(Path.Combine(fullWs, relativePath.Trim()));
            if (PathNormalization.AreSameDirectory(candidate, fullWs)
                || !candidate.StartsWith(normalizedWsPrefix, StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(candidate))
            {
                return false;
            }

            normalizedRelative = Path.GetRelativePath(fullWs, candidate).Replace('\\', '/').TrimEnd('/');
            return !string.IsNullOrWhiteSpace(normalizedRelative);
        }
        catch
        {
            return false;
        }
    }
}
