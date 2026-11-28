using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;
using Meloberry.Wrapper.Domain.Services;

namespace Meloberry.Wrapper.Application.UseCases;

/// <summary>Authoritative live coordinator for reading and mutating per-workspace authoring drafts.</summary>
public sealed class LiveDraftHandler(
    IWorkspaceDraftStore draftStore,
    ISettingsStore settingsStore)
{
    public event Action<LiveDraftChangedEvent>? DraftChanged;

    public async Task<Result<(string WorkspacePath, PromptDraft Draft)>> GetDraftAsync(
        string? workspacePath = null,
        CancellationToken ct = default)
    {
        var resolvedWs = await ResolveWorkspacePathAsync(workspacePath, ct).ConfigureAwait(false);
        if (resolvedWs.IsFailure)
        {
            return Result<(string, PromptDraft)>.Failure(resolvedWs.Error!);
        }

        var draft = await draftStore.LoadAsync(resolvedWs.Value, ct).ConfigureAwait(false);
        return Result<(string, PromptDraft)>.Success((resolvedWs.Value, NormalizeDraft(draft, resolvedWs.Value)));
    }

    public async Task<Result<PromptDraft>> ReplaceDraftAsync(
        string? workspacePath,
        PromptDraft draft,
        string source = "ui",
        string summary = "Draft updated",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var resolvedWs = await ResolveWorkspacePathAsync(workspacePath, ct).ConfigureAwait(false);
        if (resolvedWs.IsFailure)
        {
            return Result<PromptDraft>.Failure(resolvedWs.Error!);
        }

        var fullWs = resolvedWs.Value;
        var normalized = NormalizeDraft(draft, fullWs);
        var saveRes = await draftStore.SaveAsync(fullWs, normalized, ct).ConfigureAwait(false);
        if (saveRes.IsFailure)
        {
            return saveRes;
        }

        DraftChanged?.Invoke(new LiveDraftChangedEvent(
            fullWs,
            saveRes.Value,
            string.IsNullOrWhiteSpace(source) ? "ui" : source,
            string.IsNullOrWhiteSpace(summary) ? "Draft updated" : summary));
        return saveRes;
    }

    public async Task<Result<LiveDraftMutationResult>> MutateAsync(
        string? workspacePath,
        LiveDraftMutationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var resolvedWs = await ResolveWorkspacePathAsync(workspacePath, ct).ConfigureAwait(false);
        if (resolvedWs.IsFailure)
        {
            return Result<LiveDraftMutationResult>.Failure(resolvedWs.Error!);
        }

        var fullWs = resolvedWs.Value;
        var current = NormalizeDraft(await draftStore.LoadAsync(fullWs, ct).ConfigureAwait(false), fullWs);
        var op = (request.Op ?? string.Empty).Trim().ToLowerInvariant();
        var source = string.IsNullOrWhiteSpace(request.Source) ? "agent" : request.Source.Trim();

        Result<LiveDraftMutationResult> mutationRes = op switch
        {
            "patch-meta" => ApplyPatchMeta(current, request),
            "add-comp" => ApplyAddComponent(current, request),
            "rm-comp" => ApplyRemoveComponent(current, request),
            "move-comp" => ApplyMoveComponent(current, request),
            "add-spec" => ApplyAddSpec(current, request),
            "update-spec" => ApplyUpdateSpec(current, request),
            "rm-spec" => ApplyRemoveSpec(current, request),
            "move-spec" => ApplyMoveSpec(current, request),
            "clone-spec" => ApplyCloneSpec(current, request),
            "move-spec-to" => ApplyMoveSpecTo(current, request),
            "set-content" => ApplySetContent(fullWs, current, request),
            _ => Result<LiveDraftMutationResult>.Failure(
                WrapperErrors.InvalidPromptDraft($"Unsupported live draft operation '{request.Op}'."))
        };

        if (mutationRes.IsFailure)
        {
            return mutationRes;
        }

        var normalized = NormalizeDraft(mutationRes.Value.Draft, fullWs);
        var saveRes = await draftStore.SaveAsync(fullWs, normalized, ct).ConfigureAwait(false);
        if (saveRes.IsFailure)
        {
            return Result<LiveDraftMutationResult>.Failure(saveRes.Error!);
        }

        var finalRes = mutationRes.Value with { Draft = saveRes.Value };
        var wfExplicit = op == "patch-meta" && string.IsNullOrWhiteSpace(request.TargetId) && request.WorkflowMode.HasValue;
        DraftChanged?.Invoke(new LiveDraftChangedEvent(
            fullWs,
            saveRes.Value,
            source,
            finalRes.Summary,
            finalRes.TargetComponentId,
            finalRes.TargetSpecId,
            op,
            wfExplicit));

        return Result<LiveDraftMutationResult>.Success(finalRes);
    }

    private async Task<Result<string>> ResolveWorkspacePathAsync(string? explicitWorkspacePath, CancellationToken ct)
    {
        var candidate = explicitWorkspacePath;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
            candidate = settings.ActiveWorkspace?.WorkspacePath;
        }

        if (!PathNormalization.TryNormalizeDirectoryPath(candidate, out var fullWs))
        {
            return Result<string>.Failure(WrapperErrors.InvalidWorkspaceDir(candidate ?? string.Empty));
        }

        return Result<string>.Success(fullWs);
    }

    private static Result<LiveDraftMutationResult> ApplyPatchMeta(PromptDraft current, LiveDraftMutationRequest req)
    {
        var comps = (current.Components ?? []).ToList();
        var nextTitle = req.Title ?? current.Title;
        if (!string.IsNullOrWhiteSpace(req.TargetId))
        {
            var idx = comps.FindIndex(c => string.Equals(c.Id, req.TargetId, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
            {
                return Result<LiveDraftMutationResult>.Failure(WrapperErrors.InvalidPromptDraft($"Component '{req.TargetId}' not found."));
            }

            var oldTitle = string.IsNullOrWhiteSpace(comps[idx].Title) ? comps[idx].Kind.ToString() : comps[idx].Title.Trim();
            var newTitle = !string.IsNullOrWhiteSpace(req.Title) ? req.Title.Trim() : oldTitle;
            comps[idx] = comps[idx] with { Title = newTitle };

            var updatedCompDraft = current with { Components = comps };
            return Result<LiveDraftMutationResult>.Success(new LiveDraftMutationResult(
                updatedCompDraft,
                $"Renamed \"{oldTitle}\" to \"{newTitle}\"",
                comps[idx].Id));
        }

        var changed = new List<string>();
        if (req.Title is not null) changed.Add($"title \"{req.Title.Trim()}\"");
        if (req.Container.HasValue) changed.Add($"container {FormatContainerLabel(req.Container.Value)}");
        if (req.Description is not null) changed.Add("description");
        if (req.WorkflowMode.HasValue) changed.Add($"mode {FormatWorkflowModeLabel(req.WorkflowMode.Value)}");
        var metaSummary = changed.Count > 0 ? $"Updated {string.Join(", ", changed)}" : "Updated project details";

        var next = current with
        {
            Container = req.Container ?? current.Container,
            Title = nextTitle,
            Description = req.Description ?? current.Description,
            AuthoringFormat = req.AuthoringFormat ?? current.AuthoringFormat,
            WorkflowMode = req.WorkflowMode ?? current.WorkflowMode,
            AllowPlaceholdersOnFinalize = req.AllowPlaceholdersOnFinalize ?? current.AllowPlaceholdersOnFinalize
        };
        return Result<LiveDraftMutationResult>.Success(new LiveDraftMutationResult(next, metaSummary));
    }

    private static Result<LiveDraftMutationResult> ApplyAddComponent(PromptDraft current, LiveDraftMutationRequest req)
    {
        var kind = req.ComponentKind ?? ComponentKind.Article;
        var comps = (current.Components ?? []).ToList();
        var id = $"{(kind == ComponentKind.Quiz ? "q" : "a")}_{Guid.NewGuid().ToString("N")[..6]}";
        var title = !string.IsNullOrWhiteSpace(req.Title)
            ? req.Title.Trim()
            : (kind == ComponentKind.Quiz ? "Practice Quiz" : "Study Article");
        comps.Add(new PromptComponentPlan(id, kind, title, [], []));
        var next = current with { Components = comps };
        return Result<LiveDraftMutationResult>.Success(new LiveDraftMutationResult(next, $"Added {kind} '{title}'", id));
    }

    private static Result<LiveDraftMutationResult> ApplyRemoveComponent(PromptDraft current, LiveDraftMutationRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.TargetId))
        {
            return Result<LiveDraftMutationResult>.Failure(WrapperErrors.InvalidPromptDraft("Target component ID is required."));
        }

        var comps = (current.Components ?? []).ToList();
        var idx = comps.FindIndex(c => string.Equals(c.Id, req.TargetId, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
        {
            return Result<LiveDraftMutationResult>.Failure(WrapperErrors.InvalidPromptDraft($"Component '{req.TargetId}' not found."));
        }

        var removedComp = comps[idx];
        comps.RemoveAt(idx);
        return Result<LiveDraftMutationResult>.Success(new LiveDraftMutationResult(
            current with { Components = comps },
            $"Removed {removedComp.Kind} \"{removedComp.Title}\"",
            removedComp.Id));
    }

    private static Result<LiveDraftMutationResult> ApplyMoveComponent(PromptDraft current, LiveDraftMutationRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.TargetId) || !req.Index.HasValue)
        {
            return Result<LiveDraftMutationResult>.Failure(WrapperErrors.InvalidPromptDraft("TargetId and Index are required for move-comp."));
        }

        var comps = (current.Components ?? []).ToList();
        var idx = comps.FindIndex(c => string.Equals(c.Id, req.TargetId, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
        {
            return Result<LiveDraftMutationResult>.Failure(WrapperErrors.InvalidPromptDraft($"Component '{req.TargetId}' not found."));
        }

        var targetIdx = Math.Clamp(req.Index.Value, 0, Math.Max(0, comps.Count - 1));
        var item = comps[idx];
        comps.RemoveAt(idx);
        comps.Insert(targetIdx, item);
        return Result<LiveDraftMutationResult>.Success(new LiveDraftMutationResult(
            current with { Components = comps },
            $"Moved \"{item.Title}\" to position {targetIdx + 1}",
            item.Id));
    }

    private static Result<LiveDraftMutationResult> ApplyAddSpec(PromptDraft current, LiveDraftMutationRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.TargetId) || string.IsNullOrWhiteSpace(req.Topic))
        {
            return Result<LiveDraftMutationResult>.Failure(WrapperErrors.InvalidPromptDraft("Component TargetId and Topic are required for add-spec."));
        }

        var comps = (current.Components ?? []).ToList();
        var compIdx = comps.FindIndex(c => string.Equals(c.Id, req.TargetId, StringComparison.OrdinalIgnoreCase));
        if (compIdx < 0)
        {
            return Result<LiveDraftMutationResult>.Failure(WrapperErrors.InvalidPromptDraft($"Component '{req.TargetId}' not found."));
        }

        var comp = EnsureOrderedItems(comps[compIdx]);
        var ordered = (comp.OrderedItems ?? []).ToList();
        var qty = Math.Clamp(req.Count ?? 1, 1, 50);
        var topic = req.Topic.Trim().ToLowerInvariant();
        string? lastSpecId = null;

        for (var i = 0; i < qty; i++)
        {
            lastSpecId = $"{comp.Id}_{topic}_{Guid.NewGuid().ToString("N")[..6]}";
            var content = req.ContentMode is not null && req.ContentMode != SpecContentMode.None
                ? new SpecContentSource(req.ContentMode.Value, req.RawText, req.MarkdownFilePath, req.ImagePath, req.Caption, req.AltText)
                : null;
            ordered.Add(new OrderedSpecInstance(lastSpecId, topic, 1, string.IsNullOrWhiteSpace(req.Subtype) ? null : req.Subtype.Trim(), content));
        }

        var updatedComp = SyncSelectionsFromOrderedItems(comp with { OrderedItems = ordered }, topic, req.Notes);
        comps[compIdx] = updatedComp;
        var specWord = qty == 1 ? $"{topic} spec" : $"{qty} {topic} specs";
        return Result<LiveDraftMutationResult>.Success(new LiveDraftMutationResult(
            current with { Components = comps },
            $"Added {specWord} to \"{comp.Title}\"",
            comp.Id,
            lastSpecId));
    }

    private static Result<LiveDraftMutationResult> ApplyUpdateSpec(PromptDraft current, LiveDraftMutationRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.TargetId))
        {
            return Result<LiveDraftMutationResult>.Failure(WrapperErrors.InvalidPromptDraft("Target spec ID is required for update-spec."));
        }

        var comps = (current.Components ?? []).ToList();
        for (var c = 0; c < comps.Count; c++)
        {
            var comp = EnsureOrderedItems(comps[c]);
            var ordered = (comp.OrderedItems ?? []).ToList();
            var sIdx = ordered.FindIndex(s => string.Equals(s.Id, req.TargetId, StringComparison.OrdinalIgnoreCase));
            if (sIdx < 0)
            {
                continue;
            }

            var existing = ordered[sIdx];
            var nextSubtype = req.Subtype is not null
                ? (string.IsNullOrWhiteSpace(req.Subtype) ? null : req.Subtype.Trim())
                : existing.Subtype;
            ordered[sIdx] = existing with { Subtype = nextSubtype };
            comps[c] = SyncSelectionsFromOrderedItems(comp with { OrderedItems = ordered }, existing.Topic, req.Notes, req.Modes);
            var updateDetail = req.Subtype is not null
                ? (string.IsNullOrWhiteSpace(req.Subtype) ? "cleared subtype" : $"subtype -> {req.Subtype.Trim()}")
                : (req.Notes is not null ? "updated notes" : "updated options");
            return Result<LiveDraftMutationResult>.Success(new LiveDraftMutationResult(
                current with { Components = comps },
                $"Updated {existing.Topic} ({updateDetail}) in \"{comp.Title}\"",
                comp.Id,
                existing.Id));
        }

        return Result<LiveDraftMutationResult>.Failure(WrapperErrors.InvalidPromptDraft($"Spec '{req.TargetId}' not found."));
    }

    private static Result<LiveDraftMutationResult> ApplyRemoveSpec(PromptDraft current, LiveDraftMutationRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.TargetId))
        {
            return Result<LiveDraftMutationResult>.Failure(WrapperErrors.InvalidPromptDraft("Target spec ID is required."));
        }

        var comps = (current.Components ?? []).ToList();
        for (var c = 0; c < comps.Count; c++)
        {
            var comp = EnsureOrderedItems(comps[c]);
            var ordered = (comp.OrderedItems ?? []).ToList();
            var sIdx = ordered.FindIndex(s => string.Equals(s.Id, req.TargetId, StringComparison.OrdinalIgnoreCase));
            if (sIdx < 0)
            {
                continue;
            }

            var removed = ordered[sIdx];
            ordered.RemoveAt(sIdx);
            comps[c] = SyncSelectionsFromOrderedItems(comp with { OrderedItems = ordered }, removed.Topic, null);
            return Result<LiveDraftMutationResult>.Success(new LiveDraftMutationResult(
                current with { Components = comps },
                $"Removed {removed.Topic} from {comp.Title}",
                comp.Id,
                removed.Id));
        }

        return Result<LiveDraftMutationResult>.Failure(WrapperErrors.InvalidPromptDraft($"Spec '{req.TargetId}' not found."));
    }

    private static Result<LiveDraftMutationResult> ApplyMoveSpec(PromptDraft current, LiveDraftMutationRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.TargetId) || !req.Index.HasValue)
        {
            return Result<LiveDraftMutationResult>.Failure(WrapperErrors.InvalidPromptDraft("TargetId and Index are required for move-spec."));
        }

        var comps = (current.Components ?? []).ToList();
        for (var c = 0; c < comps.Count; c++)
        {
            var comp = EnsureOrderedItems(comps[c]);
            var ordered = (comp.OrderedItems ?? []).ToList();
            var sIdx = ordered.FindIndex(s => string.Equals(s.Id, req.TargetId, StringComparison.OrdinalIgnoreCase));
            if (sIdx < 0)
            {
                continue;
            }

            var targetIdx = Math.Clamp(req.Index.Value, 0, Math.Max(0, ordered.Count - 1));
            var item = ordered[sIdx];
            ordered.RemoveAt(sIdx);
            ordered.Insert(targetIdx, item);
            comps[c] = SyncSelectionsFromOrderedItems(comp with { OrderedItems = ordered }, item.Topic, null);
            return Result<LiveDraftMutationResult>.Success(new LiveDraftMutationResult(
                current with { Components = comps },
                $"Moved {item.Topic} to position {targetIdx + 1}",
                comp.Id,
                item.Id));
        }

        return Result<LiveDraftMutationResult>.Failure(WrapperErrors.InvalidPromptDraft($"Spec '{req.TargetId}' not found."));
    }

    private static Result<LiveDraftMutationResult> ApplyCloneSpec(PromptDraft current, LiveDraftMutationRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.TargetId))
        {
            return Result<LiveDraftMutationResult>.Failure(WrapperErrors.InvalidPromptDraft("Target spec ID is required for clone-spec."));
        }

        var comps = (current.Components ?? []).ToList();
        for (var c = 0; c < comps.Count; c++)
        {
            var comp = EnsureOrderedItems(comps[c]);
            var ordered = (comp.OrderedItems ?? []).ToList();
            var sIdx = ordered.FindIndex(s => string.Equals(s.Id, req.TargetId, StringComparison.OrdinalIgnoreCase));
            if (sIdx < 0)
            {
                continue;
            }

            var orig = ordered[sIdx];
            var cloneId = $"{comp.Id}_{orig.Topic}_{Guid.NewGuid().ToString("N")[..6]}";
            var clonedContent = orig.Content is null ? null : orig.Content with { };
            var clone = orig with { Id = cloneId, Content = clonedContent };
            ordered.Insert(sIdx + 1, clone);
            comps[c] = SyncSelectionsFromOrderedItems(comp with { OrderedItems = ordered }, orig.Topic, null);
            return Result<LiveDraftMutationResult>.Success(new LiveDraftMutationResult(
                current with { Components = comps },
                $"Cloned {orig.Topic} in \"{comp.Title}\"",
                comp.Id,
                cloneId));
        }

        return Result<LiveDraftMutationResult>.Failure(WrapperErrors.InvalidPromptDraft($"Spec '{req.TargetId}' not found."));
    }

    private static Result<LiveDraftMutationResult> ApplyMoveSpecTo(PromptDraft current, LiveDraftMutationRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.TargetId) || string.IsNullOrWhiteSpace(req.TargetContainerId))
        {
            return Result<LiveDraftMutationResult>.Failure(WrapperErrors.InvalidPromptDraft("TargetId and TargetContainerId are required for move-spec-to."));
        }

        var comps = (current.Components ?? []).ToList();
        var dstIdx = comps.FindIndex(c => string.Equals(c.Id, req.TargetContainerId, StringComparison.OrdinalIgnoreCase));
        if (dstIdx < 0)
        {
            return Result<LiveDraftMutationResult>.Failure(WrapperErrors.InvalidPromptDraft($"Target component '{req.TargetContainerId}' not found."));
        }

        var srcIdx = -1;
        var sIdx = -1;
        for (var c = 0; c < comps.Count; c++)
        {
            var comp = EnsureOrderedItems(comps[c]);
            comps[c] = comp;
            var idx = (comp.OrderedItems ?? []).ToList().FindIndex(s => string.Equals(s.Id, req.TargetId, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                srcIdx = c;
                sIdx = idx;
                break;
            }
        }

        if (srcIdx < 0)
        {
            return Result<LiveDraftMutationResult>.Failure(WrapperErrors.InvalidPromptDraft($"Spec '{req.TargetId}' not found."));
        }

        if (srcIdx == dstIdx)
        {
            return ApplyMoveSpec(current with { Components = comps }, req with { Index = req.Index ?? ((comps[dstIdx].OrderedItems?.Count ?? 1) - 1) });
        }

        var srcComp = comps[srcIdx];
        var dstComp = EnsureOrderedItems(comps[dstIdx]);
        if (srcComp.Kind != dstComp.Kind)
        {
            return Result<LiveDraftMutationResult>.Failure(WrapperErrors.InvalidPromptDraft(
                $"Cannot move {srcComp.Kind} spec into {dstComp.Kind} component \"{dstComp.Title}\"."));
        }

        var srcOrdered = (srcComp.OrderedItems ?? []).ToList();
        var item = srcOrdered[sIdx];
        srcOrdered.RemoveAt(sIdx);

        var dstOrdered = (dstComp.OrderedItems ?? []).ToList();
        var targetPos = Math.Clamp(req.Index ?? dstOrdered.Count, 0, dstOrdered.Count);
        dstOrdered.Insert(targetPos, item);

        comps[srcIdx] = SyncSelectionsFromOrderedItems(srcComp with { OrderedItems = srcOrdered }, item.Topic, null);
        comps[dstIdx] = SyncSelectionsFromOrderedItems(dstComp with { OrderedItems = dstOrdered }, item.Topic, null);

        return Result<LiveDraftMutationResult>.Success(new LiveDraftMutationResult(
            current with { Components = comps },
            $"Moved {item.Topic} to \"{dstComp.Title}\" at position {targetPos + 1}",
            dstComp.Id,
            item.Id));
    }

    private static Result<LiveDraftMutationResult> ApplySetContent(string fullWs, PromptDraft current, LiveDraftMutationRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.TargetId))
        {
            return Result<LiveDraftMutationResult>.Failure(WrapperErrors.InvalidPromptDraft("Target spec ID is required for set-content."));
        }

        var mode = req.ContentMode ?? (!string.IsNullOrWhiteSpace(req.ImagePath)
            ? SpecContentMode.ImageFile
            : (!string.IsNullOrWhiteSpace(req.MarkdownFilePath)
                ? SpecContentMode.MarkdownFile
                : (!string.IsNullOrWhiteSpace(req.RawText) ? SpecContentMode.RawText : SpecContentMode.None)));

        string? normalizedRelMd = null;
        string? normalizedRelImg = null;
        if (mode == SpecContentMode.MarkdownFile)
        {
            var relRes = ValidateAndNormalizeWorkspaceFilePath(fullWs, req.MarkdownFilePath, isImage: false);
            if (relRes.IsFailure)
            {
                return Result<LiveDraftMutationResult>.Failure(relRes.Error!);
            }

            normalizedRelMd = relRes.Value;
        }
        else if (mode == SpecContentMode.ImageFile)
        {
            var relRes = ValidateAndNormalizeWorkspaceFilePath(fullWs, req.ImagePath, isImage: true);
            if (relRes.IsFailure)
            {
                return Result<LiveDraftMutationResult>.Failure(relRes.Error!);
            }

            normalizedRelImg = relRes.Value;
        }

        var contentSource = mode switch
        {
            SpecContentMode.RawText => new SpecContentSource(SpecContentMode.RawText, req.RawText ?? string.Empty, null),
            SpecContentMode.MarkdownFile => new SpecContentSource(SpecContentMode.MarkdownFile, null, normalizedRelMd),
            SpecContentMode.ImageFile => new SpecContentSource(
                SpecContentMode.ImageFile,
                string.IsNullOrWhiteSpace(req.RawText) ? null : req.RawText,
                null,
                normalizedRelImg,
                string.IsNullOrWhiteSpace(req.Caption) ? null : req.Caption.Trim(),
                string.IsNullOrWhiteSpace(req.AltText) ? null : req.AltText.Trim()),
            _ => new SpecContentSource(SpecContentMode.None, null, null)
        };

        var comps = (current.Components ?? []).ToList();
        for (var c = 0; c < comps.Count; c++)
        {
            var comp = EnsureOrderedItems(comps[c]);
            var ordered = (comp.OrderedItems ?? []).ToList();
            var sIdx = ordered.FindIndex(s => string.Equals(s.Id, req.TargetId, StringComparison.OrdinalIgnoreCase));
            if (sIdx < 0)
            {
                continue;
            }

            ordered[sIdx] = ordered[sIdx] with { Content = contentSource };
            comps[c] = SyncSelectionsFromOrderedItems(comp with { OrderedItems = ordered }, ordered[sIdx].Topic, req.Notes);
            var contentSummary = mode switch
            {
                SpecContentMode.RawText => $"Filled {ordered[sIdx].Topic} text in \"{comp.Title}\"",
                SpecContentMode.MarkdownFile => $"Attached {Path.GetFileName(normalizedRelMd)} to {ordered[sIdx].Topic}",
                SpecContentMode.ImageFile => $"Attached {Path.GetFileName(normalizedRelImg)} to {ordered[sIdx].Topic}",
                _ => $"Cleared {ordered[sIdx].Topic} content"
            };
            return Result<LiveDraftMutationResult>.Success(new LiveDraftMutationResult(
                current with { Components = comps },
                contentSummary,
                comp.Id,
                ordered[sIdx].Id));
        }

        return Result<LiveDraftMutationResult>.Failure(WrapperErrors.InvalidPromptDraft($"Spec '{req.TargetId}' not found."));
    }

    private static Result<string> ValidateAndNormalizeWorkspaceFilePath(string fullWs, string? rawPath, bool isImage)
    {
        var label = isImage ? "Image" : "Markdown";
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return Result<string>.Failure(WrapperErrors.InvalidPath($"{label} file path cannot be empty."));
        }

        try
        {
            var trimmed = rawPath.Trim().Replace('/', Path.DirectorySeparatorChar);
            var candidate = Path.IsPathRooted(trimmed)
                ? Path.GetFullPath(trimmed)
                : Path.GetFullPath(Path.Combine(fullWs, trimmed));
            var fullRoot = Path.TrimEndingDirectorySeparator(fullWs) + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                return Result<string>.Failure(WrapperErrors.InvalidPath($"{label} file must be inside the Active Workspace."));
            }

            var ext = Path.GetExtension(candidate);
            var allowed = isImage ? ImportWorkspaceAssetHandler.ImageExtensions : ImportWorkspaceAssetHandler.MarkdownExtensions;
            if (!allowed.Contains(ext))
            {
                return Result<string>.Failure(WrapperErrors.InvalidPath(
                    isImage
                        ? "Attached spec image must be a .png, .jpg, .jpeg, .gif, .webp, .bmp, or .svg file."
                        : "Attached spec file must be a .md or .markdown file."));
            }

            if (!File.Exists(candidate))
            {
                return Result<string>.Failure(WrapperErrors.InvalidPath($"{label} file not found: {rawPath}"));
            }

            return Result<string>.Success(Path.GetRelativePath(fullWs, candidate).Replace('\\', '/'));
        }
        catch (Exception ex)
        {
            return Result<string>.Failure(WrapperErrors.InvalidPath($"Invalid {label} path: {ex.Message}"));
        }
    }

    private static PromptDraft NormalizeDraft(PromptDraft draft, string fullWs)
    {
        var comps = (draft.Components ?? [])
            .Select(comp => SyncSelectionsFromOrderedItems(EnsureOrderedItems(comp), null, null))
            .ToList();

        return draft with
        {
            WorkingDirectory = fullWs,
            Components = comps,
            Selections = comps.SelectMany(c => c.Selections.Where(s => s.Enabled)).ToList()
        };
    }

    private static PromptComponentPlan EnsureOrderedItems(PromptComponentPlan comp)
    {
        if (comp.OrderedItems is { Count: > 0 })
        {
            return comp;
        }

        if (comp.OrderedItems is not null && !comp.Selections.Any(s => s.Enabled))
        {
            return comp;
        }

        return comp with { OrderedItems = SpecOrderExpander.Expand(comp, useGlobalIndexInId: true) };
    }

    private static PromptComponentPlan SyncSelectionsFromOrderedItems(
        PromptComponentPlan comp,
        string? touchTopic,
        string? updatedNotes,
        IReadOnlyList<string>? updatedModes = null)
    {
        var ordered = (comp.OrderedItems ?? []).ToList();
        var perTopicCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var renumbered = new List<OrderedSpecInstance>(ordered.Count);

        foreach (var inst in ordered)
        {
            var nextNum = perTopicCounts.GetValueOrDefault(inst.Topic) + 1;
            perTopicCounts[inst.Topic] = nextNum;
            renumbered.Add(inst with { InstanceNumber = nextNum });
        }

        var existingSelections = comp.Selections.ToDictionary(s => s.Topic, StringComparer.OrdinalIgnoreCase);
        var mergedSelections = new List<ShoppingListSelection>();

        foreach (var (topic, count) in perTopicCounts)
        {
            existingSelections.TryGetValue(topic, out var prev);
            var notes = !string.IsNullOrWhiteSpace(touchTopic) &&
                        string.Equals(topic, touchTopic, StringComparison.OrdinalIgnoreCase) &&
                        updatedNotes is not null
                ? updatedNotes
                : prev?.Notes;

            var subtypeCounts = renumbered
                .Where(o => string.Equals(o.Topic, topic, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(o.Subtype))
                .GroupBy(o => o.Subtype!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            var isTouched = !string.IsNullOrWhiteSpace(touchTopic) &&
                            string.Equals(topic, touchTopic, StringComparison.OrdinalIgnoreCase);
            var modes = isTouched && updatedModes is not null
                ? updatedModes.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : prev?.Modes;

            mergedSelections.Add(new ShoppingListSelection(
                Topic: topic,
                Enabled: true,
                DesiredCount: count,
                Notes: notes,
                Modes: modes,
                ModeCounts: subtypeCounts.Count > 0 ? subtypeCounts : null));
        }

        return comp with
        {
            OrderedItems = renumbered,
            Selections = mergedSelections
        };
    }

    private static string FormatContainerLabel(ContainerKind container) => container switch
    {
        ContainerKind.Session => "Session",
        ContainerKind.StandaloneArticle => "Standalone Article",
        ContainerKind.StandaloneQuiz => "Standalone Quiz",
        _ => container.ToString()
    };

    private static string FormatWorkflowModeLabel(WorkflowMode mode) => mode switch
    {
        WorkflowMode.Direct => "Build Directly",
        WorkflowMode.LiveAgent => "Live Agent",
        WorkflowMode.ClassicPrompt => "Classic Prompt",
        _ => mode.ToString()
    };
}
