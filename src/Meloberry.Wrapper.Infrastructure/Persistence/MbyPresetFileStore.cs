using System.Text.Json;
using System.Text.Json.Serialization;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Infrastructure.Persistence;

/// <summary>Atomic `.mbypreset` file store persisting workflow presets under presets\ and reading/writing portable component presets.</summary>
public sealed class MbyPresetFileStore(
    IWrapperDataPaths paths,
    IAtomicFileWriter atomicWriter) : IPresetFileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<IReadOnlyList<PromptPreset>> LoadWorkflowPresetsAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(paths.PresetsDirectory))
            {
                return [];
            }

            var list = new List<PromptPreset>();
            foreach (var file in Directory.EnumerateFiles(paths.PresetsDirectory, "*.mbypreset", SearchOption.TopDirectoryOnly)
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var parsed = await TryReadFileUnlockedAsync(file, ct).ConfigureAwait(false);
                if (parsed is { IsSuccess: true, Value: { Scope: PresetFileScope.Workflow, WorkflowPreset: { } wp } })
                {
                    list.Add(wp with { IsBuiltIn = false });
                }
            }

            return list;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Result<PromptPreset>> SaveWorkflowPresetAsync(PromptPreset preset, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var id = string.IsNullOrWhiteSpace(preset.Id) ? $"custom-{Guid.NewGuid():N}"[..16] : preset.Id.Trim();
        var clean = preset with { Id = id, IsBuiltIn = false };
        var envelope = new MbyPresetFile(
            FormatVersion: 1,
            Scope: PresetFileScope.Workflow,
            Name: clean.Name,
            Description: clean.Description,
            WorkflowPreset: clean);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(paths.PresetsDirectory);
            await DeleteByIdUnlockedAsync(id, ct).ConfigureAwait(false);

            var destPath = Path.Combine(paths.PresetsDirectory, $"{Slugify(clean.Name)}_{Slugify(id)}.mbypreset");
            var json = JsonSerializer.Serialize(envelope, JsonOptions);
            await atomicWriter.WriteAllTextAsync(destPath, json, ct).ConfigureAwait(false);
            return Result<PromptPreset>.Success(clean);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result<PromptPreset>.Failure(WrapperErrors.IoFailure($"Failed to save preset file: {ex.Message}"));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Result<(PromptPreset Preset, string SavedPath)>> SaveWorkflowPresetToFileAsync(
        string filePath,
        PromptPreset preset,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return Result<(PromptPreset, string)>.Failure(WrapperErrors.InvalidPresetFile("Target preset file path is empty."));
        }

        var id = string.IsNullOrWhiteSpace(preset.Id) ? $"custom-{Guid.NewGuid():N}"[..16] : preset.Id.Trim();
        var clean = preset with { Id = id, IsBuiltIn = false };
        var envelope = new MbyPresetFile(
            FormatVersion: 1,
            Scope: PresetFileScope.Workflow,
            Name: clean.Name,
            Description: clean.Description,
            WorkflowPreset: clean);

        var fullTarget = Path.GetFullPath(filePath);
        var targetDir = Path.GetDirectoryName(fullTarget);
        var savedInsideLibrary = PathNormalization.AreSameDirectory(targetDir, paths.PresetsDirectory);
        if (savedInsideLibrary)
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(paths.PresetsDirectory);
                await DeleteByIdUnlockedAsync(clean.Id, ct).ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }
        }

        var writeRes = await WritePresetFileAsync(fullTarget, envelope, ct).ConfigureAwait(false);
        if (writeRes.IsFailure)
        {
            return Result<(PromptPreset, string)>.Failure(writeRes.Error!);
        }

        if (!savedInsideLibrary)
        {
            var libRes = await SaveWorkflowPresetAsync(clean, ct).ConfigureAwait(false);
            if (libRes.IsFailure)
            {
                return Result<(PromptPreset, string)>.Failure(libRes.Error!);
            }
        }

        return Result<(PromptPreset, string)>.Success((clean, writeRes.Value));
    }

    public async Task<Result> DeleteWorkflowPresetAsync(string presetId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(presetId))
        {
            return Result.Failure(WrapperErrors.PresetNotFound(string.Empty));
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var removed = await DeleteByIdUnlockedAsync(presetId.Trim(), ct).ConfigureAwait(false);
            return removed ? Result.Success() : Result.Failure(WrapperErrors.PresetNotFound(presetId));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Result<string>> WritePresetFileAsync(string filePath, MbyPresetFile presetFile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(presetFile);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return Result<string>.Failure(WrapperErrors.InvalidPresetFile("Target preset file path is empty."));
        }

        try
        {
            var fullPath = Path.GetFullPath(filePath);
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(presetFile, JsonOptions);
            await atomicWriter.WriteAllTextAsync(fullPath, json, ct).ConfigureAwait(false);
            return Result<string>.Success(fullPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result<string>.Failure(WrapperErrors.IoFailure($"Failed to write preset file: {ex.Message}"));
        }
    }

    public Task<Result<MbyPresetFile>> ReadPresetFileAsync(string filePath, CancellationToken ct = default) =>
        TryReadFileUnlockedAsync(filePath, ct);

    private async Task<bool> DeleteByIdUnlockedAsync(string presetId, CancellationToken ct)
    {
        if (!Directory.Exists(paths.PresetsDirectory))
        {
            return false;
        }

        var removed = false;
        foreach (var file in Directory.EnumerateFiles(paths.PresetsDirectory, "*.mbypreset", SearchOption.TopDirectoryOnly))
        {
            var parsed = await TryReadFileUnlockedAsync(file, ct).ConfigureAwait(false);
            if (parsed is { IsSuccess: true, Value: { Scope: PresetFileScope.Workflow, WorkflowPreset: { } wp } } &&
                string.Equals(wp.Id, presetId, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(file);
                removed = true;
            }
        }

        return removed;
    }

    private static async Task<Result<MbyPresetFile>> TryReadFileUnlockedAsync(string filePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return Result<MbyPresetFile>.Failure(WrapperErrors.InvalidPresetFile($"Preset file '{filePath}' does not exist."));
        }

        try
        {
            var json = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize<MbyPresetFile>(json, JsonOptions);
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.Name))
            {
                return Result<MbyPresetFile>.Failure(WrapperErrors.InvalidPresetFile("Invalid or empty preset file."));
            }

            return Result<MbyPresetFile>.Success(parsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result<MbyPresetFile>.Failure(WrapperErrors.InvalidPresetFile($"Malformed preset file: {ex.Message}"));
        }
    }

    private static string Slugify(string raw)
    {
        var chars = (raw ?? "preset").ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '_')
            .ToArray();
        var collapsed = new string(chars);
        while (collapsed.Contains("__", StringComparison.Ordinal))
        {
            collapsed = collapsed.Replace("__", "_", StringComparison.Ordinal);
        }

        collapsed = collapsed.Trim('_');
        return string.IsNullOrWhiteSpace(collapsed) ? "preset" : (collapsed.Length > 32 ? collapsed[..32] : collapsed);
    }
}
