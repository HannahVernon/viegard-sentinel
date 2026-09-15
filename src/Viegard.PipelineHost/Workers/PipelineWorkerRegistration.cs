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
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, RetentionWorker>());
        }

        return services;
    }
}
