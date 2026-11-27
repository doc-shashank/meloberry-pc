using System.Collections.Concurrent;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Infrastructure.Workspace;

/// <summary>
/// Real-time FileSystemWatcher over %LOCALAPPDATA%\Meloberry\apps\ and the Active Workspace
/// with short burst debouncing to keep the UI and Notification Center synchronized.
/// </summary>
public sealed class WorkspaceFileWatcherService : IWorkspaceFileWatcher, IDisposable
{
    private readonly IWrapperDataPaths _dataPaths;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastEventByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _activeSuppressCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _suppressedUntilByScope = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    private FileSystemWatcher? _appsWatcher;
    private FileSystemWatcher? _workspaceWatcher;
    private string? _activeWorkspacePath;

    public event Action<WorkspaceFileChangeEvent>? Changed;

    public WorkspaceFileWatcherService(IWrapperDataPaths dataPaths)
    {
        _dataPaths = dataPaths;
        StartAppsWatcher();
    }

    public void UpdateActiveWorkspace(string? workspacePath)
    {
        string? normalized = null;
        if (PathNormalization.TryNormalizeDirectoryPath(workspacePath, out var full) && Directory.Exists(full))
        {
            normalized = full;
        }

        lock (_sync)
        {
            if (string.Equals(_activeWorkspacePath, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _workspaceWatcher?.Dispose();
            _workspaceWatcher = null;
            _activeWorkspacePath = normalized;

            if (normalized is null)
            {
                return;
            }

            try
            {
                var watcher = new FileSystemWatcher(normalized, "*")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
                };
                watcher.Created += (_, e) => HandleWorkspaceEvent("created", e.FullPath);
                watcher.Changed += (_, e) => HandleWorkspaceEvent("changed", e.FullPath);
                watcher.Deleted += (_, e) => HandleWorkspaceEvent("deleted", e.FullPath);
                watcher.Renamed += (_, e) =>
                {
                    HandleWorkspaceEvent("deleted", e.OldFullPath);
                    HandleWorkspaceEvent("created", e.FullPath);
                };
                watcher.Error += (_, _) => { };
                watcher.EnableRaisingEvents = true;
                _workspaceWatcher = watcher;
            }
            catch
            {
                _workspaceWatcher = null;
            }
        }
    }

    private void StartAppsWatcher()
    {
        try
        {
            Directory.CreateDirectory(_dataPaths.AppsDirectory);
            var watcher = new FileSystemWatcher(_dataPaths.AppsDirectory, "*.exe")
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
            };
            watcher.Created += (_, e) => EmitDebounced("apps", "created", e.FullPath);
            watcher.Deleted += (_, e) => EmitDebounced("apps", "deleted", e.FullPath);
            watcher.Renamed += (_, e) =>
            {
                if (e.OldFullPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    EmitDebounced("apps", "deleted", e.OldFullPath);
                }

                if (e.FullPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    EmitDebounced("apps", "created", e.FullPath);
                }
            };
            watcher.Error += (_, _) => { };
            watcher.EnableRaisingEvents = true;
            _appsWatcher = watcher;
        }
        catch
        {
            _appsWatcher = null;
        }
    }

    private void HandleWorkspaceEvent(string changeKind, string fullPath)
    {
        var fileName = Path.GetFileName(fullPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return;
        }

        if (fullPath.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
            fullPath.Contains($"{Path.DirectorySeparatorChar}.meloberry{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".mbytmp", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.Equals(fileName, MbyCliSignal.SignalFileName, StringComparison.OrdinalIgnoreCase) &&
            changeKind is "created" or "changed" &&
            PathNormalization.AreSameDirectory(Path.GetDirectoryName(fullPath), _activeWorkspacePath))
        {
            EmitDebounced("signal", "updated", fullPath, bypassSuppression: true, debounceMs: 120);
            return;
        }

        if (changeKind == "changed")
        {
            return;
        }

        var isWatchedFile =
            fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "AGENTS.md", StringComparison.OrdinalIgnoreCase);

        if (isWatchedFile)
        {
            EmitDebounced("workspace", changeKind, fullPath);
        }
    }

    public IDisposable SuppressScope(string scope, int postDisposeGraceMs = 1500)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return NoopDisposable.Instance;
        }

        _activeSuppressCounts.AddOrUpdate(scope, 1, (_, count) => count + 1);
        return new SuppressionHandle(this, scope, postDisposeGraceMs);
    }

    private void EndSuppression(string scope, int postDisposeGraceMs)
    {
        if (postDisposeGraceMs > 0)
        {
            var until = DateTimeOffset.UtcNow.AddMilliseconds(postDisposeGraceMs);
            _suppressedUntilByScope.AddOrUpdate(scope, until, (_, prev) => until > prev ? until : prev);
        }

        _activeSuppressCounts.AddOrUpdate(scope, 0, (_, count) => Math.Max(0, count - 1));
    }

    private void EmitDebounced(string scope, string changeKind, string fullPath, bool bypassSuppression = false, int debounceMs = 300)
    {
        var now = DateTimeOffset.UtcNow;
        if (!bypassSuppression &&
            ((_activeSuppressCounts.TryGetValue(scope, out var activeCount) && activeCount > 0) ||
             (_suppressedUntilByScope.TryGetValue(scope, out var suppressedUntil) && now <= suppressedUntil)))
        {
            return;
        }

        var key = $"{scope}|{changeKind}|{fullPath}";
        if (_lastEventByKey.TryGetValue(key, out var prev) && (now - prev).TotalMilliseconds < debounceMs)
        {
            return;
        }

        _lastEventByKey[key] = now;
        if (_lastEventByKey.Count > 256)
        {
            foreach (var old in _lastEventByKey)
            {
                if ((now - old.Value).TotalSeconds > 5)
                {
                    _lastEventByKey.TryRemove(old.Key, out _);
                }
            }
        }

        var fileName = Path.GetFileName(fullPath);
        Changed?.Invoke(new WorkspaceFileChangeEvent(scope, changeKind, fileName, fullPath, now));
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }

    private sealed class SuppressionHandle(WorkspaceFileWatcherService owner, string scope, int postDisposeGraceMs) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.EndSuppression(scope, postDisposeGraceMs);
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _appsWatcher?.Dispose();
            _appsWatcher = null;
            _workspaceWatcher?.Dispose();
            _workspaceWatcher = null;
        }
    }
}
