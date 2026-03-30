using System.Runtime.InteropServices;
using Microsoft.Graph.Communications.Calls;
using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Skype.Bots.Media;

namespace teams_streaming_call.Services;

/// <summary>
/// Owns the audio stream for a single Teams call.
/// Subscribes to <see cref="IAudioSocket.AudioMediaReceived"/> on the call's
/// <see cref="ILocalMediaSession"/> and logs per-participant PCM frames.
/// </summary>
internal sealed class AudioCaptureSession : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly string _callId;
    private readonly IAudioSocket _audioSocket;
    private long _frameCount;
    private bool _disposed;

    public AudioCaptureSession(ICall call, ILogger logger)
    {
        _logger = logger;
        _callId = call.Id;

        // GetLocalMediaSession() retrieves the ILocalMediaSession that was passed
        // to call.AnswerAsync() inside BotService.OnIncoming.
        _audioSocket = call.GetLocalMediaSession().AudioSocket;
        _audioSocket.AudioMediaReceived += OnAudioMediaReceived;

        _logger.LogInformation(
            "Audio capture session initialized for CallId={CallId}.",
            _callId);
    }

    // ── Audio event ───────────────────────────────────────────────────────────

    private void OnAudioMediaReceived(object? sender, AudioMediaReceivedEventArgs e)
    {
        try
        {
            var frameNumber = Interlocked.Increment(ref _frameCount);
            if (frameNumber == 1)
                _logger.LogInformation("First audio frame received for CallId={CallId}", _callId);

            if (e.Buffer.Length <= 0 || e.Buffer.Data == IntPtr.Zero)
                return;

            var pcm = new byte[e.Buffer.Length];
            Marshal.Copy(e.Buffer.Data, pcm, 0, (int)e.Buffer.Length);

            _logger.LogDebug(
                "Audio: CallId={CallId}, Bytes={Length}",
                _callId,
                pcm.Length);

            // TODO: forward `pcm` to a WAV writer / transcription pipeline.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing audio buffer for CallId={CallId}", _callId);
        }
        finally
        {
            // Must always dispose — the SDK recycles the native buffer immediately.
            e.Buffer.Dispose();
        }
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;

        _audioSocket.AudioMediaReceived -= OnAudioMediaReceived;

        return ValueTask.CompletedTask;
    }
}

