using System.Text.Json;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Infrastructure.Persistence;

/// <summary>Atomic JSON store for wrapper-settings.json.</summary>
public sealed class JsonSettingsStore(IWrapperDataPaths paths) : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<WrapperSettings> LoadAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(paths.SettingsFilePath))
            {
                return new WrapperSettings();
            }

            await using var fs = File.OpenRead(paths.SettingsFilePath);
            var loaded = await JsonSerializer.DeserializeAsync<WrapperSettings>(fs, JsonOptions, ct).ConfigureAwait(false);
            return loaded ?? new WrapperSettings();
        }
        catch
        {
            return new WrapperSettings();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Result> SaveAsync(WrapperSettings settings, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(paths.BaseDirectory);
            var tmp = paths.SettingsFilePath + ".tmp";
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
            File.Move(tmp, paths.SettingsFilePath, overwrite: true);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(WrapperErrors.IoFailure($"Failed to save wrapper settings: {ex.Message}"));
        }
        finally
        {
            _lock.Release();
        }
    }
}

/// <summary>Persists captured WebApp POST /api/analysis runs into &lt;webDir&gt;/reports/.</summary>
public sealed class WebAppAnalysisStore : IWebAppAnalysisStore
{
    public async Task<Result<string>> SaveAnalysisAsync(string webAppDir, string? reportJson, string? markdown, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(webAppDir) || !Directory.Exists(webAppDir))
        {
            return Result<string>.Failure(WrapperErrors.InvalidWebAppDir(webAppDir));
        }

        if (string.IsNullOrWhiteSpace(reportJson) && string.IsNullOrWhiteSpace(markdown))
        {
            return Result<string>.Failure(WrapperErrors.InvalidPromptDraft("Analysis payload was empty."));
        }

        try
        {
            var reportsDir = Path.Combine(Path.GetFullPath(webAppDir), "reports");
            Directory.CreateDirectory(reportsDir);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
            var jsonPath = Path.Combine(reportsDir, $"{stamp}.analysis.json");

            if (!string.IsNullOrWhiteSpace(reportJson))
            {
                await File.WriteAllTextAsync(jsonPath, reportJson, ct).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(markdown))
            {
                var mdPath = Path.Combine(reportsDir, $"{stamp}.analysis.md");
                await File.WriteAllTextAsync(mdPath, markdown, ct).ConfigureAwait(false);
            }

            return Result<string>.Success(jsonPath);
        }
        catch (Exception ex)
        {
            return Result<string>.Failure(WrapperErrors.IoFailure($"Failed to persist WebApp analysis: {ex.Message}"));
        }
    }

    public int CountReports(string webAppDir)
    {
        try
        {
            var reportsDir = Path.Combine(webAppDir, "reports");
            return Directory.Exists(reportsDir)
                ? Directory.GetFiles(reportsDir, "*.analysis.json", SearchOption.TopDirectoryOnly).Length
                : 0;
        }
        catch
        {
            return 0;
        }
    }
}
