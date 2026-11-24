using Meloberry.Wrapper.Application.UseCases;
using Meloberry.Wrapper.Domain.Models;
using Meloberry.Wrapper.Infrastructure.Cli;
using Meloberry.Wrapper.Infrastructure.Persistence;
using Meloberry.Wrapper.Infrastructure.Workspace;
using Xunit;

namespace Meloberry.Wrapper.UnitTests;

public sealed class SettingsAndCliTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"mby_cfg_test_{Guid.NewGuid():N}");

    [Fact]
    public async Task JsonSettingsStore_AndPresets_RoundTripCleanly()
    {
        var paths = new WrapperDataPaths(_tempRoot);
        var store = new JsonSettingsStore(paths);
        var handler = new SettingsAndPresetsHandler(store);

        var updated = await handler.UpdateSettingsAsync("dark", null, null, @"C:\Builds", "custom-owner", "custom-pc");
        Assert.True(updated.IsSuccess);
        Assert.Equal("dark", updated.Value.ThemeMode);
        Assert.True(updated.Value.OverrideWebAppTheme);

        var toggledOff = await handler.UpdateOverrideWebAppThemeAsync(false);
        Assert.True(toggledOff.IsSuccess);
        Assert.False(toggledOff.Value.OverrideWebAppTheme);

        var preserved = await handler.UpdateSettingsAsync("paper", null, null, @"C:\Builds", "custom-owner", "custom-pc");
        Assert.True(preserved.IsSuccess);
        Assert.Equal("paper", preserved.Value.ThemeMode);
        Assert.False(preserved.Value.OverrideWebAppTheme);

        var custom = await handler.SaveCustomPresetAsync(new PromptPreset(
            "",
            "My Drill",
            "Notes",
            ContainerKind.StandaloneQuiz,
            AuthoringFormat.Json,
            [new ShoppingListSelection("flashcard", true, 10)]));
        Assert.True(custom.IsSuccess);

        var allPresets = await handler.GetAllPresetsAsync();
        Assert.Contains(allPresets, p => p.Name == "My Drill" && !p.IsBuiltIn);

        Assert.True((await handler.DeleteCustomPresetAsync(custom.Value.Id)).IsSuccess);
        Assert.DoesNotContain(await handler.GetAllPresetsAsync(), p => p.Name == "My Drill");
    }

    [Fact]
    public async Task LiveCliIntegration_ParsesMbyAndMelomgSpecsWhenBinariesPresent()
    {
        var mbyRepoExe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "CLI", "app", "mby-cli-2.0.0.exe"));
        if (File.Exists(mbyRepoExe))
        {
            var client = new ProcessMbyCliClient();
            var catalog = await client.GetSpecCatalogAsync(mbyRepoExe, null);
            Assert.True(catalog.IsSuccess);
            Assert.Contains(catalog.Value, s => s.Topic == "mcq");
            Assert.Contains(catalog.Value, s => s.Topic == "table");
        }

        var melomgRepoExe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "toolings", "melomg", "app", "meloberry-melomg-2.2.0.exe"));
        if (File.Exists(melomgRepoExe))
        {
            var melomgClient = new ProcessMelomgCliClient();
            var specs = await melomgClient.GetSpecTextAsync(melomgRepoExe, null, ["extract", "mask"]);
            Assert.True(specs.IsSuccess);
            Assert.Contains("melomg extract", specs.Value);
            Assert.Contains("melomg mask", specs.Value);
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
