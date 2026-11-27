using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Meloberry.Wrapper.Application.Abstractions;

namespace Meloberry.Wrapper.Infrastructure.Cli;

/// <summary>Authenticated HTTP loopback client connecting `--cli` invocations to a running Meloberry PC GUI.</summary>
public sealed class LiveGuiIpcClient(IRuntimeIpcRegistry ipcRegistry)
{
    private static readonly HttpClient SharedHttp = new()
    {
        Timeout = TimeSpan.FromMinutes(3)
    };

    public bool IsGuiAlive() => ipcRegistry.TryReadActive() is not null;

    public async Task<(bool Reachable, int StatusCode, string Body)> SendAsync(
        HttpMethod method,
        string relativeApiPath,
        object? payload = null,
        CancellationToken ct = default)
    {
        var active = ipcRegistry.TryReadActive();
        if (active is null)
        {
            return (false, 0, string.Empty);
        }

        var url = $"http://127.0.0.1:{active.ShellPort}{relativeApiPath}";
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Mby-Token", active.Token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (payload is not null)
        {
            var json = JsonSerializer.Serialize(payload, Server.HttpEnvelope.JsonOptions);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        try
        {
            using var resp = await SharedHttp.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return (true, (int)resp.StatusCode, body);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return (false, 0, string.Empty);
        }
    }

    public async Task TryPostUiAsync(
        string action,
        string? view = null,
        string? path = null,
        string? message = null,
        string? level = null,
        CancellationToken ct = default)
    {
        try
        {
            await SendAsync(HttpMethod.Post, "/api/live/ui", new { action, view, path, message, level }, ct).ConfigureAwait(false);
        }
        catch
        {
            // Live GUI notifications are strictly best-effort and non-fatal.
        }
    }
}
