using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Runtime.Versioning;
using teams_streaming_call.Configuration;
using teams_streaming_call.Models;

namespace teams_streaming_call.Services;

/// <summary>
/// Coordinates audio capture sessions. On Windows with media capture enabled, creates an
/// <see cref="AudioCaptureSession"/> per call that opens a native <c>AudioSocket</c>,
/// subscribes to incoming PCM frames, and writes them to a WAV file.
/// On non-Windows hosts the coordinator returns informational notes and makes no SDK calls.
/// </summary>
public sealed class MediaCaptureCoordinator : IMediaCaptureCoordinator
{
    private readonly TeamsCallBotOptions _options;
    private readonly ILogger<MediaCaptureCoordinator> _logger;

    // Values are AudioCaptureSession instances on Windows; stored as IAsyncDisposable
    // to avoid requiring Windows platform annotation on the dictionary field itself.
    private readonly ConcurrentDictionary<string, IAsyncDisposable> _sessions =
        new(StringComparer.OrdinalIgnoreCase);

    public MediaCaptureCoordinator(
        IOptions<TeamsCallBotOptions> options,
        ILogger<MediaCaptureCoordinator> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task<string?> PrepareCaptureAsync(
        CallNotificationRecord notification,
        CancellationToken cancellationToken)
    {
        var callId = notification.CallId ?? "unknown";

        if (OperatingSystem.IsWindows() &&
            _sessions.TryGetValue(callId, out var existingSession) &&
            existingSession is AudioCaptureSession existingAudioSession)
        {
            return Task.FromResult<string?>(existingAudioSession.MediaConfiguration);
        }

        bool hasMedia = notification.Modalities.Any(static m =>
            m.Equals("audio", StringComparison.OrdinalIgnoreCase) ||
            m.Equals("video", StringComparison.OrdinalIgnoreCase));

        bool isIncoming = notification.CallState is not null &&
            notification.CallState.Equals("incoming", StringComparison.OrdinalIgnoreCase);

        if (!hasMedia && !isIncoming)
            return Task.FromResult<string?>(null);

        if (!_options.EnableWindowsMediaCapture)
        {
            const string note = "Audio/video modalities detected. Real media sockets are disabled. " +
            "Set TeamsCallBot:EnableWindowsMediaCapture=true on Windows to capture raw streams.";
            _logger.LogWarning("{Note} CallId={CallId}", note, notification.CallId);
            return Task.FromResult<string?>(note);
        }

        if (!OperatingSystem.IsWindows())
        {
            const string note =
                "Audio/video modalities detected but media capture requires Windows. " +
                "Deploy to Windows Server to use the Graph media platform.";
            _logger.LogWarning("{Note} CallId={CallId}", note, notification.CallId);
            return Task.FromResult<string?>(note);
        }

        var mediaConfig = StartCaptureOnWindows(callId);
        return Task.FromResult(mediaConfig);
    }

    [SupportedOSPlatform("windows")]
    private string? StartCaptureOnWindows(string callId)
    {
        try
        {
            var session = new AudioCaptureSession(callId, _options.CaptureRoot, _logger);
            _sessions[callId] = session;
            _logger.LogInformation("Audio capture session started for CallId={CallId}", callId);
            return session.MediaConfiguration;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start audio capture for CallId={CallId}", callId);
            return null;
        }
    }

    public async Task StopCaptureAsync(string callId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryRemove(callId, out var session))
            return;

        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
            _logger.LogInformation("Audio capture session stopped for CallId={CallId}", callId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping capture session for CallId={CallId}", callId);
        }
    }
}