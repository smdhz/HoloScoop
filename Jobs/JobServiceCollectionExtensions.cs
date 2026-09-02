using Quartz;

namespace HoloScoop.Jobs;

public static class JobServiceCollectionExtensions
{
    public static IServiceCollection AddHoloScoopJobScheduling(this IServiceCollection services)
    {
        services.AddScoped<ITaskStateMachine, TaskStateMachine>();
        services.AddQuartz(configurator =>
        {
            var expireKey = new JobKey("expire-candidates");
            configurator.AddJob<ExpireCandidateTasksJob>(options => options.WithIdentity(expireKey));
            configurator.AddTrigger(options => options
                .WithIdentity("expire-candidates-every-minute")
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
        });
        services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);
        return services;
    }
}
