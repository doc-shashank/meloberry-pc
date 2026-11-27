using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Application.UseCases;

/// <summary>Loads &amp; caches live `mby spec` topics and appends virtual block helpers.</summary>
public sealed class LoadSpecCatalogHandler(
    IManagedAppsStore appsStore,
    IMbyCliClient mbyClient,
    ISettingsStore settingsStore)
{
    private sealed record SpecCacheEntry(
        string ExePath,
        DateTime LastWriteTimeUtc,
        IReadOnlyList<SpecTopicItem> Catalog,
        IReadOnlyDictionary<string, string> SpecIcons);

    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private volatile SpecCacheEntry? _cache;

    public async Task<(IReadOnlyList<SpecTopicItem> Specs, bool IsLiveCli, string? ActiveMbyPath)> LoadAsync(
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        var (specs, _, isLiveCli, activeMbyPath) = await LoadWithIconsAsync(forceRefresh, ct).ConfigureAwait(false);
        return (specs, isLiveCli, activeMbyPath);
    }

    public async Task<(IReadOnlyList<SpecTopicItem> Specs, IReadOnlyDictionary<string, string> SpecIcons, bool IsLiveCli, string? ActiveMbyPath)> LoadWithIconsAsync(
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
            return (SpecCatalogDefaults.BuiltInFallbackCatalog, new Dictionary<string, string>(), false, null);
        }

        var mtime = File.GetLastWriteTimeUtc(mbyPath);
        var existing = _cache;
        if (!forceRefresh &&
            existing is not null &&
            existing.LastWriteTimeUtc == mtime &&
            string.Equals(existing.ExePath, mbyPath, StringComparison.OrdinalIgnoreCase))
        {
            return (existing.Catalog, existing.SpecIcons, true, mbyPath);
        }

        await _cacheLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            mtime = File.GetLastWriteTimeUtc(mbyPath);
            existing = _cache;
            if (!forceRefresh &&
                existing is not null &&
                existing.LastWriteTimeUtc == mtime &&
                string.Equals(existing.ExePath, mbyPath, StringComparison.OrdinalIgnoreCase))
            {
                return (existing.Catalog, existing.SpecIcons, true, mbyPath);
            }

            var result = await mbyClient.GetSpecCatalogWithIconsAsync(mbyPath, settings.ActiveWorkspace?.WorkspacePath, ct).ConfigureAwait(false);
            if (result.IsFailure)
            {
                return (SpecCatalogDefaults.BuiltInFallbackCatalog, new Dictionary<string, string>(), false, mbyPath);
            }

            var merged = result.Value.Specs.ToList();
            if (!merged.Any(s => string.Equals(s.Topic, SpecCatalogDefaults.VirtualLinkTopicName, StringComparison.OrdinalIgnoreCase)))
            {
                merged.Add(SpecCatalogDefaults.VirtualLinkTopic);
            }

            IReadOnlyList<SpecTopicItem> snapshot = merged.AsReadOnly();
            var icons = result.Value.SpecIcons;
            _cache = new SpecCacheEntry(mbyPath, mtime, snapshot, icons);
            return (snapshot, icons, true, mbyPath);
        }
        finally
        {
            _cacheLock.Release();
        }
    }
}
