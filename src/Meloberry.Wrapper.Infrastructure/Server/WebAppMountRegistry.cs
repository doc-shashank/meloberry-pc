using System.Collections.Concurrent;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;

namespace Meloberry.Wrapper.Infrastructure.Server;

/// <summary>Tracks mounted WebApp directories under /viewer/&lt;slotId&gt;/ routes.</summary>
public sealed class WebAppMountRegistry : IWebAppMountRegistry
{
    private readonly ConcurrentDictionary<string, string> _slots = new(StringComparer.OrdinalIgnoreCase);
    private string? _activeSlotId;

    public string? ActiveSlotId => _activeSlotId;

    public Result<string> Mount(string webAppDir)
    {
        if (!PathNormalization.TryNormalizeDirectoryPath(webAppDir, out var full) || !Directory.Exists(full))
        {
            return Result<string>.Failure(WrapperErrors.InvalidWebAppDir(webAppDir));
        }

        foreach (var kv in _slots)
        {
            if (string.Equals(kv.Value, full, StringComparison.OrdinalIgnoreCase))
            {
                _activeSlotId = kv.Key;
                return Result<string>.Success(kv.Key);
            }
        }

        var slotId = Guid.NewGuid().ToString("N")[..10];
        _slots[slotId] = full;
        _activeSlotId = slotId;
        return Result<string>.Success(slotId);
    }

    public bool TryResolve(string slotId, out string webAppDir) =>
        _slots.TryGetValue(slotId, out webAppDir!);
}
