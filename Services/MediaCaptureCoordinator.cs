using teams_streaming_call.Models;

namespace teams_streaming_call.Services;

/// <summary>
/// Stub kept for interface compatibility. Call/media lifecycle is now fully
/// managed by <see cref="BotService"/> via <see cref="ICommunicationsClient"/>.
/// </summary>
public sealed class MediaCaptureCoordinator : IMediaCaptureCoordinator
{
    public Task<string?> PrepareCaptureAsync(CallNotificationRecord notification, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);

    public Task StopCaptureAsync(string callId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}