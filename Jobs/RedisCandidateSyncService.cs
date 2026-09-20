using HoloScoop.Services.Redis;
using Microsoft.Extensions.Options;

namespace HoloScoop.Jobs;

public sealed class RedisCandidateSyncService(
    IServiceScopeFactory scopeFactory,
    IOptions<RedisStreamOptions> options,
    ILogger<RedisCandidateSyncService> logger) : BackgroundService
{
    private readonly RedisStreamOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_options.CandidateSyncIntervalMinutes));
        do
        {
            try
            {
                await SynchronizeAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Redis candidate synchronization failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task SynchronizeAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var synchronizer = scope.ServiceProvider.GetRequiredService<IRedisCandidateSynchronizer>();
        var result = await synchronizer.SynchronizeAllAsync(cancellationToken).ConfigureAwait(false);
        if (result.Activated > 0 || result.Expired > 0)
        {
            logger.LogInformation(
                "Synchronized Redis candidate state: {Activated} activated, {Expired} expired.",
                result.Activated,
                result.Expired);
        }
    }
}
