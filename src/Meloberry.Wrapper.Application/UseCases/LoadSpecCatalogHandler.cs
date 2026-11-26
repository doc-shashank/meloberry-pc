using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Application.UseCases;

/// <summary>Loads &amp; caches live `mby spec` topics and appends virtual block helpers.</summary>
public sealed class LoadSpecCatalogHandler(
    IManagedAppsStore appsStore,
    IMbyCliClient mbyClient,
    ISettingsStore settingsStore)
{
    private sealed record SpecCacheEntry(string ExePath, DateTime LastWriteTimeUtc, IReadOnlyList<SpecTopicItem> Catalog);

    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private volatile SpecCacheEntry? _cache;

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

        var mtime = File.GetLastWriteTimeUtc(mbyPath);
        var existing = _cache;
        if (!forceRefresh &&
            existing is not null &&
            existing.LastWriteTimeUtc == mtime &&
            string.Equals(existing.ExePath, mbyPath, StringComparison.OrdinalIgnoreCase))
        {
            return (existing.Catalog, true, mbyPath);
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
                return (existing.Catalog, true, mbyPath);
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

            IReadOnlyList<SpecTopicItem> snapshot = merged.AsReadOnly();
            _cache = new SpecCacheEntry(mbyPath, mtime, snapshot);
            return (snapshot, true, mbyPath);
        }
        finally
        {
            _cacheLock.Release();
        }
    }
}
