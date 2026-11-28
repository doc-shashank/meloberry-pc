using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Application.UseCases;

/// <summary>
/// Delegates modular Markdown + asset scaffolding to `mby scaffold` and runs transactional `mby batch -`
/// (`apply` -> `check` -> `build --all-themes`) before mounting the WebApp into Step 5 Viewer.
/// </summary>
public sealed class FinalizeWorkspaceHandler(
    IManagedAppsStore appsStore,
    IWorkspaceDraftStore draftStore,
    IMbyCliClient mbyClient,
    ManageLibraryHandler manageLibrary,
    ISettingsStore settingsStore)
{
    private static readonly JsonSerializerOptions BlueprintJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<Result<FinalizeResult>> FinalizeAsync(
        FinalizeRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var wsCandidate = !string.IsNullOrWhiteSpace(request.WorkspacePath)
            ? request.WorkspacePath
            : settings.ActiveWorkspace?.WorkspacePath;
        if (!PathNormalization.TryNormalizeDirectoryPath(wsCandidate, out var fullWs))
        {
            return Result<FinalizeResult>.Failure(WrapperErrors.InvalidWorkspaceDir(wsCandidate ?? string.Empty));
        }

        var mbyExe = await appsStore.ResolveToolExecutableAsync(
            "mby",
            fullWs,
            settings.MbyExecutableOverride,
            ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(mbyExe))
        {
            return Result<FinalizeResult>.Failure(WrapperErrors.CliNotFound("mby"));
        }

        var verRes = await mbyClient.GetVersionInfoAsync(mbyExe, ct).ConfigureAwait(false);
        if (verRes.IsFailure)
        {
            return Result<FinalizeResult>.Failure(WrapperErrors.CliIncompatible(
                "unknown",
                MbyCliCompatibility.MinimumDisplayVersion,
                verRes.Error!.Description));
        }

        if (!verRes.Value.IsCompatible)
        {
            return Result<FinalizeResult>.Failure(WrapperErrors.CliIncompatible(
                verRes.Value.Version,
                MbyCliCompatibility.MinimumDisplayVersion,
                verRes.Value.IncompatibilityReason ?? string.Empty));
        }

        var draft = await draftStore.LoadAsync(fullWs, ct).ConfigureAwait(false);
        var allowPlaceholders = request.AllowPlaceholders || draft.AllowPlaceholdersOnFinalize;

        var pathCheck = ValidateReferencedMarkdownFiles(fullWs, draft);
        if (pathCheck.IsFailure)
        {
            return Result<FinalizeResult>.Failure(pathCheck.Error!);
        }

        var slug = Slugify(draft.Title, "study_module");
        var buildSrcDir = Path.Combine(fullWs, ".meloberry", "build-src", slug);
        try
        {
            if (Directory.Exists(buildSrcDir))
            {
                Directory.Delete(buildSrcDir, recursive: true);
            }
            Directory.CreateDirectory(buildSrcDir);
        }
        catch (Exception ex)
        {
            return Result<FinalizeResult>.Failure(WrapperErrors.IoFailure($"Failed preparing finalize build directory: {ex.Message}"));
        }

        var blueprintJson = JsonSerializer.Serialize(new
        {
            container = draft.Container,
            title = draft.Title,
            description = draft.Description,
            selections = draft.Selections,
            components = draft.Components,
            allowPlaceholdersOnFinalize = allowPlaceholders
        }, BlueprintJsonOptions);

        var scaffoldRes = await mbyClient.ScaffoldAsync(
            mbyExe,
            blueprintJson,
            format: "md",
            outputPath: buildSrcDir,
            apply: false,
            allowPlaceholders,
            workingDir: fullWs,
            ct).ConfigureAwait(false);

        if (scaffoldRes.IsFailure)
        {
            return Result<FinalizeResult>.Failure(scaffoldRes.Error!);
        }

        var effectiveSlug = string.IsNullOrWhiteSpace(scaffoldRes.Value.Slug) ? slug : scaffoldRes.Value.Slug;
        var outDir = ResolveOutputDirectory(fullWs, effectiveSlug, request.OutputDir, settings.DefaultBuildOutputDir);
        var relBuildSrc = Path.GetRelativePath(fullWs, buildSrcDir).Replace('\\', '/');
        var payload = new object[]
        {
            new { op = "apply", file = relBuildSrc },
            new { op = "check", addr = "$0.id" },
            new
            {
                op = "build",
                addr = "$0.id",
                @out = outDir,
                verify = true,
                staticOnly = request.VerifyStaticOnly,
                allThemes = true
            }
        };
        var batchJson = JsonSerializer.Serialize(payload);
        var batchRes = await mbyClient.RunBatchAsync(mbyExe, batchJson, fullWs, ct).ConfigureAwait(false);
        if (batchRes.IsFailure)
        {
            return Result<FinalizeResult>.Failure(batchRes.Error!);
        }

        var effectiveWebDir = !string.IsNullOrWhiteSpace(batchRes.Value.OutputDirectory)
            ? batchRes.Value.OutputDirectory
            : (Path.IsPathRooted(outDir) ? outDir : Path.GetFullPath(outDir, fullWs));

        var openRes = await manageLibrary.OpenWebAppAsync(effectiveWebDir, ct).ConfigureAwait(false);
        if (openRes.IsFailure)
        {
            return Result<FinalizeResult>.Failure(openRes.Error!);
        }

        var (slotId, relViewerUrl, _) = openRes.Value;
        return Result<FinalizeResult>.Success(new FinalizeResult(
            WorkspaceId: batchRes.Value.WorkspaceId ?? string.Empty,
            BuildSourceDirectory: buildSrcDir,
            WebAppDirectory: effectiveWebDir,
            SlotId: slotId,
            ViewerUrl: relViewerUrl,
            PlaceholderSpecsUsed: scaffoldRes.Value.PlaceholderSpecsUsed));
    }

    private static Result ValidateReferencedMarkdownFiles(string fullWs, PromptDraft draft)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(fullWs) + Path.DirectorySeparatorChar;
        foreach (var comp in draft.Components ?? [])
        {
            foreach (var inst in comp.OrderedItems ?? [])
            {
                if (inst.Content?.Mode != SpecContentMode.MarkdownFile ||
                    string.IsNullOrWhiteSpace(inst.Content.MarkdownFilePath))
                {
                    continue;
                }

                var key = inst.Content.MarkdownFilePath.Replace('\\', '/');
                var candidate = Path.GetFullPath(Path.Combine(fullWs, key.Replace('/', Path.DirectorySeparatorChar)));
                if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return Result.Failure(WrapperErrors.InvalidPath($"Markdown file escapes Active Workspace: {key}"));
                }

                if (!File.Exists(candidate))
                {
                    return Result.Failure(WrapperErrors.InvalidPath($"Attached Markdown file not found: {key}"));
                }
            }
        }

        return Result.Success();
    }

    private static string ResolveOutputDirectory(string fullWs, string slug, string? requestedOut, string? defaultBuildDir)
    {
        if (!string.IsNullOrWhiteSpace(requestedOut))
        {
            return Path.IsPathRooted(requestedOut)
                ? Path.GetFullPath(requestedOut)
                : Path.GetFullPath(requestedOut, fullWs);
        }

        if (!string.IsNullOrWhiteSpace(defaultBuildDir))
        {
            return Path.Combine(Path.GetFullPath(defaultBuildDir), $"{slug}_web");
        }

        return $"{slug}_web";
    }

    private static string Slugify(string? raw, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        var sb = new StringBuilder();
        var prevDash = false;
        foreach (var ch in raw.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                prevDash = false;
            }
            else if (!prevDash && sb.Length > 0)
            {
                sb.Append('_');
                prevDash = true;
            }
        }

        var slug = sb.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(slug) ? fallback : (slug.Length > 40 ? slug[..40].Trim('_') : slug);
    }
}
