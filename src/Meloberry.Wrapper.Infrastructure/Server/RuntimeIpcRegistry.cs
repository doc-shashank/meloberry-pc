using System.Diagnostics;
using System.Text.Json;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Infrastructure.Common;

namespace Meloberry.Wrapper.Infrastructure.Server;

/// <summary>Publishes and cleans up `%LOCALAPPDATA%\Meloberry\runtime-ipc.json` for active GUI discovery.</summary>
public sealed class RuntimeIpcRegistry(IWrapperDataPaths dataPaths) : IRuntimeIpcRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public async Task WriteCurrentAsync(int shellPort, int viewerPort, string token, CancellationToken ct = default)
    {
        var info = new RuntimeIpcInfo(
            Pid: Environment.ProcessId,
            ShellPort: shellPort,
            ViewerPort: viewerPort,
            Token: token,
            StartedUtc: DateTimeOffset.UtcNow);
        var json = JsonSerializer.Serialize(info, JsonOptions);
        await AtomicFileOps.WriteAllTextAsync(dataPaths.RuntimeIpcFilePath, json, ct).ConfigureAwait(false);
    }

    public RuntimeIpcInfo? TryReadActive()
    {
        try
        {
            if (!File.Exists(dataPaths.RuntimeIpcFilePath))
            {
                return null;
            }

            var json = File.ReadAllText(dataPaths.RuntimeIpcFilePath);
            var info = JsonSerializer.Deserialize<RuntimeIpcInfo>(json, JsonOptions);
            if (info is null || info.Pid <= 0 || info.ShellPort <= 0 || string.IsNullOrWhiteSpace(info.Token))
            {
                return null;
            }

            using var proc = Process.GetProcessById(info.Pid);
            return proc.HasExited ? null : info;
        }
        catch
        {
            return null;
        }
    }

    public void CleanupCurrent()
    {
        try
        {
            if (!File.Exists(dataPaths.RuntimeIpcFilePath))
            {
                return;
            }

            var json = File.ReadAllText(dataPaths.RuntimeIpcFilePath);
            var info = JsonSerializer.Deserialize<RuntimeIpcInfo>(json, JsonOptions);
            if (info is not null && info.Pid == Environment.ProcessId)
            {
                File.Delete(dataPaths.RuntimeIpcFilePath);
            }
        }
        catch
        {
            // Ignore cleanup race errors on shutdown.
        }
    }
}
