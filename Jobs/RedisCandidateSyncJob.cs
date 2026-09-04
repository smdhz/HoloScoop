using HoloScoop.Services.Redis;
using Quartz;

namespace HoloScoop.Jobs;

[DisallowConcurrentExecution]
public sealed class RedisCandidateSyncJob(
    IRedisCandidateSynchronizer synchronizer,
    ILogger<RedisCandidateSyncJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        var result = await synchronizer.SynchronizeAllAsync(context.CancellationToken);

        if (result.Activated > 0 || result.Expired > 0)
        {
            logger.LogInformation(
                "Synchronized Redis candidate state: {Activated} activated, {Expired} expired.",
                result.Activated,
                result.Expired);
        }
    }
}
