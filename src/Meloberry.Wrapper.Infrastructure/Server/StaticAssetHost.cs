using System.Net;
using System.Text;
using Meloberry.Wrapper.Application.Abstractions;

namespace Meloberry.Wrapper.Infrastructure.Server;

/// <summary>Serves embedded Shell HTML, bundled CSS, native ES modules, SVG icons, and isolated WebApps.</summary>
public sealed class StaticAssetHost(
    IWrapperMetadataProvider metadataProvider,
    IWebAppMountRegistry mountRegistry)
{
    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "application/javascript; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".woff2"] = "font/woff2",
        [".woff"] = "font/woff",
        [".ttf"] = "font/ttf"
    };

    private static readonly string[] OrderedStyleSuffixes =
    [
        "WebAssets.styles.tokens.css",
        "WebAssets.styles.paper-theme.css",
        "WebAssets.styles.base.css",
        "WebAssets.styles.chrome.css",
        "WebAssets.styles.setup.css",
        "WebAssets.styles.studio.css",
        "WebAssets.styles.organize.css",
        "WebAssets.styles.viewer.css",
        "WebAssets.styles.overlays.css"
    ];

    private static readonly Lazy<IReadOnlyDictionary<string, string>> IconMapLazy = new(BuildEmbeddedIconMap);
    private static readonly Lazy<IReadOnlyDictionary<string, byte[]>> JsModulesLazy = new(BuildEmbeddedJsMap);
    private static readonly Lazy<byte[]> CombinedShellCssBytesLazy = new(() =>
    {
        var bundled = string.Join("\n\n", OrderedStyleSuffixes.Select(suffix => ReadEmbeddedText(suffix).TrimEnd()));
        return Encoding.UTF8.GetBytes($"{bundled}\n");
    });

    public static IReadOnlyDictionary<string, string> GetEmbeddedIconMapSnapshot() => IconMapLazy.Value;

    public async Task ServeShellHtmlAsync(HttpListenerResponse resp, string token, CancellationToken ct)
    {
        var meta = metadataProvider.GetMetadata();
        var html = ReadEmbeddedText("WebAssets.shell.html");
        html = html.Replace("__MBY_TOKEN_VALUE__", token, StringComparison.Ordinal)
            .Replace("__MBY_VERSION_VALUE__", WebUtility.HtmlEncode(meta.Version), StringComparison.Ordinal)
            .Replace("__MBY_PRODUCT_NAME__", WebUtility.HtmlEncode(meta.ProductName), StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(html);
        resp.ContentType = "text/html; charset=utf-8";
        resp.Headers["Cache-Control"] = "no-store";
        resp.Headers["X-Frame-Options"] = "DENY";
        resp.Headers["Content-Security-Policy"] = "frame-ancestors 'none'";
        resp.StatusCode = 200;
        await resp.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
        resp.Close();
    }

    public async Task ServeBundledCssAsync(HttpListenerResponse resp, CancellationToken ct)
    {
        var cssBytes = CombinedShellCssBytesLazy.Value;
        resp.ContentType = "text/css; charset=utf-8";
        resp.StatusCode = 200;
        await resp.OutputStream.WriteAsync(cssBytes, ct).ConfigureAwait(false);
        resp.Close();
    }

    public async Task ServeJsModuleAsync(HttpListenerResponse resp, string rawRelPath, bool isHead, CancellationToken ct)
    {
        var normalized = rawRelPath.Trim().TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.Contains("..", StringComparison.Ordinal) ||
            normalized.Contains('\\') ||
            !normalized.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
            !JsModulesLazy.Value.TryGetValue(normalized, out var bytes))
        {
            resp.StatusCode = 404;
            resp.Close();
            return;
        }

        resp.ContentType = "application/javascript; charset=utf-8";
        resp.Headers["Cache-Control"] = "no-store";
        resp.StatusCode = 200;
        if (!isHead)
        {
            await resp.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
        }

        resp.Close();
    }

    public Task ServeIconsJsonAsync(HttpListenerResponse resp, CancellationToken ct)
    {
        resp.Headers["Cache-Control"] = "public, max-age=3600";
        return HttpEnvelope.WriteJsonAsync(resp, 200, IconMapLazy.Value, ct);
    }

    public async Task ServeIconSvgAsync(HttpListenerResponse resp, string rawKey, CancellationToken ct)
    {
        var key = rawKey.Trim().TrimStart('/');
        if (key.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            key = key[..^4];
        }

        if (!IconMapLazy.Value.TryGetValue(key, out var svg))
        {
            resp.StatusCode = 404;
            resp.Close();
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(svg);
        resp.ContentType = "image/svg+xml; charset=utf-8";
        resp.Headers["Cache-Control"] = "public, max-age=3600";
        resp.StatusCode = 200;
        await resp.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
        resp.Close();
    }

    public async Task ServeMountedWebAppAsync(
        HttpListenerResponse resp,
        string path,
        int shellPort,
        bool isHead,
        CancellationToken ct)
    {
        _ = shellPort;
        var trimmed = path["/viewer/".Length..];
        var slash = trimmed.IndexOf('/');
        if (slash <= 0)
        {
            resp.StatusCode = 404;
            resp.Close();
            return;
        }

        var slotId = trimmed[..slash];
        var relPath = trimmed[(slash + 1)..];
        if (string.IsNullOrWhiteSpace(relPath))
        {
            relPath = "index.html";
        }

        if (!mountRegistry.TryResolve(slotId, out var webDir))
        {
            resp.StatusCode = 404;
            resp.Close();
            return;
        }

        string fullRoot;
        string target;
        try
        {
            var decoded = Uri.UnescapeDataString(relPath).Replace('/', Path.DirectorySeparatorChar);
            fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(webDir)) + Path.DirectorySeparatorChar;
            target = Path.GetFullPath(Path.Combine(fullRoot, decoded));
        }
        catch
        {
            resp.StatusCode = 404;
            resp.Close();
            return;
        }

        if (!target.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(target))
        {
            resp.StatusCode = 404;
            resp.Close();
            return;
        }

        var ext = Path.GetExtension(target);
        resp.ContentType = MimeTypes.GetValueOrDefault(ext, "application/octet-stream");
        resp.Headers["Cache-Control"] = "no-store";
        resp.StatusCode = 200;

        if (isHead)
        {
            resp.Close();
            return;
        }

        await using var fs = File.OpenRead(target);
        await fs.CopyToAsync(resp.OutputStream, ct).ConfigureAwait(false);
        resp.Close();
    }

    private static IReadOnlyDictionary<string, string> BuildEmbeddedIconMap()
    {
        var asm = typeof(StaticAssetHost).Assembly;
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        const string Marker = ".WebAssets.icons.";

        foreach (var resName in asm.GetManifestResourceNames())
        {
            var idx = resName.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0 || !resName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var tail = resName[(idx + Marker.Length)..^4];
            var dot = tail.IndexOf('.');
            if (dot <= 0)
            {
                continue;
            }

            var key = string.Concat(tail.AsSpan(0, dot), "/", tail.AsSpan(dot + 1));
            using var stream = asm.GetManifestResourceStream(resName);
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            dict[key] = reader.ReadToEnd().Trim();
        }

        return dict;
    }

    private static IReadOnlyDictionary<string, byte[]> BuildEmbeddedJsMap()
    {
        var asm = typeof(StaticAssetHost).Assembly;
        var dict = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        const string Marker = ".WebAssets.js.";

        foreach (var resName in asm.GetManifestResourceNames())
        {
            var idx = resName.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0 || !resName.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var stem = resName[(idx + Marker.Length)..^3];
            var relPath = stem.Replace('.', '/') + ".js";
            using var stream = asm.GetManifestResourceStream(resName);
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            dict[relPath] = Encoding.UTF8.GetBytes(reader.ReadToEnd());
        }

        return dict;
    }

    private static string ReadEmbeddedText(string suffix)
    {
        var asm = typeof(StaticAssetHost).Assembly;
        var resName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Missing embedded WebAsset '{suffix}'.");

        using var stream = asm.GetManifestResourceStream(resName)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
