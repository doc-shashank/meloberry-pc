using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Infrastructure.Workspace;
using Xunit;

namespace Meloberry.Wrapper.ArchitectureTests;

public sealed class WrapperArchitectureTests
{
    [Fact]
    public void LayerDependencies_FollowStrictCleanArchitectureRules()
    {
        var domainAsm = typeof(Result).Assembly;
        var appAsm = typeof(IWrapperMetadataProvider).Assembly;
        var infraAsm = typeof(ManagedAppsStore).Assembly;
        var desktopAsm = typeof(Meloberry.Wrapper.Desktop.Win32.Win32WindowHost).Assembly;

        var domainRefs = domainAsm.GetReferencedAssemblies().Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(appAsm.GetName().Name, domainRefs);
        Assert.DoesNotContain(infraAsm.GetName().Name, domainRefs);
        Assert.DoesNotContain(desktopAsm.GetName().Name, domainRefs);

        // Domain must stay zero third-party packages.
        Assert.All(domainRefs, name => Assert.StartsWith("System.", name ?? string.Empty, StringComparison.OrdinalIgnoreCase));

        var appRefs = appAsm.GetReferencedAssemblies().Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains(domainAsm.GetName().Name, appRefs);
        Assert.DoesNotContain(infraAsm.GetName().Name, appRefs);
        Assert.DoesNotContain(desktopAsm.GetName().Name, appRefs);

        var infraRefs = infraAsm.GetReferencedAssemblies().Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains(appAsm.GetName().Name, infraRefs);
        Assert.DoesNotContain(desktopAsm.GetName().Name, infraRefs);
    }

    [Fact]
    public void AllAssemblies_ContainZeroRedundantFrameworksAndUnifiedVersion()
    {
        var assemblies = new[]
        {
            typeof(Result).Assembly,
            typeof(IWrapperMetadataProvider).Assembly,
            typeof(ManagedAppsStore).Assembly,
            typeof(Meloberry.Wrapper.Desktop.Win32.Win32WindowHost).Assembly
        };

        var bannedPrefixes = new[]
        {
            "Microsoft.WindowsAppSDK",
            "PresentationFramework",
            "System.Windows.Forms",
            "Microsoft.AspNetCore",
            "Microsoft.ML",
            "Microsoft.Data.Sqlite"
        };

        var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asm in assemblies)
        {
            var meta = new AssemblyWrapperMetadataProvider(asm).GetMetadata();
            versions.Add(meta.Version);
            Assert.Equal("mby-pc", meta.ExecutableName);
            Assert.Equal("doc-shashank", meta.SelfUpdateRepoOwner);
            Assert.Equal("meloberry-pc", meta.SelfUpdateRepoName);

            foreach (var r in asm.GetReferencedAssemblies())
            {
                var name = r.Name ?? string.Empty;
                Assert.DoesNotContain(bannedPrefixes, banned => name.StartsWith(banned, StringComparison.OrdinalIgnoreCase));
            }
        }

        Assert.Single(versions);
        Assert.Equal("1.0.0", versions.Single());
    }
}
