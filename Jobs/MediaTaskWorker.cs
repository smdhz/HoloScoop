using HoloScoop.Data;
using HoloScoop.Services.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using MediaTaskStatus = HoloScoop.Data.Entities.TaskStatus;

namespace HoloScoop.Jobs;

public sealed class MediaTaskWorker(
    IConnectionMultiplexer redis,
    IServiceScopeFactory scopeFactory,
    IOptions<RedisStreamOptions> options,
    ILogger<MediaTaskWorker> logger) : BackgroundService
{
    private readonly RedisStreamOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _options.Validate();
        var database = redis.GetDatabase();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnsureConsumerGroupAsync(database).ConfigureAwait(false);
                break;
            }
            catch (RedisException exception)
            {
                logger.LogWarning(exception, "Could not initialize the media task consumer group; retrying.");
                await Task.Delay(_options.PollDelayMilliseconds, stoppingToken).ConfigureAwait(false);
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var recovered = await database.StreamAutoClaimAsync(
                    _options.ProcessingStreamName,
                    _options.ProcessingConsumerGroup,
                    _options.ConsumerName,
                    _options.ProcessingPendingIdleMilliseconds,
                    "0-0",
                    _options.BatchSize).ConfigureAwait(false);
                if (recovered.ClaimedEntries.Length > 0)
                {
                    foreach (var entry in recovered.ClaimedEntries)
                        await ProcessAndAcknowledgeAsync(database, entry, recoverInterrupted: true, stoppingToken)
                            .ConfigureAwait(false);
                    continue;
                }

                var entries = await database.StreamReadGroupAsync(
                        _options.ProcessingStreamName,
                        _options.ProcessingConsumerGroup,
                        _options.ConsumerName,
                        ">",
                        _options.BatchSize,
                        false,
                        TimeSpan.FromMilliseconds(_options.BlockMilliseconds),
                        CommandFlags.None).ConfigureAwait(false);

                foreach (var entry in entries)
                    await ProcessAndAcknowledgeAsync(database, entry, recoverInterrupted: false, stoppingToken)
                        .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (RedisException exception)
            {
                logger.LogWarning(exception, "Redis media task stream failed; retrying.");
                await Task.Delay(_options.PollDelayMilliseconds, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Media task worker iteration failed; retrying.");
                await Task.Delay(_options.PollDelayMilliseconds, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task EnsureConsumerGroupAsync(IDatabase database)
    {
        try
        {
            await database.StreamCreateConsumerGroupAsync(
                _options.ProcessingStreamName,
                _options.ProcessingConsumerGroup,
                "0-0",
                createStream: true).ConfigureAwait(false);
        }
        catch (RedisServerException exception) when (
            exception.Message.Contains("BUSYGROUP", StringComparison.OrdinalIgnoreCase))
        {
            // The durable consumer group already exists.
        }
    }

    private async Task ProcessAndAcknowledgeAsync(
        IDatabase database,
        StreamEntry entry,
        bool recoverInterrupted,
        CancellationToken cancellationToken)
    {
        if (!TryGetTaskId(entry.Values, out var taskId))
        {
            logger.LogError("Redis media task message {MessageId} has no valid taskId.", entry.Id);
            await AcknowledgeAsync(database, entry.Id).ConfigureAwait(false);
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<HoloScoopDbContext>();
        var now = DateTimeOffset.UtcNow;
        if (recoverInterrupted)
        {
            await dbContext.Tasks
                .Where(task => task.Id == taskId &&
                    (task.Status == MediaTaskStatus.Downloading ||
                     task.Status == MediaTaskStatus.ParsingSubtitles ||
                     task.Status == MediaTaskStatus.Diarizing ||
                     task.Status == MediaTaskStatus.Indexing))
                .ExecuteUpdateAsync(update => update
                    .SetProperty(task => task.Status, MediaTaskStatus.Queued)
                    .SetProperty(task => task.LastError, (string?)null)
                    .SetProperty(task => task.UpdatedAt, now),
                    cancellationToken).ConfigureAwait(false);
        }

        var claimed = await dbContext.Tasks
            .Where(task => task.Id == taskId && task.Status == MediaTaskStatus.Queued)
            .ExecuteUpdateAsync(update => update
                .SetProperty(task => task.Status, MediaTaskStatus.Downloading)
                .SetProperty(task => task.AttemptCount, task => task.AttemptCount + 1)
                .SetProperty(task => task.LastError, (string?)null)
                .SetProperty(task => task.UpdatedAt, now),
                cancellationToken).ConfigureAwait(false);

        if (claimed == 0)
        {
            await AcknowledgeAsync(database, entry.Id).ConfigureAwait(false);
            return;
        }

        try
        {
            var processor = scope.ServiceProvider.GetRequiredService<IMediaTaskProcessor>();
            await processor.ProcessAsync(taskId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Media task {TaskId} failed.", taskId);
            await MarkFailedAsync(dbContext, taskId, exception, cancellationToken).ConfigureAwait(false);
        }

        await AcknowledgeAsync(database, entry.Id).ConfigureAwait(false);
    }

    private Task<long> AcknowledgeAsync(IDatabase database, RedisValue messageId) =>
        database.StreamAcknowledgeAsync(
            _options.ProcessingStreamName,
            _options.ProcessingConsumerGroup,
            messageId);

    private static Task<int> MarkFailedAsync(
        HoloScoopDbContext dbContext,
        long taskId,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var error = exception.ToString();
        if (error.Length > 16_000) error = error[..16_000];
        var now = DateTimeOffset.UtcNow;
        return dbContext.Tasks
            .Where(task => task.Id == taskId && task.Status != MediaTaskStatus.Completed)
            .ExecuteUpdateAsync(update => update
                .SetProperty(task => task.Status, MediaTaskStatus.Failed)
                .SetProperty(task => task.LastError, error)
                .SetProperty(task => task.UpdatedAt, now),
                cancellationToken);
    }

    private static bool TryGetTaskId(NameValueEntry[] values, out long taskId)
    {
        taskId = 0;
        var fields = values.Where(value => value.Name == "taskId").ToArray();
        return fields.Length == 1 && long.TryParse(fields[0].Value.ToString(), out taskId) && taskId > 0;
    }
}
