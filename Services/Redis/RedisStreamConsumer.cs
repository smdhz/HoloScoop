using Microsoft.Extensions.Options;
using HoloScoop.Services.Note;
using StackExchange.Redis;

namespace HoloScoop.Services.Redis;

public sealed class RedisStreamConsumer(
    IConnectionMultiplexer redis,
    IServiceScopeFactory scopeFactory,
    IOptions<RedisStreamOptions> options,
    ILogger<RedisStreamConsumer> logger) : BackgroundService
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
                logger.LogWarning(exception, "Could not initialize the Redis consumer group; retrying.");
                await Task.Delay(_options.PollDelayMilliseconds, stoppingToken).ConfigureAwait(false);
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var recovered = await RecoverStalePendingAsync(database, stoppingToken).ConfigureAwait(false);
                var entries = recovered.Length > 0
                    ? recovered
                    : await database.StreamReadGroupAsync(
                        _options.StreamName,
                        _options.ConsumerGroup,
                        _options.ConsumerName,
                        ">",
                        _options.BatchSize,
                        noAck: false).ConfigureAwait(false);

                if (entries.Length == 0)
                {
                    await Task.Delay(_options.PollDelayMilliseconds, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                foreach (var entry in entries)
                    await PersistAndAcknowledgeAsync(database, entry, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (RedisConnectionException exception)
            {
                logger.LogWarning(exception, "Redis stream connection failed; retrying.");
                await Task.Delay(_options.PollDelayMilliseconds, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Redis stream polling iteration failed; retrying.");
                await Task.Delay(_options.PollDelayMilliseconds, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task EnsureConsumerGroupAsync(IDatabase database)
    {
        try
        {
            await database.StreamCreateConsumerGroupAsync(
                _options.StreamName,
                _options.ConsumerGroup,
                _options.GroupStartPosition,
                createStream: true).ConfigureAwait(false);
        }
        catch (RedisServerException exception) when (exception.Message.Contains("BUSYGROUP", StringComparison.OrdinalIgnoreCase))
        {
            // Expected once the group already exists.
        }
    }

    private async Task<StreamEntry[]> RecoverStalePendingAsync(IDatabase database, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var summary = await database.StreamPendingAsync(_options.StreamName, _options.ConsumerGroup).ConfigureAwait(false);
        if (summary.PendingMessageCount == 0) return [];

        var ids = new List<RedisValue>(_options.BatchSize);
        foreach (var consumer in summary.Consumers)
        {
            if (ids.Count >= _options.BatchSize) break;
            var pending = await database.StreamPendingMessagesAsync(
                _options.StreamName,
                _options.ConsumerGroup,
                _options.BatchSize - ids.Count,
                consumer.Name,
                "-",
                "+").ConfigureAwait(false);

            ids.AddRange(pending
                .Where(item => item.IdleTimeInMilliseconds >= _options.PendingIdleMilliseconds)
                .Select(item => item.MessageId));
        }

        if (ids.Count == 0) return [];
        return await database.StreamClaimAsync(
            _options.StreamName,
            _options.ConsumerGroup,
            _options.ConsumerName,
            _options.PendingIdleMilliseconds,
            ids.ToArray()).ConfigureAwait(false);
    }

    private async Task PersistAndAcknowledgeAsync(
        IDatabase database,
        StreamEntry entry,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        if (!TryGetNoteScheduleId(entry.Values, out var scheduleId))
        {
            logger.LogError(
                "Redis message {Stream}/{MessageId} must contain exactly one valid Note schedule id field.",
                _options.StreamName,
                entry.Id);
            return;
        }

        var lookup = scope.ServiceProvider.GetRequiredService<INoteScheduleLookup>();
        var message = await lookup.FindAsync(scheduleId, cancellationToken).ConfigureAwait(false);
        if (message is null)
        {
            logger.LogError(
                "Redis message {Stream}/{MessageId} references missing Note.dbo.HololiveSchedule row {ScheduleId}.",
                _options.StreamName,
                entry.Id,
                scheduleId);
            return;
        }

        var store = scope.ServiceProvider.GetRequiredService<IIncomingTaskStore>();
        var result = await store.SaveCandidateAsync(
            _options.StreamName,
            entry.Id.ToString(),
            message,
            cancellationToken).ConfigureAwait(false);

        // The unique Redis stream/message key makes AlreadyExists safe to acknowledge.
        await database.StreamAcknowledgeAsync(_options.StreamName, _options.ConsumerGroup, entry.Id).ConfigureAwait(false);
        logger.LogInformation("Redis message {MessageId} persisted ({Result}) and acknowledged.", entry.Id, result);
    }

    private static bool TryGetNoteScheduleId(NameValueEntry[] values, out Guid scheduleId)
    {
        scheduleId = Guid.Empty;
        var idFields = values.Where(value => value.Name.ToString() == "id").ToArray();
        return idFields.Length == 1 && Guid.TryParse(idFields[0].Value.ToString(), out scheduleId);
    }
}
