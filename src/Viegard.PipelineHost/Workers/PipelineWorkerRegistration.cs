using Microsoft.Extensions.DependencyInjection.Extensions;
using Viegard.PipelineHost.Configuration;

namespace Viegard.PipelineHost.Workers;

public static class PipelineWorkerRegistration
{
    public static IServiceCollection AddMaintenanceWorkers(
        this IServiceCollection services,
        IEnumerable<string> configuredRoles)
    {
        if (configuredRoles.Contains(RoleNames.Maintenance, StringComparer.OrdinalIgnoreCase))
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, IngestionFilterSeedWorker>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, PolicyThresholdSeedWorker>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ClassifierSettingsSeedWorker>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, BurstDetectionSettingsSeedWorker>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, DohBlocklistSettingsSeedWorker>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, IncidentCoalescingSettingsSeedWorker>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, PolicyPostureSeedWorker>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, LocalModelAdvisorSeedWorker>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, LocalModelAdvisorPromptTemplateSeedWorker>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, RetentionWorker>());
        }

        return services;
    }
}
