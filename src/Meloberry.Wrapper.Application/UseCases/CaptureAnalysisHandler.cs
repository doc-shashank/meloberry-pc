using System.Text.Json;
using System.Text.Json.Nodes;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;

namespace Meloberry.Wrapper.Application.UseCases;

/// <summary>Captures POST /api/analysis payloads emitted by mounted mby WebApps.</summary>
public sealed class CaptureAnalysisHandler(
    IWebAppMountRegistry mountRegistry,
    IWebAppAnalysisStore analysisStore,
    IManagedAppsStore? appsStore = null,
    IMbyCliClient? mbyClient = null,
    ISettingsStore? settingsStore = null)
{
    public async Task<Result<string>> CaptureAsync(string? slotId, string? reportJson, string? markdown, CancellationToken ct = default)
    {
        var targetSlot = !string.IsNullOrWhiteSpace(slotId) ? slotId : mountRegistry.ActiveSlotId;
        if (string.IsNullOrWhiteSpace(targetSlot) || !mountRegistry.TryResolve(targetSlot, out var webDir))
        {
            return Result<string>.Failure(WrapperErrors.InvalidWebAppDir("No active WebApp mounted."));
        }

        if (!string.IsNullOrWhiteSpace(reportJson) &&
            appsStore is not null &&
            mbyClient is not null &&
            TryBuildIngestPayload(reportJson, markdown, out var ingestPayload))
        {
            var settings = settingsStore is not null
                ? await settingsStore.LoadAsync(ct).ConfigureAwait(false)
                : null;
            var mbyExe = await appsStore.ResolveToolExecutableAsync(
                "mby",
                settings?.ActiveWorkspace?.WorkspacePath,
                settings?.MbyExecutableOverride,
                ct).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(mbyExe))
            {
                var verRes = await mbyClient.GetVersionInfoAsync(mbyExe, ct).ConfigureAwait(false);
                if (verRes.IsSuccess && verRes.Value.IsCompatible)
                {
                    var ingestRes = await mbyClient.IngestAnalysisAsync(mbyExe, webDir, ingestPayload, ct).ConfigureAwait(false);
                    if (ingestRes.IsSuccess && !string.IsNullOrWhiteSpace(ingestRes.Value.JsonPath))
                    {
                        return Result<string>.Success(ingestRes.Value.JsonPath);
                    }
                }
            }
        }

        return await analysisStore.SaveAnalysisAsync(webDir, reportJson, markdown, ct).ConfigureAwait(false);
    }

    private static bool TryBuildIngestPayload(string reportJson, string? markdown, out string ingestPayload)
    {
        ingestPayload = string.Empty;
        try
        {
            var node = JsonNode.Parse(reportJson);
            if (node is not JsonObject obj)
            {
                return false;
            }

            var wrapper = new JsonObject
            {
                ["report"] = obj
            };
            if (!string.IsNullOrWhiteSpace(markdown))
            {
                wrapper["markdown"] = markdown;
            }

            ingestPayload = wrapper.ToJsonString();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
