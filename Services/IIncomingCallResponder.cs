using teams_streaming_call.Models;

namespace teams_streaming_call.Services;

public interface IIncomingCallResponder
{
    Task<bool> TryAcceptAsync(
        CallNotificationRecord notification,
        string? mediaConfiguration,
        CancellationToken cancellationToken);
}