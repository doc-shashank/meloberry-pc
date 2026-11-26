using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meloberry.Wrapper.Domain.Common;

namespace Meloberry.Wrapper.Infrastructure.Server;

/// <summary>Shared JSON serialization, bounded request reading, and Result-to-HTTP envelope helpers.</summary>
public static class HttpEnvelope
{
    public const int MaxBodyBytes = 10 * 1024 * 1024;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static int MapStatusCode(ErrorType type) =>
        type switch
        {
            ErrorType.Validation => 400,
            ErrorType.NotFound => 404,
            ErrorType.Conflict => 409,
            ErrorType.Security => 403,
            ErrorType.Network => 502,
            ErrorType.ExternalProcess => 422,
            _ => 500
        };

    public static async Task<(bool TooLarge, string Body)> ReadBoundedUtf8BodyAsync(
        HttpListenerRequest req,
        int maxBytes,
        CancellationToken ct)
    {
        if (req.ContentLength64 > maxBytes)
        {
            return (true, string.Empty);
        }

        using var ms = new MemoryStream();
        var buffer = new byte[16384];
        long total = 0;
        int read;
        while ((read = await req.InputStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                return (true, string.Empty);
            }

            ms.Write(buffer, 0, read);
        }

        return (false, Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
    }

    public static async Task<T?> ReadJsonAsync<T>(HttpListenerRequest req, CancellationToken ct)
    {
        try
        {
            var (tooLarge, body) = await ReadBoundedUtf8BodyAsync(req, MaxBodyBytes, ct).ConfigureAwait(false);
            if (tooLarge || string.IsNullOrWhiteSpace(body))
            {
                return default;
            }

            return JsonSerializer.Deserialize<T>(body, JsonOptions);
        }
        catch
        {
            return default;
        }
    }

    public static Task WriteResultAsync<T>(HttpListenerResponse resp, Result<T> result, CancellationToken ct) =>
        result.IsSuccess
            ? WriteJsonAsync(resp, 200, result.Value, ct)
            : WriteErrorAsync(resp, result.Error!, ct);

    public static Task WriteResultAsync(HttpListenerResponse resp, Result result, CancellationToken ct) =>
        result.IsSuccess
            ? WriteJsonAsync(resp, 200, new { ok = true }, ct)
            : WriteErrorAsync(resp, result.Error!, ct);

    public static Task WriteErrorAsync(HttpListenerResponse resp, Error error, CancellationToken ct) =>
        WriteJsonErrorAsync(resp, MapStatusCode(error.Type), error.Code, error.Description, ct, error.Hint);

    public static async Task WriteJsonAsync(HttpListenerResponse resp, int status, object? payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        resp.StatusCode = status;
        resp.ContentType = "application/json; charset=utf-8";
        await resp.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
        resp.Close();
    }

    public static Task WriteJsonErrorAsync(
        HttpListenerResponse resp,
        int status,
        string code,
        string description,
        CancellationToken ct,
        string? hint = null) =>
        WriteJsonAsync(resp, status, new { error = new { code, description, hint } }, ct);
}
