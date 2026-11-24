using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;
using Meloberry.Wrapper.Domain.Services;

namespace Meloberry.Wrapper.Application.UseCases;

/// <summary>Loads &amp; caches live `mby spec` topics and appends virtual block helpers.</summary>
public sealed class LoadSpecCatalogHandler(
    IManagedAppsStore appsStore,
    IMbyCliClient mbyClient,
    ISettingsStore settingsStore)
{
    private IReadOnlyList<SpecTopicItem>? _cachedLiveCatalog;
    private string? _cachedExePath;

    public async Task<(IReadOnlyList<SpecTopicItem> Specs, bool IsLiveCli, string? ActiveMbyPath)> LoadAsync(
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var mbyPath = await appsStore.ResolveToolExecutableAsync(
            "mby",
            settings.ActiveWorkspace?.WorkspacePath,
            settings.MbyExecutableOverride,
            ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(mbyPath))
        {
            return (SpecCatalogDefaults.BuiltInFallbackCatalog, false, null);
        }

        if (!forceRefresh &&
            _cachedLiveCatalog is not null &&
            string.Equals(_cachedExePath, mbyPath, StringComparison.OrdinalIgnoreCase))
        {
            return (_cachedLiveCatalog, true, mbyPath);
        }

        var result = await mbyClient.GetSpecCatalogAsync(mbyPath, settings.ActiveWorkspace?.WorkspacePath, ct).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return (SpecCatalogDefaults.BuiltInFallbackCatalog, false, mbyPath);
        }

        var merged = result.Value.ToList();
        if (!merged.Any(s => string.Equals(s.Topic, SpecCatalogDefaults.VirtualLinkTopicName, StringComparison.OrdinalIgnoreCase)))
        {
            merged.Add(SpecCatalogDefaults.VirtualLinkTopic);
        }

        _cachedLiveCatalog = merged;
        _cachedExePath = mbyPath;
        return (merged, true, mbyPath);
    }
}

/// <summary>Composes and saves Prompt Studio prompts.</summary>
public sealed class PromptHandlers(LoadSpecCatalogHandler specLoader)
{
    public async Task<ComposedPrompt> ComposeAsync(PromptDraft draft, CancellationToken ct = default)
    {
        var (specs, _, _) = await specLoader.LoadAsync(false, ct).ConfigureAwait(false);
        return PromptComposer.Compose(draft, specs);
    }

    public async Task<Result<string>> SavePromptAsync(string filePath, string markdown, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return Result<string>.Failure(WrapperErrors.InvalidPath("Save file path cannot be empty."));
        }

        try
        {
            var full = Path.GetFullPath(filePath);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tmp = full + ".tmp";
            await File.WriteAllTextAsync(tmp, markdown, ct).ConfigureAwait(false);
            File.Move(tmp, full, overwrite: true);
            return Result<string>.Success(full);
        }
        catch (Exception ex)
        {
            return Result<string>.Failure(WrapperErrors.IoFailure($"Failed to save prompt: {ex.Message}"));
        }
    }
}
