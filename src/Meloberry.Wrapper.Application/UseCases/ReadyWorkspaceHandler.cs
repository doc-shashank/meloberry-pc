using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;
using Meloberry.Wrapper.Domain.Services;

namespace Meloberry.Wrapper.Application.UseCases;

/// <summary>Request payload for exporting a targeted AGENTS.md into the Active Workspace.</summary>
public sealed record ReadyWorkspaceRequest(
    PromptDraft Draft,
    bool OverwriteConfirmed = false);

/// <summary>
/// Exports a tailored <c>AGENTS.md</c> file containing the workspace playbook and live targeted CLI schemas
/// for the user's selected shopping-list specs, warning explicitly before overwriting an existing file.
/// </summary>
public sealed class ReadyWorkspaceHandler(
    IAgentsMdFileWriter agentsMdWriter,
    IManagedAppsStore appsStore,
    IMbyCliClient mbyClient,
    IMelomgCliClient melomgClient,
    ISettingsStore settingsStore)
{
    public async Task<Result<AgentsMdStatus>> GetStatusAsync(string? workspacePathOverride, CancellationToken ct = default)
    {
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var wsPath = !string.IsNullOrWhiteSpace(workspacePathOverride)
            ? workspacePathOverride
            : settings.ActiveWorkspace?.WorkspacePath;

        if (string.IsNullOrWhiteSpace(wsPath))
        {
            return Result<AgentsMdStatus>.Failure(WrapperErrors.InvalidWorkspaceDir("No Active Workspace selected."));
        }

        return await agentsMdWriter.GetStatusAsync(wsPath, ct).ConfigureAwait(false);
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

        var fullWs = Path.GetFullPath(wsPath);
        var preStatus = await agentsMdWriter.GetStatusAsync(fullWs, ct).ConfigureAwait(false);
        if (preStatus.IsFailure)
        {
            return Result<AgentsMdExportResult>.Failure(preStatus.Error!);
        }

        var alreadyExisted = preStatus.Value.Exists;
        if (alreadyExisted && !request.OverwriteConfirmed)
        {
            return Result<AgentsMdExportResult>.Failure(WrapperErrors.AgentsMdAlreadyExists(preStatus.Value.AgentsMdPath));
        }

        // Collect workspace executables and staged/copied files & folders.
        var apps = await appsStore.ScanAppsAsync(fullWs, ct).ConfigureAwait(false);
        var workspaceMby = apps.FirstOrDefault(a => a.Kind == ManagedAppKind.Mby && a.IsPresentInWorkspace)?.FileName;
        var workspaceMelomg = apps.FirstOrDefault(a => a.Kind == ManagedAppKind.Melomg && a.IsPresentInWorkspace)?.FileName;
        var extraWorkspaceExes = apps
            .Where(a => a.Kind == ManagedAppKind.CustomExecutable && a.IsPresentInWorkspace)
            .Select(a => a.FileName)
            .ToList();

        var mbyExePath = await appsStore.ResolveToolExecutableAsync("mby", fullWs, settings.MbyExecutableOverride, ct).ConfigureAwait(false);
        var melomgExePath = await appsStore.ResolveToolExecutableAsync("melomg", fullWs, settings.MelomgExecutableOverride, ct).ConfigureAwait(false);

        // Compute targeted spec topics from container + format + enabled shopping list selections.
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

        var includeLink = false;
        var allSelections = request.Draft.Components is { Count: > 0 }
            ? PromptComposer.ResolveEffectiveComponents(
                request.Draft,
                SpecCatalogDefaults.BuiltInFallbackCatalog.ToDictionary(s => s.Topic, StringComparer.OrdinalIgnoreCase))
                .SelectMany(c => c.Selections)
            : request.Draft.Selections.Where(s => s.Enabled);

        foreach (var sel in allSelections)
        {
            if (string.Equals(sel.Topic, SpecCatalogDefaults.VirtualLinkTopicName, StringComparison.OrdinalIgnoreCase))
            {
                includeLink = true;
                continue;
            }

            if (PromptComposer.IsAllowedInContainer(request.Draft.Container, InferCategory(sel.Topic)))
            {
                AddUnique(sel.Topic);
            }
        }

        if (request.Draft.Container == ContainerKind.StandaloneQuiz && !topicSet.Any(t => InferCategory(t) == "question"))
        {
            AddUnique("mcq");
        }

        IReadOnlyList<AgentsMdSpecDetail> specDetails = [];
        if (!string.IsNullOrWhiteSpace(mbyExePath))
        {
            var detailRes = await mbyClient.GetSpecDetailsAsync(mbyExePath, fullWs, topicSet, ct).ConfigureAwait(false);
            if (detailRes.IsSuccess)
            {
                specDetails = detailRes.Value;
            }
        }

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
        }

        var stagedFiles = (settings.ActiveWorkspace?.StagedItems ?? [])
            .Where(i => i.Kind == StagedCopyKind.File)
            .Select(i => i.TargetRelativePath)
            .Concat(request.Draft.Attachments.Select(a => Path.GetFileName(a.FilePath)))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var stagedFolders = (settings.ActiveWorkspace?.StagedItems ?? [])
            .Where(i => i.Kind == StagedCopyKind.Folder)
            .Select(i => i.TargetRelativePath)
            .Concat(request.Draft.CopiedFolders ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var composeInput = new AgentsMdComposeInput(
            WorkspacePath: fullWs,
            Container: request.Draft.Container,
            AuthoringFormat: request.Draft.AuthoringFormat,
            WorkspaceMbyFileName: workspaceMby ?? (!string.IsNullOrWhiteSpace(mbyExePath) ? mbyExePath : null),
            WorkspaceMelomgFileName: workspaceMelomg ?? (!string.IsNullOrWhiteSpace(melomgExePath) ? melomgExePath : null),
            ExtraWorkspaceExecutables: extraWorkspaceExes,
            CopiedFiles: stagedFiles,
            CopiedFolders: stagedFolders,
            SelectedSpecs: specDetails,
            MelomgSpecsText: melomgText,
            IncludeLinkBlock: includeLink);

        var markdown = AgentsMdComposer.Compose(composeInput);
        var writeRes = await agentsMdWriter.WriteAsync(fullWs, markdown, request.OverwriteConfirmed, ct).ConfigureAwait(false);
        if (writeRes.IsFailure)
        {
            return Result<AgentsMdExportResult>.Failure(writeRes.Error!);
        }

        if (settings.ActiveWorkspace is not null &&
            string.Equals(settings.ActiveWorkspace.WorkspacePath, fullWs, StringComparison.OrdinalIgnoreCase))
        {
            var updatedState = settings.ActiveWorkspace with { AgentsMdExportedUtc = DateTimeOffset.UtcNow };
            await settingsStore.SaveAsync(settings with { ActiveWorkspace = updatedState }, ct).ConfigureAwait(false);
        }

        var included = specDetails.Select(s => s.Topic)
            .Concat(includeLink ? [SpecCatalogDefaults.VirtualLinkTopicName] : [])
            .ToList();

        return Result<AgentsMdExportResult>.Success(new AgentsMdExportResult(
            OutputPath: writeRes.Value.AgentsMdPath,
            OverwroteExisting: alreadyExisted,
            BytesWritten: System.Text.Encoding.UTF8.GetByteCount(markdown),
            ApproximateTokens: AgentsMdComposer.EstimateTokens(markdown),
            IncludedTopics: included,
            MarkdownPreview: markdown));
    }

    private static string InferCategory(string topic) => topic.ToLowerInvariant() switch
    {
        "text" or "image" or "callout" or "table" or "link" => "block",
        "session" or "article" or "quiz" => "container",
        "mcq" or "flashcard" or "fill-blank" or "assertion-reason" or "spot-error" or "diff" or "mechanism" or "relabel" => "question",
        _ => "other"
    };
}
