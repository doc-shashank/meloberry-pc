using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Application.UseCases;

/// <summary>Result produced when a `.mby-signal.txt` `webapp_built` event auto-mounts a WebApp.</summary>
public sealed record CliSignalWebAppBuiltResult(
    string SlotId,
    string RelativeUrl,
    WebAppLibraryEntry Entry,
    string? WorkspaceId,
    string? Title,
    string SignalId);

/// <summary>Parses, deduplicates, and dispatches CLI IPC events written to `.mby-signal.txt`.</summary>
public sealed class ProcessCliSignalHandler(
    ISettingsStore settingsStore,
    ManageLibraryHandler libraryHandler)
{
    private const int MaxRememberedIds = 128;
    private readonly object _dedupLock = new();
    private readonly HashSet<string> _seenSignalIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();

    public async Task<Result<CliSignalWebAppBuiltResult?>> ProcessFileAsync(string signalFilePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(signalFilePath) || !File.Exists(signalFilePath))
        {
            return Result<CliSignalWebAppBuiltResult?>.Success(null);
        }

        var content = await ReadTextWithRetryAsync(signalFilePath, ct).ConfigureAwait(false);
        if (!MbyCliSignalParser.TryParse(content, out var signal) || signal is null)
        {
            return Result<CliSignalWebAppBuiltResult?>.Success(null);
        }

        if (!string.Equals(signal.Event, MbyCliSignal.EventWebAppBuilt, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(signal.Path))
        {
            return Result<CliSignalWebAppBuiltResult?>.Success(null);
        }

        if (IsAlreadyProcessed(signal.Id))
        {
            return Result<CliSignalWebAppBuiltResult?>.Success(null);
        }

        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var signalDir = Path.GetDirectoryName(signalFilePath);
        var baseDir = !string.IsNullOrWhiteSpace(signalDir)
            ? signalDir
            : settings.ActiveWorkspace?.WorkspacePath;

        string resolvedDir;
        try
        {
            resolvedDir = Path.IsPathRooted(signal.Path)
                ? Path.GetFullPath(signal.Path)
                : (!string.IsNullOrWhiteSpace(baseDir)
                    ? Path.GetFullPath(Path.Combine(baseDir, signal.Path))
                    : Path.GetFullPath(signal.Path));
        }
        catch
        {
            return Result<CliSignalWebAppBuiltResult?>.Success(null);
        }

        var openRes = await libraryHandler.OpenWebAppAsync(resolvedDir, ct).ConfigureAwait(false);
        if (openRes.IsFailure)
        {
            return Result<CliSignalWebAppBuiltResult?>.Failure(openRes.Error!);
        }

        MarkProcessed(signal.Id);
        return Result<CliSignalWebAppBuiltResult?>.Success(new CliSignalWebAppBuiltResult(
            SlotId: openRes.Value.SlotId,
            RelativeUrl: openRes.Value.Url,
            Entry: openRes.Value.Entry,
            WorkspaceId: signal.WorkspaceId,
            Title: signal.Title,
            SignalId: signal.Id));
    }

    private bool IsAlreadyProcessed(string id)
    {
        lock (_dedupLock)
        {
            return _seenSignalIds.Contains(id);
        }
    }

    private void MarkProcessed(string id)
    {
        lock (_dedupLock)
        {
            if (!_seenSignalIds.Add(id))
            {
                return;
            }

            _seenOrder.Enqueue(id);
            while (_seenOrder.Count > MaxRememberedIds)
            {
                var oldest = _seenOrder.Dequeue();
                _seenSignalIds.Remove(oldest);
            }
        }
    }

    private static async Task<string?> ReadTextWithRetryAsync(string filePath, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                return await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (IOException) when (attempt < 3)
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (attempt < 3)
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }
}
