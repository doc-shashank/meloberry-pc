using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;

namespace Meloberry.Wrapper.Application.UseCases;

/// <summary>Captures POST /api/analysis payloads emitted by mounted mby WebApps.</summary>
public sealed class CaptureAnalysisHandler(
    IWebAppMountRegistry mountRegistry,
    IWebAppAnalysisStore analysisStore)
{
    public async Task<Result<string>> CaptureAsync(string? slotId, string? reportJson, string? markdown, CancellationToken ct = default)
    {
        var targetSlot = !string.IsNullOrWhiteSpace(slotId) ? slotId : mountRegistry.ActiveSlotId;
        if (string.IsNullOrWhiteSpace(targetSlot) || !mountRegistry.TryResolve(targetSlot, out var webDir))
        {
            return Result<string>.Failure(WrapperErrors.InvalidWebAppDir("No active WebApp mounted."));
        }

        return await analysisStore.SaveAnalysisAsync(webDir, reportJson, markdown, ct).ConfigureAwait(false);
    }
}
