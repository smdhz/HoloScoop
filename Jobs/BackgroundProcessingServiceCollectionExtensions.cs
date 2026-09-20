using HoloScoop.Services.Media;

namespace HoloScoop.Jobs;

public static class BackgroundProcessingServiceCollectionExtensions
{
    public static IServiceCollection AddHoloScoopBackgroundProcessing(
        this IServiceCollection services)
    {
        services.AddScoped<ITaskStateMachine, TaskStateMachine>();
        services.AddHostedService<MediaTaskWorker>();
        services.AddHostedService<QueuedTaskRepairService>();
        services.AddHostedService<RedisCandidateSyncService>();
        return services;
    }
}
