using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using Microsoft.Skype.Bots.Media;

namespace teams_streaming_call.Services;

/// <summary>
/// Owns an <see cref="IAudioSocket"/> for a single Teams call and writes incoming
/// 16-kHz PCM frames to a WAV file under <c>captures/audio/</c>.
/// Windows-only: the native Microsoft.Skype.Bots.Media platform only runs on Windows.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class AudioCaptureSession : IAsyncDisposable
{
    // PCM 16-kHz, 16-bit, mono
    private const int SampleRate = 16_000;
    private const short BitsPerSample = 16;
    private const short NumChannels = 1;
    private const int ByteRate = SampleRate * NumChannels * (BitsPerSample / 8);
    private const short BlockAlign = NumChannels * (BitsPerSample / 8);

    private readonly AudioSocket _audioSocket;
    private readonly Channel<byte[]> _frameChannel;
    private readonly Task _writerTask;
    private readonly string _outputPath;
    private readonly ILogger _logger;
    private bool _disposed;

    /// <summary>
    /// JSON-serialized MediaConfiguration to pass back to Graph in the answer-call response.
    /// </summary>
    public string MediaConfiguration { get; } = string.Empty;

    public AudioCaptureSession(string callId, string captureRoot, ILogger logger)
    {
        _logger = logger;

        var settings = new AudioSocketSettings
        {
            StreamDirections = StreamDirection.Recvonly,
            SupportedAudioFormat = AudioFormat.Pcm16K,
            CallId = callId,
        };

        _audioSocket = new AudioSocket(settings);
        _audioSocket.AudioMediaReceived += OnAudioMediaReceived;

        // CreateMediaConfiguration returns a JObject (Newtonsoft) — serialise to string
        MediaConfiguration = MediaPlatform.CreateMediaConfiguration(_audioSocket).ToString();

        var safeId = SafeFileName(callId);
        var dir = Path.Combine(captureRoot, "audio");
        Directory.CreateDirectory(dir);
        _outputPath = Path.Combine(dir, $"{safeId}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.wav");

        // Bounded channel: 500 frames ≈ 10 seconds; oldest frames dropped on overflow
        _frameChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(500)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        _writerTask = Task.Run(() => WriteWavAsync(CancellationToken.None));
    }

    // ── Audio event ─────────────────────────────────────────────────────────────

    private void OnAudioMediaReceived(object? sender, AudioMediaReceivedEventArgs e)
    {
        try
        {
            var buf = e.Buffer;
            if (buf.Length <= 0 || buf.Data == IntPtr.Zero)
                return;

            var pcm = new byte[buf.Length];
            Marshal.Copy(buf.Data, pcm, 0, (int)buf.Length);
            _frameChannel.Writer.TryWrite(pcm);
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

    // ── WAV writer ──────────────────────────────────────────────────────────────

    private async Task WriteWavAsync(CancellationToken ct)
    {
        try
        {
            await using var fs = new FileStream(
                _outputPath, FileMode.Create, FileAccess.Write,
                FileShare.None, bufferSize: 65536, FileOptions.Asynchronous);

            // 44-byte WAV header with zero sizes; patched once recording ends
            await fs.WriteAsync(BuildWavHeader(dataChunkSize: 0), ct).ConfigureAwait(false);

            int totalBytes = 0;
            await foreach (var frame in _frameChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await fs.WriteAsync(frame, ct).ConfigureAwait(false);
                totalBytes += frame.Length;
            }

            // Patch RIFF chunk size (offset 4) and data chunk size (offset 40)
            var riffSize = new byte[4];
            var dataSize = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(riffSize, 36 + totalBytes);
            BinaryPrimitives.WriteInt32LittleEndian(dataSize, totalBytes);

            fs.Seek(4, SeekOrigin.Begin);
            await fs.WriteAsync(riffSize, ct).ConfigureAwait(false);
            fs.Seek(40, SeekOrigin.Begin);
            await fs.WriteAsync(dataSize, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Audio capture saved: {Path} ({Bytes:N0} bytes, {Seconds:F1} s)",
                _outputPath, totalBytes, totalBytes / (double)ByteRate);
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WAV writer failed for {Path}", _outputPath);
        }
    }

    private static byte[] BuildWavHeader(int dataChunkSize)
    {
        var h = new byte[44];
        // RIFF chunk
        h[0] = (byte)'R'; h[1] = (byte)'I'; h[2] = (byte)'F'; h[3] = (byte)'F';
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(4), 36 + dataChunkSize);
        h[8] = (byte)'W'; h[9] = (byte)'A'; h[10] = (byte)'V'; h[11] = (byte)'E';
        // fmt chunk
        h[12] = (byte)'f'; h[13] = (byte)'m'; h[14] = (byte)'t'; h[15] = (byte)' ';
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(20), 1);           // PCM
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(22), NumChannels);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(24), SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(28), ByteRate);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(32), BlockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(34), BitsPerSample);
        // data chunk header
        h[36] = (byte)'d'; h[37] = (byte)'a'; h[38] = (byte)'t'; h[39] = (byte)'a';
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(40), dataChunkSize);
        return h;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
        return safe.Length > 64 ? safe[..64] : safe;
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Unsubscribe so no more frames arrive, then signal the writer to finish
        _audioSocket.AudioMediaReceived -= OnAudioMediaReceived;
        _frameChannel.Writer.TryComplete();

        try { await _writerTask.ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "WAV writer faulted during dispose"); }

        _audioSocket.Dispose();
    }
}
