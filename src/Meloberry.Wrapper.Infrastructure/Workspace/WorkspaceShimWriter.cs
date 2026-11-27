using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Infrastructure.Common;

namespace Meloberry.Wrapper.Infrastructure.Workspace;

/// <summary>Creates and cleans up the active-workspace-only `mby-pc.bat` CLI shim.</summary>
public sealed class WorkspaceShimWriter : IWorkspaceShimWriter
{
    public const string ShimFileName = "mby-pc.bat";
    public const string ManagedMarker = ":: Meloberry PC Workspace Shim";

    public string RenderShimContent() => """
        @echo off
        :: Meloberry PC Workspace Shim
        setlocal
        set "MBY_PC_EXE="
        if exist "%LOCALAPPDATA%\Meloberry\mby-pc-path.txt" (
          set /p MBY_PC_EXE=<"%LOCALAPPDATA%\Meloberry\mby-pc-path.txt"
        )
        if not defined MBY_PC_EXE set "MBY_PC_EXE=%LOCALAPPDATA%\Programs\Meloberry PC\mby-pc.exe"
        if not exist "%MBY_PC_EXE%" (
          echo {"success":false,"error":{"code":"Wrapper.ExecutableNotFound","description":"Meloberry PC executable not found. Launch Meloberry PC once or reinstall it."}} 1>&2
          exit /b 3
        )
        "%MBY_PC_EXE%" --cli --workspace "%~dp0." %*
        exit /b %ERRORLEVEL%
        """.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);

    public async Task<Result<string>> WriteShimAsync(
        string workspacePath,
        bool overwriteExisting = true,
        CancellationToken ct = default)
    {
        if (!PathNormalization.TryNormalizeDirectoryPath(workspacePath, out var fullWs))
        {
            return Result<string>.Failure(WrapperErrors.InvalidWorkspaceDir(workspacePath));
        }

        try
        {
            Directory.CreateDirectory(fullWs);
            var shimPath = Path.Combine(fullWs, ShimFileName);
            if (!overwriteExisting && File.Exists(shimPath))
            {
                return Result<string>.Success(shimPath);
            }

            if (File.Exists(shimPath))
            {
                var existing = await File.ReadAllTextAsync(shimPath, ct).ConfigureAwait(false);
                if (!existing.Contains(ManagedMarker, StringComparison.Ordinal) && !overwriteExisting)
                {
                    return Result<string>.Success(shimPath);
                }
            }

            await AtomicFileOps.WriteAllTextAsync(shimPath, RenderShimContent(), ct).ConfigureAwait(false);
            return Result<string>.Success(shimPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result<string>.Failure(WrapperErrors.IoFailure($"Failed to write workspace shim: {ex.Message}"));
        }
    }

    public async Task<Result<bool>> RemoveShimIfManagedAsync(string workspacePath, CancellationToken ct = default)
    {
        if (!PathNormalization.TryNormalizeDirectoryPath(workspacePath, out var fullWs))
        {
            return Result<bool>.Success(false);
        }

        try
        {
            var shimPath = Path.Combine(fullWs, ShimFileName);
            if (!File.Exists(shimPath))
            {
                return Result<bool>.Success(false);
            }

            var content = await File.ReadAllTextAsync(shimPath, ct).ConfigureAwait(false);
            if (!content.Contains(ManagedMarker, StringComparison.Ordinal))
            {
                return Result<bool>.Success(false);
            }

            File.Delete(shimPath);
            return Result<bool>.Success(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result<bool>.Failure(WrapperErrors.IoFailure($"Failed to remove workspace shim: {ex.Message}"));
        }
    }
}
