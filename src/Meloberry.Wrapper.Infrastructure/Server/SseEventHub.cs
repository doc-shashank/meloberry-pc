using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Meloberry.Wrapper.Infrastructure.Server;

/// <summary>Manages Server-Sent Event (SSE) subscribers, 15s keep-alive pings, and broadcasts.</summary>
public sealed class SseEventHub
{
    private readonly ConcurrentDictionary<Guid, Channel<byte[]>> _clients = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentWebAppBroadcasts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _activeFinalizeWorkspaces = new(StringComparer.OrdinalIgnoreCase);

    public int SubscriberCount => _clients.Count;

    public IDisposable BeginFinalizeSuppression(string? workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return NoopDisposable.Instance;
        }

        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
        _activeFinalizeWorkspaces.AddOrUpdate(key, 1, (_, count) => count + 1);
        return new FinalizeSuppressionScope(this, key);
    }

    public bool IsFinalizeInProgress(string? workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return false;
        }

        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
        return _activeFinalizeWorkspaces.TryGetValue(key, out var count) && count > 0;
    }

    private void EndFinalizeSuppression(string key)
    {
        while (_activeFinalizeWorkspaces.TryGetValue(key, out var count))
        {
            if (count <= 1)
            {
                if (_activeFinalizeWorkspaces.TryRemove(key, out _))
                {
                    return;
                }
            }
            else if (_activeFinalizeWorkspaces.TryUpdate(key, count - 1, count))
            {
                return;
            }
        }
    }

    public void MarkRecentlyBroadcastWebApp(string webAppDir)
    {
        if (string.IsNullOrWhiteSpace(webAppDir))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(webAppDir));
        _recentWebAppBroadcasts[key] = now;

        // Prune stale dedup entries so dictionary stays bounded over long sessions.
        foreach (var (k, ts) in _recentWebAppBroadcasts)
        {
            if ((now - ts).TotalSeconds > 30)
            {
                _recentWebAppBroadcasts.TryRemove(k, out _);
            }
        }
    }

    public bool WasRecentlyBroadcastWebApp(string webAppDir, int windowMs = 3000)
    {
        if (string.IsNullOrWhiteSpace(webAppDir))
        {
            return false;
        }

        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(webAppDir));
        return _recentWebAppBroadcasts.TryGetValue(key, out var ts) &&
               (DateTimeOffset.UtcNow - ts).TotalMilliseconds <= windowMs;
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }

    private sealed class FinalizeSuppressionScope(SseEventHub hub, string key) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                hub.EndFinalizeSuppression(key);
            }
        }
    }

    public async Task HandleStreamAsync(HttpListenerResponse resp, CancellationToken ct)
    {
        resp.ContentType = "text/event-stream; charset=utf-8";
        resp.Headers["Cache-Control"] = "no-cache";
        resp.SendChunked = true;
        resp.StatusCode = 200;

        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        _clients[id] = channel;

        try
        {
            var hello = Encoding.UTF8.GetBytes(": connected\n\n");
            await resp.OutputStream.WriteAsync(hello, ct).ConfigureAwait(false);
            await resp.OutputStream.FlushAsync(ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                while (channel.Reader.TryRead(out var frame))
                {
                    await resp.OutputStream.WriteAsync(frame, ct).ConfigureAwait(false);
                    await resp.OutputStream.FlushAsync(ct).ConfigureAwait(false);
                }

                using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pingCts.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    if (!await channel.Reader.WaitToReadAsync(pingCts.Token).ConfigureAwait(false))
                    {
                        break;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    var ping = Encoding.UTF8.GetBytes(": ping\n\n");
                    await resp.OutputStream.WriteAsync(ping, ct).ConfigureAwait(false);
                    await resp.OutputStream.FlushAsync(ct).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            // Client disconnected or server shutting down.
        }
        finally
        {
            _clients.TryRemove(id, out _);
            channel.Writer.TryComplete();
            try { resp.Close(); } catch { }
        }
    }

    public void Broadcast(string eventName, object payload)
    {
        var json = JsonSerializer.Serialize(payload, HttpEnvelope.JsonOptions);
        var frame = Encoding.UTF8.GetBytes($"event: {eventName}\ndata: {json}\n\n");

        foreach (var kv in _clients)
        {
            kv.Value.Writer.TryWrite(frame);
        }
    }

    public void CompleteAll()
    {
        foreach (var kv in _clients)
        {
            kv.Value.Writer.TryComplete();
        }

        _clients.Clear();
    }
}
