using System.Text.Json;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;
using Meloberry.Wrapper.Infrastructure.Common;

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
            return await LoadUnlockedAsync(ct).ConfigureAwait(false);
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
            return await SaveUnlockedAsync(settings, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task<Result<WrapperSettings>> UpdateAsync(
        Func<WrapperSettings, WrapperSettings> mutator,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutator);
        return UpdateAsync(current => Result<WrapperSettings>.Success(mutator(current)), ct);
    }

    public async Task<Result<WrapperSettings>> UpdateAsync(
        Func<WrapperSettings, Result<WrapperSettings>> mutator,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutator);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = await LoadUnlockedAsync(ct).ConfigureAwait(false);
            var mutated = mutator(current);
            if (mutated.IsFailure)
            {
                return mutated;
            }

            var saveRes = await SaveUnlockedAsync(mutated.Value, ct).ConfigureAwait(false);
            return saveRes.IsSuccess
                ? Result<WrapperSettings>.Success(mutated.Value)
                : Result<WrapperSettings>.Failure(saveRes.Error!);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<WrapperSettings> LoadUnlockedAsync(CancellationToken ct)
    {
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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new WrapperSettings();
        }
    }

    private async Task<Result> SaveUnlockedAsync(WrapperSettings settings, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            await AtomicFileOps.WriteAllTextAsync(paths.SettingsFilePath, json, ct).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result.Failure(WrapperErrors.IoFailure($"Failed to save wrapper settings: {ex.Message}"));
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
            var mdPath = Path.Combine(reportsDir, $"{stamp}.analysis.md");
            if (File.Exists(jsonPath) || File.Exists(mdPath))
            {
                var uniqueStamp = $"{stamp}_{Guid.NewGuid().ToString("N")[..6]}";
                jsonPath = Path.Combine(reportsDir, $"{uniqueStamp}.analysis.json");
                mdPath = Path.Combine(reportsDir, $"{uniqueStamp}.analysis.md");
            }

            string? savedPath = null;
            if (!string.IsNullOrWhiteSpace(reportJson))
            {
                await File.WriteAllTextAsync(jsonPath, reportJson, ct).ConfigureAwait(false);
                savedPath = jsonPath;
            }

            if (!string.IsNullOrWhiteSpace(markdown))
            {
                await File.WriteAllTextAsync(mdPath, markdown, ct).ConfigureAwait(false);
                savedPath ??= mdPath;
            }

            return Result<string>.Success(savedPath!);
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
            if (!Directory.Exists(reportsDir))
            {
                return 0;
            }

            var stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(reportsDir, "*.analysis.json", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(file);
                stems.Add(name[..^".analysis.json".Length]);
            }

            foreach (var file in Directory.EnumerateFiles(reportsDir, "*.analysis.md", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(file);
                stems.Add(name[..^".analysis.md".Length]);
            }

            return stems.Count;
        }
        catch
        {
            return 0;
        }
    }
}
