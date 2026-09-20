using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace HoloScoop.Services.Redis;

public interface IMediaTaskQueue
{
    Task PublishAsync(long taskId, CancellationToken cancellationToken = default);
}

public sealed class RedisMediaTaskQueue(
    IConnectionMultiplexer redis,
    IOptions<RedisStreamOptions> options) : IMediaTaskQueue
{
    private readonly RedisStreamOptions _options = options.Value;

    public async Task PublishAsync(long taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await redis.GetDatabase().StreamAddAsync(
            _options.ProcessingStreamName,
            "taskId",
            taskId,
            null,
            _options.ProcessingStreamMaxLength,
            true,
            CommandFlags.None).ConfigureAwait(false);
    }
}
