using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;
using Meloberry.Wrapper.Domain.Services;

namespace Meloberry.Wrapper.Application.UseCases;

/// <summary>Result returned after generating a raw template JSON and creating a draft workspace via `mby apply`.</summary>
public sealed record GenerateTemplateResult(
    string JsonPath,
    string WorkspaceId,
    string RawJson);

/// <summary>Builds strict `mby` JSON session/article/quiz scaffolds and optionally ingests them via `mby apply`.</summary>
public sealed class SessionTemplateHandler(
    LoadSpecCatalogHandler specLoader,
    ManagedAppsHandler managedApps,
    IMbyCliClient mbyClient,
    ISettingsStore settingsStore)
{
    // Valid 1x1 transparent PNG bytes so draft `image` / `relabel` templates pass asset confinement cleanly.
    private static readonly byte[] PlaceholderPngBytes =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
        0x42, 0x60, 0x82
    ];

    public async Task<Result<SessionScaffoldOutput>> BuildRawJsonAsync(PromptDraft draft, CancellationToken ct = default)
    {
        var (specs, _, _) = await specLoader.LoadAsync(false, ct).ConfigureAwait(false);
        return SessionScaffoldGenerator.Generate(draft, specs);
    }

    public async Task<Result<GenerateTemplateResult>> GenerateDraftTemplateAsync(PromptDraft draft, CancellationToken ct = default)
    {
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var wsPath = !string.IsNullOrWhiteSpace(draft.WorkingDirectory)
            ? draft.WorkingDirectory!
            : settings.ActiveWorkspace?.WorkspacePath;

        if (string.IsNullOrWhiteSpace(wsPath))
        {
            return Result<GenerateTemplateResult>.Failure(
                WrapperErrors.InvalidWorkspaceDir("Select an Active Workspace before generating a draft template."));
        }

        var fullWs = Path.GetFullPath(wsPath);
        Directory.CreateDirectory(fullWs);

        var mbyPath = await managedApps.ResolveExecutableAsync("mby", fullWs, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(mbyPath))
        {
            return Result<GenerateTemplateResult>.Failure(WrapperErrors.CliNotFound("mby"));
        }

        var scaffoldRes = await BuildRawJsonAsync(draft, ct).ConfigureAwait(false);
        if (scaffoldRes.IsFailure)
        {
            return Result<GenerateTemplateResult>.Failure(scaffoldRes.Error!);
        }

        var scaffold = scaffoldRes.Value;
        if (scaffold.RequiresPlaceholderImage)
        {
            var assetsDir = Path.Combine(fullWs, "assets");
            Directory.CreateDirectory(assetsDir);
            var placeholderPath = Path.Combine(assetsDir, "placeholder.png");
            if (!File.Exists(placeholderPath))
            {
                await File.WriteAllBytesAsync(placeholderPath, PlaceholderPngBytes, ct).ConfigureAwait(false);
            }
        }

        var jsonPath = Path.Combine(fullWs, scaffold.SuggestedFileName);
        var tmpJson = jsonPath + ".tmp";
        await File.WriteAllTextAsync(tmpJson, scaffold.RawJson, ct).ConfigureAwait(false);
        File.Move(tmpJson, jsonPath, overwrite: true);

        var applyRes = await mbyClient.ApplySpecJsonAsync(mbyPath, jsonPath, fullWs, ct).ConfigureAwait(false);
        if (applyRes.IsFailure)
        {
            return Result<GenerateTemplateResult>.Failure(applyRes.Error!);
        }

        return Result<GenerateTemplateResult>.Success(new GenerateTemplateResult(
            JsonPath: jsonPath,
            WorkspaceId: applyRes.Value,
            RawJson: scaffold.RawJson));
    }
}
