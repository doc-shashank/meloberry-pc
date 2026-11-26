using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;
using Meloberry.Wrapper.Domain.Services;

namespace Meloberry.Wrapper.Application.UseCases;

/// <summary>Composes and saves Prompt Studio prompts.</summary>
public sealed class PromptHandlers(LoadSpecCatalogHandler specLoader, IAtomicFileWriter atomicFileWriter)
{
    public async Task<ComposedPrompt> ComposeAsync(PromptDraft draft, CancellationToken ct = default)
    {
        var (specs, _, _) = await specLoader.LoadAsync(false, ct).ConfigureAwait(false);
        return PromptComposer.Compose(draft, specs);
    }

    public async Task<Result<string>> SavePromptAsync(string filePath, string markdown, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return Result<string>.Failure(WrapperErrors.InvalidPath("Save file path cannot be empty."));
        }

        try
        {
            var full = Path.GetFullPath(filePath);
            await atomicFileWriter.WriteAllTextAsync(full, markdown, ct).ConfigureAwait(false);
            return Result<string>.Success(full);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result<string>.Failure(WrapperErrors.IoFailure($"Failed to save prompt: {ex.Message}"));
        }
    }
}
