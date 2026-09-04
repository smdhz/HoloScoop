using HoloScoop.Services.Media;
using Quartz;

namespace HoloScoop.Jobs;

public static class JobServiceCollectionExtensions
{
    public static IServiceCollection AddHoloScoopJobScheduling(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<MaintenanceOptions>()
            .Bind(configuration.GetSection(MaintenanceOptions.SectionName))
            .Validate(
                options => options.UnselectedRetentionDays > 0 && options.CompletedRetentionDays > 0,
                "Maintenance retention periods must be positive.")
            .ValidateOnStart();
        services.AddScoped<ITaskStateMachine, TaskStateMachine>();
        services.AddQuartz(configurator =>
        {
            var expireKey = new JobKey("sync-redis-candidates");
            configurator.AddJob<ExpireCandidateTasksJob>(options => options.WithIdentity(expireKey));
            configurator.AddTrigger(options => options
                .WithIdentity("sync-redis-candidates-every-minute")
                .ForJob(expireKey)
                .StartNow()
                .WithSimpleSchedule(schedule => schedule.WithIntervalInMinutes(1).RepeatForever()));

            var queuedKey = new JobKey("process-queued-tasks");
            configurator.AddJob<QueuedTasksJob>(options => options.WithIdentity(queuedKey));
            configurator.AddTrigger(options => options
                .WithIdentity("process-queued-tasks-every-five-seconds")
                .ForJob(queuedKey)
                .StartNow()
                .WithSimpleSchedule(schedule => schedule.WithIntervalInSeconds(5).RepeatForever()));

            var cleanupKey = new JobKey("cleanup-expired-task-records");
            configurator.AddJob<CleanupUnselectedTasksJob>(options => options.WithIdentity(cleanupKey));
            configurator.AddTrigger(options => options
                .WithIdentity("cleanup-expired-task-records-daily")
                .ForJob(cleanupKey)
                .StartNow()
                .WithSimpleSchedule(schedule => schedule.WithIntervalInHours(24).RepeatForever()));

        });
        services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);
        return services;
    }
}
