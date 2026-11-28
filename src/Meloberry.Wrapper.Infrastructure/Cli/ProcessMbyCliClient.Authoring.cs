using System.Globalization;
using System.Text.Json;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Infrastructure.Cli;

public sealed partial class ProcessMbyCliClient
{
    public Task<Result<string>> ApplyAtAsync(
        string exePath,
        string targetAddr,
        string specPath,
        int insertAt,
        string? workingDir,
        CancellationToken ct = default) =>
        RunApplyCoreAsync(
            exePath,
            ["apply", targetAddr, specPath, "--at", insertAt.ToString(CultureInfo.InvariantCulture), "--json"],
            workingDir,
            ct);

    private static async Task<Result<string>> RunApplyCoreAsync(
        string exePath,
        IReadOnlyList<string> args,
        string? workingDir,
        CancellationToken ct)
    {
        var res = await CliProcessRunner.RunAsync(exePath, args, workingDir, ct).ConfigureAwait(false);
        if (res.ExitCode != 0)
        {
            return Result<string>.Failure(
                WrapperErrors.CliExecutionFailed("mby", res.ExitCode, CliProcessRunner.ExtractErrorMessage(res.StdOut, res.StdErr)));
        }

        try
        {
            using var doc = JsonDocument.Parse(res.StdOut);
            var data = doc.RootElement.GetProperty("data");
            var id = MbyCliResponseParsers.GetNullableString(data, "targetId")
                ?? MbyCliResponseParsers.GetNullableString(data, "id")
                ?? MbyCliResponseParsers.GetNullableString(data, "workspaceId")
                ?? MbyCliResponseParsers.GetNullableString(data, "sessionId")
                ?? MbyCliResponseParsers.GetNullableString(data, "setId")
                ?? string.Empty;
            return Result<string>.Success(id);
        }
        catch
        {
            return Result<string>.Success(string.Empty);
        }
    }

    public async Task<Result<ScaffoldCliResult>> ScaffoldAsync(
        string exePath,
        string blueprintJson,
        string format,
        string? outputPath,
        bool apply,
        bool allowPlaceholders,
        string? workingDir,
        CancellationToken ct = default)
    {
        var args = new List<string> { "scaffold", "-", "--format", format };
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            args.Add("-o");
            args.Add(outputPath);
        }
        if (apply) args.Add("--apply");
        if (allowPlaceholders) args.Add("--placeholders");
        args.Add("--json");

        var res = await CliProcessRunner.RunAsync(
            exePath,
            args,
            workingDir,
            ct,
            timeout: TimeSpan.FromMinutes(2),
            stdinText: blueprintJson).ConfigureAwait(false);

        if (res.ExitCode != 0)
        {
            var missingErr = MbyCliResponseParsers.TryParseScaffoldMissingContentError(res.StdOut, res.StdErr);
            return Result<ScaffoldCliResult>.Failure(
                missingErr ?? WrapperErrors.CliExecutionFailed("mby", res.ExitCode, CliProcessRunner.ExtractErrorMessage(res.StdOut, res.StdErr)));
        }

        return MbyCliResponseParsers.ParseScaffoldPayload(res.StdOut, res.ExitCode);
    }

    public async Task<Result<AssetAddCliResult>> AddAssetAsync(
        string exePath,
        string? workspaceAddr,
        string? targetDir,
        string sourceFilePath,
        string? workingDir,
        CancellationToken ct = default)
    {
        var args = new List<string> { "asset", "add" };
        if (!string.IsNullOrWhiteSpace(targetDir))
        {
            args.Add("--dir");
            args.Add(targetDir);
            args.Add(sourceFilePath);
        }
        else if (!string.IsNullOrWhiteSpace(workspaceAddr))
        {
            args.Add(workspaceAddr);
            args.Add(sourceFilePath);
        }
        else
        {
            return Result<AssetAddCliResult>.Failure(WrapperErrors.InvalidPath("Either workspace address or target directory is required."));
        }

        args.Add("--json");
        var res = await CliProcessRunner.RunAsync(exePath, args, workingDir, ct).ConfigureAwait(false);
        if (res.ExitCode != 0)
        {
            return Result<AssetAddCliResult>.Failure(
                WrapperErrors.CliExecutionFailed("mby", res.ExitCode, CliProcessRunner.ExtractErrorMessage(res.StdOut, res.StdErr)));
        }

        return MbyCliResponseParsers.ParseAssetAddPayload(res.StdOut, res.ExitCode);
    }

    public async Task<Result<string>> PatchWithMarkdownAsync(
        string exePath,
        string targetAddr,
        string markdownFragment,
        string? subtype,
        string? imagePath,
        string? workingDir,
        CancellationToken ct = default)
    {
        var args = new List<string> { "patch", targetAddr, "-", "--md" };
        if (!string.IsNullOrWhiteSpace(subtype))
        {
            args.Add("--subtype");
            args.Add(subtype);
        }
        if (!string.IsNullOrWhiteSpace(imagePath))
        {
            args.Add("--image");
            args.Add(imagePath);
        }
        args.Add("--json");

        var res = await CliProcessRunner.RunAsync(exePath, args, workingDir, ct, stdinText: markdownFragment).ConfigureAwait(false);
        return ExtractTargetIdResult(res, targetAddr);
    }

    public async Task<Result<PrimeAgentsMdCliResult>> PrimeAgentsMdAsync(
        string exePath,
        IReadOnlyList<string> topics,
        string container,
        string authoringFormat,
        string cliPrefix,
        string? workspacePath,
        CancellationToken ct = default)
    {
        var topicArg = topics.Count > 0 ? string.Join(",", topics) : "@content";
        var args = new List<string>
        {
            "prime",
            "--topics", topicArg,
            "--container", container,
            "--authoring-format", authoringFormat,
            "--format", "agents-md",
            "--cli-prefix", cliPrefix
        };
        if (!string.IsNullOrWhiteSpace(workspacePath))
        {
            args.Add("--workspace");
            args.Add(workspacePath);
        }
        args.Add("--json");

        var res = await CliProcessRunner.RunAsync(exePath, args, workspacePath, ct).ConfigureAwait(false);
        if (res.ExitCode != 0)
        {
            return Result<PrimeAgentsMdCliResult>.Failure(
                WrapperErrors.CliExecutionFailed("mby", res.ExitCode, CliProcessRunner.ExtractErrorMessage(res.StdOut, res.StdErr)));
        }

        return MbyCliResponseParsers.ParsePrimeAgentsMdPayload(res.StdOut, res.ExitCode);
    }

    public async Task<Result<IngestAnalysisCliResult>> IngestAnalysisAsync(
        string exePath,
        string webAppDir,
        string payloadJson,
        CancellationToken ct = default)
    {
        var res = await CliProcessRunner.RunAsync(
            exePath,
            ["analyze", webAppDir, "--ingest", "-", "--json"],
            webAppDir,
            ct,
            stdinText: payloadJson).ConfigureAwait(false);

        if (res.ExitCode != 0)
        {
            return Result<IngestAnalysisCliResult>.Failure(
                WrapperErrors.CliExecutionFailed("mby", res.ExitCode, CliProcessRunner.ExtractErrorMessage(res.StdOut, res.StdErr)));
        }

        return MbyCliResponseParsers.ParseIngestAnalysisPayload(res.StdOut, res.ExitCode);
    }

    public async Task<Result<string>> MoveEntityAsync(
        string exePath,
        string addr,
        string targetCompAddr,
        int? index,
        string? workingDir,
        CancellationToken ct = default)
    {
        var args = new List<string> { "patch", addr, "--to", targetCompAddr };
        if (index.HasValue)
        {
            args.Add("--at");
            args.Add(index.Value.ToString(CultureInfo.InvariantCulture));
        }
        args.Add("--json");

        var res = await CliProcessRunner.RunAsync(exePath, args, workingDir, ct).ConfigureAwait(false);
        return ExtractTargetIdResult(res, addr);
    }

    public async Task<Result<string>> ReorderContainerAsync(
        string exePath,
        string containerAddr,
        IReadOnlyList<string> orderedIds,
        string? workingDir,
        CancellationToken ct = default)
    {
        var args = new List<string>
        {
            "patch",
            containerAddr,
            "--order",
            string.Join(",", orderedIds),
            "--json"
        };

        var res = await CliProcessRunner.RunAsync(exePath, args, workingDir, ct).ConfigureAwait(false);
        return ExtractTargetIdResult(res, containerAddr);
    }

    public async Task<Result<CloneEntityCliResult>> CloneEntityAsync(
        string exePath,
        string addr,
        string? targetCompAddr,
        int? insertAt,
        string? newRef,
        string? workingDir,
        CancellationToken ct = default)
    {
        var args = new List<string> { "clone", addr };
        if (!string.IsNullOrWhiteSpace(targetCompAddr))
        {
            args.Add("--to");
            args.Add(targetCompAddr);
        }
        if (insertAt.HasValue)
        {
            args.Add("--at");
            args.Add(insertAt.Value.ToString(CultureInfo.InvariantCulture));
        }
        if (!string.IsNullOrWhiteSpace(newRef))
        {
            args.Add("--ref");
            args.Add(newRef);
        }
        args.Add("--json");

        var res = await CliProcessRunner.RunAsync(exePath, args, workingDir, ct).ConfigureAwait(false);
        if (res.ExitCode != 0)
        {
            return Result<CloneEntityCliResult>.Failure(
                WrapperErrors.CliExecutionFailed("mby", res.ExitCode, CliProcessRunner.ExtractErrorMessage(res.StdOut, res.StdErr)));
        }

        return MbyCliResponseParsers.ParseClonePayload(res.StdOut, res.ExitCode);
    }

    private static Result<string> ExtractTargetIdResult((int ExitCode, string StdOut, string StdErr) res, string fallbackId)
    {
        if (res.ExitCode != 0)
        {
            return Result<string>.Failure(
                WrapperErrors.CliExecutionFailed("mby", res.ExitCode, CliProcessRunner.ExtractErrorMessage(res.StdOut, res.StdErr)));
        }

        try
        {
            using var doc = JsonDocument.Parse(res.StdOut);
            var data = doc.RootElement.GetProperty("data");
            return Result<string>.Success(MbyCliResponseParsers.GetNullableString(data, "targetId") ?? fallbackId);
        }
        catch
        {
            return Result<string>.Success(fallbackId);
        }
    }
}
