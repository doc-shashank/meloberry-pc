using System.Globalization;

namespace Meloberry.Wrapper.Domain.Models;

/// <summary>Structured event parsed from a workspace `.mby-signal.txt` IPC file emitted by `mby` CLI.</summary>
public sealed record MbyCliSignal(
    int Version,
    string Event,
    string Id,
    DateTimeOffset? TimestampUtc,
    string? WorkspaceId,
    string? Path,
    string? Title,
    string? Producer,
    IReadOnlyDictionary<string, string> RawFields)
{
    public const string SignalFileName = ".mby-signal.txt";
    public const string EventWebAppBuilt = "webapp_built";
}

/// <summary>Zero-dependency UTF-8 key=value parser for `.mby-signal.txt` files.</summary>
public static class MbyCliSignalParser
{
    public static bool TryParse(string? content, out MbyCliSignal? signal)
    {
        signal = null;
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = content.Split(["\r\n", "\n"], StringSplitOptions.None);
        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var eq = trimmed.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = trimmed[..eq].Trim();
            var val = trimmed[(eq + 1)..].Trim();
            if (key.Length > 0)
            {
                fields[key] = val;
            }
        }

        if (!fields.TryGetValue("event", out var evt) || string.IsNullOrWhiteSpace(evt))
        {
            return false;
        }

        fields.TryGetValue("path", out var path);
        if (string.Equals(evt, MbyCliSignal.EventWebAppBuilt, StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var version = 1;
        if (fields.TryGetValue("version", out var verRaw) && int.TryParse(verRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedVer) && parsedVer > 0)
        {
            version = parsedVer;
        }

        DateTimeOffset? ts = null;
        fields.TryGetValue("timestamp", out var tsRaw);
        if (!string.IsNullOrWhiteSpace(tsRaw) &&
            DateTimeOffset.TryParse(tsRaw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedTs))
        {
            ts = parsedTs;
        }

        fields.TryGetValue("id", out var id);
        if (string.IsNullOrWhiteSpace(id))
        {
            id = $"auto:{evt.Trim()}|{path?.Trim()}|{tsRaw?.Trim()}";
        }

        fields.TryGetValue("workspace_id", out var wsId);
        fields.TryGetValue("title", out var title);
        fields.TryGetValue("producer", out var producer);

        signal = new MbyCliSignal(
            Version: version,
            Event: evt.Trim(),
            Id: id.Trim(),
            TimestampUtc: ts,
            WorkspaceId: string.IsNullOrWhiteSpace(wsId) ? null : wsId.Trim(),
            Path: string.IsNullOrWhiteSpace(path) ? null : path.Trim(),
            Title: string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
            Producer: string.IsNullOrWhiteSpace(producer) ? null : producer.Trim(),
            RawFields: fields);
        return true;
    }
}
