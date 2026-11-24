using System.Reflection;

namespace Meloberry.Wrapper.Application.Abstractions;

/// <summary>Unified build/version metadata surfaced from the single-source-of-truth props.</summary>
public sealed record WrapperMetadata(
    string ProductName,
    string AppName,
    string ExecutableName,
    string Version,
    string SelfUpdateRepoOwner,
    string SelfUpdateRepoName);

/// <summary>Provides compiled wrapper assembly metadata.</summary>
public interface IWrapperMetadataProvider
{
    WrapperMetadata GetMetadata();
}

/// <summary>Reads <see cref="WrapperMetadata"/> from assembly attributes compiled from Directory.Build.props.</summary>
public sealed class AssemblyWrapperMetadataProvider(Assembly assembly) : IWrapperMetadataProvider
{
    private readonly Assembly _assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));

    public WrapperMetadata GetMetadata()
    {
        var product = _assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "Meloberry PC";
        var rawVer = _assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? _assembly.GetName().Version?.ToString(3)
            ?? "1.0.0";
        var plusIdx = rawVer.IndexOf('+');
        var version = plusIdx > 0 ? rawVer[..plusIdx] : rawVer;

        var meta = _assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(m => m.Key, m => m.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);

        var appName = meta.GetValueOrDefault("AppName", "Meloberry");
        var exeName = meta.GetValueOrDefault("ExecutableName", "mby-pc");
        var owner = meta.GetValueOrDefault("SelfUpdateRepoOwner", "doc-shashank");
        var repo = meta.GetValueOrDefault("SelfUpdateRepoName", "meloberry-pc");

        return new WrapperMetadata(product, appName, exeName, version, owner, repo);
    }
}
