using System.Text.Json;
using System.Text.Json.Serialization;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Application.UseCases;

/// <summary>Result returned after generating a raw template JSON and creating a draft workspace via `mby scaffold --apply`.</summary>
public sealed record GenerateTemplateResult(
    string JsonPath,
    string WorkspaceId,
    string RawJson,
    IReadOnlyList<string>? Warnings = null);

/// <summary>Delegates strict `mby` JSON scaffold generation and draft template materialization to `mby scaffold`.</summary>
public sealed class SessionTemplateHandler(
    ManagedAppsHandler managedApps,
    IMbyCliClient mbyClient,
    ISettingsStore settingsStore)
{
    private static readonly JsonSerializerOptions BlueprintJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<Result<SessionScaffoldOutput>> BuildRawJsonAsync(PromptDraft draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var wsPath = !string.IsNullOrWhiteSpace(draft.WorkingDirectory)
            ? draft.WorkingDirectory
            : settings.ActiveWorkspace?.WorkspacePath;

        var mbyRes = await managedApps.EnsureCompatibleMbyAsync(wsPath, ct).ConfigureAwait(false);
        if (mbyRes.IsFailure)
        {
            return Result<SessionScaffoldOutput>.Failure(mbyRes.Error!);
        }

        var qtyError = ValidateSelectionsQty(draft);
        if (qtyError is not null)
        {
            return Result<SessionScaffoldOutput>.Failure(qtyError);
        }

        var blueprintJson = SerializeBlueprint(draft);
        var scaffoldRes = await mbyClient.ScaffoldAsync(
            mbyRes.Value,
            blueprintJson,
            format: "json",
            outputPath: null,
            apply: false,
            allowPlaceholders: true,
            workingDir: wsPath,
            ct).ConfigureAwait(false);

        if (scaffoldRes.IsFailure)
        {
            return Result<SessionScaffoldOutput>.Failure(scaffoldRes.Error!);
        }

        var val = scaffoldRes.Value;
        var suggestedName = $"{(string.IsNullOrWhiteSpace(val.Slug) ? "session" : val.Slug)}.json";
        return Result<SessionScaffoldOutput>.Success(new SessionScaffoldOutput(
            RawJson: val.RawJson ?? "{}",
            RequiresPlaceholderImage: val.RequiresPlaceholderImage,
            SuggestedFileName: suggestedName,
            Warnings: val.Warnings.Count > 0 ? val.Warnings : null));
    }

    public async Task<Result<GenerateTemplateResult>> GenerateDraftTemplateAsync(PromptDraft draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var wsPath = !string.IsNullOrWhiteSpace(draft.WorkingDirectory)
            ? draft.WorkingDirectory!
            : settings.ActiveWorkspace?.WorkspacePath;

        if (!PathNormalization.TryNormalizeDirectoryPath(wsPath, out var fullWs))
        {
            return Result<GenerateTemplateResult>.Failure(
                WrapperErrors.InvalidWorkspaceDir("Select an Active Workspace before generating a draft template."));
        }

        var mbyRes = await managedApps.EnsureCompatibleMbyAsync(fullWs, ct).ConfigureAwait(false);
        if (mbyRes.IsFailure)
        {
            return Result<GenerateTemplateResult>.Failure(mbyRes.Error!);
        }

        var qtyError = ValidateSelectionsQty(draft);
        if (qtyError is not null)
        {
            return Result<GenerateTemplateResult>.Failure(qtyError);
        }

        try
        {
            Directory.CreateDirectory(fullWs);
        }
        catch (Exception ex)
        {
            return Result<GenerateTemplateResult>.Failure(WrapperErrors.IoFailure($"Failed to prepare workspace directory: {ex.Message}"));
        }

        var blueprintJson = SerializeBlueprint(draft);
        var scaffoldRes = await mbyClient.ScaffoldAsync(
            mbyRes.Value,
            blueprintJson,
            format: "json",
            outputPath: fullWs + Path.DirectorySeparatorChar,
            apply: true,
            allowPlaceholders: true,
            workingDir: fullWs,
            ct).ConfigureAwait(false);

        if (scaffoldRes.IsFailure)
        {
            return Result<GenerateTemplateResult>.Failure(scaffoldRes.Error!);
        }

        var val = scaffoldRes.Value;
        var jsonPath = !string.IsNullOrWhiteSpace(val.WrittenPath)
            ? val.WrittenPath!
            : Path.Combine(fullWs, $"{(string.IsNullOrWhiteSpace(val.Slug) ? "session" : val.Slug)}.json");

        return Result<GenerateTemplateResult>.Success(new GenerateTemplateResult(
            JsonPath: jsonPath,
            WorkspaceId: val.AppliedTargetId ?? string.Empty,
            RawJson: val.RawJson ?? "{}",
            Warnings: val.Warnings.Count > 0 ? val.Warnings : null));
    }

    private static string SerializeBlueprint(PromptDraft draft) =>
        JsonSerializer.Serialize(new
        {
            container = draft.Container,
            title = draft.Title,
            description = draft.Description,
            selections = draft.Selections,
            components = draft.Components,
            allowPlaceholdersOnFinalize = true
        }, BlueprintJsonOptions);

    private static Error? ValidateSelectionsQty(PromptDraft draft)
    {
        if (draft.Components is { Count: > 0 })
        {
            foreach (var comp in draft.Components)
            {
                if (draft.Container == ContainerKind.StandaloneArticle && comp.Kind != ComponentKind.Article) continue;
                if (draft.Container == ContainerKind.StandaloneQuiz && comp.Kind != ComponentKind.Quiz) continue;

                var missingQty = comp.Selections.FirstOrDefault(s => s.Enabled && (s.DesiredCount is null or <= 0));
                if (missingQty is not null)
                {
                    return WrapperErrors.InvalidPromptDraft(
                        $"Spec '{missingQty.Topic}' in '{comp.Title}' has an empty Qty. Fill every active Qty before generating raw session data.");
                }
            }
        }
        else
        {
            var missingQty = draft.Selections.FirstOrDefault(s => s.Enabled && (s.DesiredCount is null or <= 0));
            if (missingQty is not null)
            {
                return WrapperErrors.InvalidPromptDraft(
                    $"Spec '{missingQty.Topic}' has an empty Qty. Fill every active Qty before generating raw session data.");
            }
        }

        return null;
    }
}
