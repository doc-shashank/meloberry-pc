using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Application.UseCases;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Models;
using Meloberry.Wrapper.Domain.Services;
using Meloberry.Wrapper.Infrastructure.Persistence;
using Meloberry.Wrapper.Infrastructure.Workspace;
using Xunit;

namespace Meloberry.Wrapper.UnitTests;

public sealed class MelomgGuiExtractionTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"mby_melomg_gui_{Guid.NewGuid():N}");

    [Theory]
    [InlineData("Figure 1.1", "1.1")]
    [InlineData("Fig. 1.1a", "1.1a")]
    [InlineData("fig 2.3", "2.3")]
    [InlineData("Diagram 3.2", "3.2")]
    [InlineData("Chart 4", "4")]
    [InlineData("#1.1", "1.1")]
    [InlineData("f1", "f1")]
    [InlineData("p4", "p4")]
    [InlineData("n:cell membrane", "n:cell membrane")]
    [InlineData("all", "all")]
    public void MelomgFigureQueryNormalizer_MapsNaturalLanguageAndPreservesSelectors(string raw, string expected)
    {
        Assert.Equal(expected, MelomgFigureQueryNormalizer.Normalize(raw));
    }

    [Fact]
    public async Task ExtractFigureWithMelomgHandler_ReturnsCliNotFound_ReusesExistingDoc_AndFallsBackToOneShot()
    {
        var paths = new WrapperDataPaths(Path.Combine(_tempRoot, "melomg_handler"));
        var ws = Path.Combine(_tempRoot, "ws_fig");
        Directory.CreateDirectory(ws);
        var pdfPath = Path.Combine(ws, "Anatomy_Atlas.pdf");
        await File.WriteAllBytesAsync(pdfPath, [0x25, 0x50, 0x44, 0x46]);

        var settingsStore = new JsonSettingsStore(paths);
        await settingsStore.SaveAsync(new WrapperSettings(ActiveWorkspace: new ActiveWorkspaceState(ws, true)));
        var appsStore = new ManagedAppsStore(paths);
        var stubMelomg = new StubMelomgCliClient(ws);
        var handler = new ExtractFigureWithMelomgHandler(appsStore, stubMelomg, settingsStore);

        var missingRes = await handler.ExtractAsync(new ExtractFigureRequest(ws, pdfPath, "Figure 1.1"));
        Assert.True(missingRes.IsFailure);
        Assert.Equal("Cli.NotFound", missingRes.Error!.Code);

        Directory.CreateDirectory(paths.AppsDirectory);
        await File.WriteAllBytesAsync(Path.Combine(paths.AppsDirectory, "meloberry-melomg-2.2.1.exe"), [(byte)'M', (byte)'Z', 1]);
        stubMelomg.Documents = [new MelomgDocumentSummary("Doc_123", "Anatomy_Atlas", 20, 8, Path.Combine(ws, "Doc_123"))];

        var reuseRes = await handler.ExtractAsync(new ExtractFigureRequest(ws, pdfPath, "Figure 1.1", Quality: 90, MaskAll: true, EmitMeta: true));
        Assert.True(reuseRes.IsSuccess);
        Assert.Equal("Doc_123", stubMelomg.LastTarget);
        Assert.Equal("1.1", stubMelomg.LastSelector);
        Assert.StartsWith("assets/", reuseRes.Value.RelativeImagePath);

        var pdf2 = Path.Combine(ws, "NewPaper.pdf");
        await File.WriteAllBytesAsync(pdf2, [0x25, 0x50, 0x44, 0x46]);
        var oneShotRes = await handler.ExtractAsync(new ExtractFigureRequest(ws, pdf2, "Diagram 2.4"));
        Assert.True(oneShotRes.IsSuccess);
        Assert.Equal(pdf2, stubMelomg.LastTarget);
        Assert.Equal("2.4", stubMelomg.LastSelector);
        Assert.True(stubMelomg.LastRetainDb);
    }

    private sealed class StubMelomgCliClient(string ws) : IMelomgCliClient
    {
        public IReadOnlyList<MelomgDocumentSummary> Documents { get; set; } = [];
        public string? LastTarget { get; private set; }
        public string? LastSelector { get; private set; }
        public bool LastRetainDb { get; private set; }

        public Task<Result<string>> GetSpecTextAsync(string exePath, string? workingDir, IReadOnlyList<string> topics, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Result<IReadOnlyList<MelomgDocumentSummary>>> ListDocumentsAsync(string exePath, string? workingDir, CancellationToken ct = default) =>
            Task.FromResult(Result<IReadOnlyList<MelomgDocumentSummary>>.Success(Documents));

        public Task<Result<MelomgExtractResult>> ExtractFigureAsync(
            string exePath, string targetOrPdf, string selector, string outputDir,
            int? quality, bool maskAll, bool emitMeta, bool retainDb, bool offline,
            string? workingDir, CancellationToken ct = default)
        {
            LastTarget = targetOrPdf;
            LastSelector = selector;
            LastRetainDb = retainDb;
            var outPath = Path.Combine(ws, "assets", $"fig_{selector}.png");
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.WriteAllBytes(outPath, [1, 2, 3]);
            return Task.FromResult(Result<MelomgExtractResult>.Success(new MelomgExtractResult(
                "Doc_123",
                outputDir,
                [new MelomgExtractedArtifact("Fig_1", selector, 1, $"Figure {selector}", outPath, emitMeta ? outPath + ".meta.json" : null, 800, 600)])));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
