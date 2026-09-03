namespace HoloScoop.Services.Redis;

public enum CandidateSaveResult
{
    Created,
    AlreadyExists
}

public interface IIncomingTaskStore
{
    Task<CandidateSaveResult> SaveCandidateAsync(
        string redisStream,
        string redisMessageId,
        IncomingStreamMessage message,
        DateTimeOffset? expiresAt,
        CancellationToken cancellationToken);
}
