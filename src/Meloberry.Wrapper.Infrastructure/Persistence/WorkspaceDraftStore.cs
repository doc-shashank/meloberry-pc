using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;
using Meloberry.Wrapper.Infrastructure.Common;

namespace Meloberry.Wrapper.Infrastructure.Persistence;

/// <summary>Thread-safe atomic persistence for `<Workspace>/.meloberry/pc-draft.json`.</summary>
public sealed class WorkspaceDraftStore : IWorkspaceDraftStore
{
    public const string RelativeDraftPath = ".meloberry/pc-draft.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public async Task<PromptDraft> LoadAsync(string workspacePath, CancellationToken ct = default)
    {
        if (!PathNormalization.TryNormalizeDirectoryPath(workspacePath, out var fullWs))
        {
            return CreateDefaultDraft(null);
        }

        var sem = _locks.GetOrAdd(fullWs, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var draftPath = GetDraftFilePath(fullWs);
            if (!File.Exists(draftPath))
            {
                return CreateDefaultDraft(fullWs);
            }

            try
            {
                await using var fs = File.OpenRead(draftPath);
                var loaded = await JsonSerializer.DeserializeAsync<PromptDraft>(fs, JsonOptions, ct).ConfigureAwait(false);
                if (loaded is null)
                {
                    return CreateDefaultDraft(fullWs);
                }

                return loaded with { WorkingDirectory = fullWs };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return CreateDefaultDraft(fullWs);
            }
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task<Result<PromptDraft>> SaveAsync(string workspacePath, PromptDraft draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!PathNormalization.TryNormalizeDirectoryPath(workspacePath, out var fullWs))
        {
            return Result<PromptDraft>.Failure(WrapperErrors.InvalidWorkspaceDir(workspacePath));
        }

        var normalizedDraft = draft with { WorkingDirectory = fullWs };
        var sem = _locks.GetOrAdd(fullWs, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var draftPath = GetDraftFilePath(fullWs);
            var json = JsonSerializer.Serialize(normalizedDraft, JsonOptions);
            await AtomicFileOps.WriteAllTextAsync(draftPath, json, ct).ConfigureAwait(false);
            return Result<PromptDraft>.Success(normalizedDraft);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result<PromptDraft>.Failure(WrapperErrors.IoFailure($"Failed to persist workspace draft: {ex.Message}"));
        }
        finally
        {
            sem.Release();
        }
    }

    private static string GetDraftFilePath(string fullWs) =>
        Path.Combine(fullWs, ".meloberry", "pc-draft.json");

    private static PromptDraft CreateDefaultDraft(string? fullWs)
    {
        var preset = BuiltInPresets.All[0];
        return new PromptDraft(
            Container: preset.Container,
            Title: string.Empty,
            Description: string.Empty,
            AuthoringFormat: preset.AuthoringFormat,
            SourceText: string.Empty,
            Selections: preset.Selections,
            Attachments: [],
            UseMelomgForPdfs: true,
            EmbedInlineSchemas: false,
            LazyCliDiscovery: true,
            AgentsMdExported: false,
            WorkingDirectory: fullWs,
            Components: preset.Components,
            WorkflowMode: WorkflowMode.Direct,
            AllowPlaceholdersOnFinalize: false);
    }
}
