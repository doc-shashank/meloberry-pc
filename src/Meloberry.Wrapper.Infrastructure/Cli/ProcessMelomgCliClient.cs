using System.Text.Json;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;

namespace Meloberry.Wrapper.Infrastructure.Cli;

/// <summary>Out-of-process runner for `melomg` CLI.</summary>
public sealed class ProcessMelomgCliClient : IMelomgCliClient
{
    public async Task<Result<string>> GetSpecTextAsync(
        string exePath,
        string? workingDir,
        IReadOnlyList<string> topics,
        CancellationToken ct = default)
    {
        var args = new List<string> { "spec" };
        args.AddRange(topics.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase));
        args.Add("--json");

        var res = await CliProcessRunner.RunAsync(exePath, args, workingDir, ct).ConfigureAwait(false);
        if (res.ExitCode != 0)
        {
            return Result<string>.Failure(
                WrapperErrors.CliExecutionFailed("melomg", res.ExitCode, CliProcessRunner.ExtractErrorMessage(res.StdOut, res.StdErr)));
        }

        try
        {
            using var doc = JsonDocument.Parse(res.StdOut);
            var specs = doc.RootElement.GetProperty("data").GetProperty("specs");
            var chunks = new List<string>();
            foreach (var spec in specs.EnumerateArray())
            {
                if (spec.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                {
                    var text = content.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        chunks.Add(text.Trim());
                    }
                }
            }

            return Result<string>.Success(string.Join("\n\n", chunks));
        }
        catch (Exception ex)
        {
            return Result<string>.Failure(
                WrapperErrors.CliExecutionFailed("melomg", res.ExitCode, $"Failed to parse `melomg spec --json`: {ex.Message}"));
        }
    }
}
