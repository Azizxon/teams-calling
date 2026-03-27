using teams_streaming_call.Models;

namespace teams_streaming_call.Services;

public interface IMediaCaptureCoordinator
{
    /// <summary>
    /// Prepares audio capture for the given call notification after the call signaling flow
    /// has already been established.
    /// On Windows with media capture enabled, creates an <see cref="AudioCaptureSession"/>
    /// and returns the serialized <c>MediaConfiguration</c> for diagnostics or later use.
    /// Returns <c>null</c> if the notification has no audio/video modalities.
    /// </summary>
    Task<string?> PrepareCaptureAsync(CallNotificationRecord notification, CancellationToken cancellationToken);

    /// <summary>
    /// Stops and finalizes the audio capture session for the given call, flushing the WAV file.
    /// No-op if no active session exists for <paramref name="callId"/>.
    /// </summary>
    Task StopCaptureAsync(string callId, CancellationToken cancellationToken = default);
}
