namespace teams_streaming_call.Services;

public interface ICallNotificationArchiver
{
    Task<string?> ArchiveAsync(string callId, string rawJson, CancellationToken cancellationToken);
}