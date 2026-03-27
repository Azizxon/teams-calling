using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Skype.Bots.Media;

namespace teams_streaming_call.Services;

/// <summary>
/// Owns an <see cref="IAudioSocket"/> for a single Teams call and logs incoming
/// 16-kHz PCM frames.
/// Windows-only: the native Microsoft.Skype.Bots.Media platform only runs on Windows.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class AudioCaptureSession : IAsyncDisposable
{
    private readonly AudioSocket _audioSocket;
    private readonly ILogger _logger;
    private readonly string _callId;
    private readonly PeriodicTimer _firstFrameWatchdog = new(TimeSpan.FromSeconds(10));
    private readonly CancellationTokenSource _watchdogCts = new();
    private long _frameCount;
    private bool _disposed;

    /// <summary>
    /// JSON-serialized MediaConfiguration to pass back to Graph in the answer-call response.
    /// </summary>
    public string MediaConfiguration { get; } = string.Empty;

    public AudioCaptureSession(string callId, string captureRoot, ILogger logger)
    {
        _logger = logger;
        _callId = callId;

        var settings = new AudioSocketSettings
        {
            StreamDirections = StreamDirection.Recvonly,
            SupportedAudioFormat = AudioFormat.Pcm16K,
            CallId = callId,
        };

        _audioSocket = new AudioSocket(settings);
        _audioSocket.AudioMediaReceived += OnAudioMediaReceived;

        // CreateMediaConfiguration returns a JObject (Newtonsoft).
        // Inject @odata.type so Graph routes audio frames to this app-hosted socket
        // rather than falling back to service-hosted (cloud) media processing.
        var mediaConfigJson = MediaPlatform.CreateMediaConfiguration(_audioSocket);
        mediaConfigJson["@odata.type"] = "#microsoft.graph.appHostedMediaConfig";
        MediaConfiguration = mediaConfigJson.ToString();

        _logger.LogInformation(
            "Audio capture session initialized for CallId={CallId}. WAV persistence is disabled; frames will be logged only.",
            callId);

        _ = WatchForMissingFirstFrameAsync();
    }

    // ── Audio event ─────────────────────────────────────────────────────────────

    private void OnAudioMediaReceived(object? sender, AudioMediaReceivedEventArgs e)
    {
        try
        {
            var frameNumber = Interlocked.Increment(ref _frameCount);
            if (frameNumber == 1)
            {
                _logger.LogInformation("First audio frame received for CallId={CallId}", _callId);
            }

            _logger.LogInformation(
                "Received audio frame for call: {Length} bytes, timestamp {Timestamp}",
                 e.Buffer.Length, e.Buffer.Timestamp);
            var buf = e.Buffer;
            if (buf.Length <= 0 || buf.Data == IntPtr.Zero)
                return;

            var pcm = new byte[buf.Length];
            Marshal.Copy(buf.Data, pcm, 0, (int)buf.Length);
            // Keep the copy operation to ensure native buffer access remains valid while logging.
            _ = pcm;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error copying audio buffer");
        }
        finally
        {
            // Always dispose – the SDK recycles the native buffer immediately after the event
            e.Buffer.Dispose();
        }
    }

    private async Task WatchForMissingFirstFrameAsync()
    {
        try
        {
            await _firstFrameWatchdog.WaitForNextTickAsync(_watchdogCts.Token).ConfigureAwait(false);

            if (Interlocked.Read(ref _frameCount) == 0)
            {
                _logger.LogWarning(
                    "No audio frames received within 10 seconds for CallId={CallId}. " +
                    "Likely causes: answer used serviceHostedMediaConfig, media endpoint reachability/firewall, or certificate/media platform mismatch.",
                    _callId);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal on dispose.
        }
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Unsubscribe so no more frames arrive.
        _audioSocket.AudioMediaReceived -= OnAudioMediaReceived;

        _watchdogCts.Cancel();
        _firstFrameWatchdog.Dispose();
        _watchdogCts.Dispose();

        _audioSocket.Dispose();
        await Task.CompletedTask;
    }
}
