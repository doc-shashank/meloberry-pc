using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Infrastructure.Cli;
using Meloberry.Wrapper.Infrastructure.GitHub;
using Meloberry.Wrapper.Infrastructure.Persistence;
using Meloberry.Wrapper.Infrastructure.Server;
using Meloberry.Wrapper.Infrastructure.Workspace;
using Microsoft.Extensions.DependencyInjection;

namespace Meloberry.Wrapper.Infrastructure;

/// <summary>Registers Infrastructure layer adapters and HTTP server.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddWrapperInfrastructure(this IServiceCollection services, string? customDataDir = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IWrapperDataPaths>(_ => new WrapperDataPaths(customDataDir));
        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.AddSingleton<IWebAppAnalysisStore, WebAppAnalysisStore>();
        services.AddSingleton<IManagedAppsStore, ManagedAppsStore>();
        services.AddSingleton<IWorkspaceInitializer, WorkspaceInitializer>();
        services.AddSingleton<IWorkspaceMaintenanceService, WorkspaceMaintenanceService>();
        services.AddSingleton<IWorkspaceFileWatcher, WorkspaceFileWatcherService>();
        services.AddSingleton<IAgentsMdFileWriter, AgentsMdFileWriter>();
        services.AddSingleton<IMbyCliClient, ProcessMbyCliClient>();
        services.AddSingleton<IMelomgCliClient, ProcessMelomgCliClient>();
        services.AddSingleton<GitHubHttpClientHelper>(sp =>
            new GitHubHttpClientHelper(sp.GetRequiredService<IWrapperMetadataProvider>()));
        services.AddSingleton<IGitHubToolDownloader, GitHubToolDownloader>();
        services.AddSingleton<IGitHubSelfUpdater, GitHubSelfUpdater>();
        services.AddSingleton<IWebAppMountRegistry, WebAppMountRegistry>();
        services.AddSingleton<LocalHttpWrapperServer>();

        return services;
    }
}
