using System.Reflection;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Application.UseCases;
using Microsoft.Extensions.DependencyInjection;

namespace Meloberry.Wrapper.Application;

/// <summary>Registers Application layer services and assembly metadata provider.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddWrapperApplication(this IServiceCollection services, Assembly hostAssembly)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(hostAssembly);

        services.AddSingleton<IWrapperMetadataProvider>(_ => new AssemblyWrapperMetadataProvider(hostAssembly));
        services.AddSingleton<ManagedAppsHandler>();
        services.AddSingleton<WorkspaceSetupHandler>();
        services.AddSingleton<WorkspaceMaintenanceHandler>();
        services.AddSingleton<LoadSpecCatalogHandler>();
        services.AddSingleton<ReadyWorkspaceHandler>();
        services.AddSingleton<PromptHandlers>();
        services.AddSingleton<SessionTemplateHandler>();
        services.AddSingleton<ManageLibraryHandler>();
        services.AddSingleton<WorkspaceActionsHandler>();
        services.AddSingleton<CaptureAnalysisHandler>();
        services.AddSingleton<SelfUpdateHandler>();
        services.AddSingleton<SettingsAndPresetsHandler>();
        services.AddSingleton<ProcessCliSignalHandler>();
        services.AddSingleton<LiveDraftHandler>();
        services.AddSingleton<ImportWorkspaceAssetHandler>();
        services.AddSingleton<ExtractFigureWithMelomgHandler>();
        services.AddSingleton<FinalizeWorkspaceHandler>();

        return services;
    }
}
