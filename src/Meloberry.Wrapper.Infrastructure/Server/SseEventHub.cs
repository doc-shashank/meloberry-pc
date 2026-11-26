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

    public int SubscriberCount => _clients.Count;

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
