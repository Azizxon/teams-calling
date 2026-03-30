using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Options;
using Microsoft.Graph.Communications.Calls;
using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Graph.Communications.Client;
using Microsoft.Graph.Communications.Common;
using Microsoft.Graph.Communications.Resources;
using Microsoft.Graph.Communications.Common.Telemetry;
using Microsoft.Skype.Bots.Media;
using teams_streaming_call.Configuration;
using teams_streaming_call.Models;

namespace teams_streaming_call.Services;

/// <summary>
/// Central hosted service that owns the <see cref="ICommunicationsClient"/> — the SDK
/// entry point that handles call signaling and media for the bot.
/// Replaces <c>MediaPlatformInitializer</c>, <c>GraphIncomingCallResponder</c>, and
/// <c>MediaCaptureCoordinator</c>.
/// </summary>
public sealed class BotService : IHostedService, IDisposable
{
    private readonly TeamsCallBotOptions _options;
    private readonly IGraphLogger _graphLogger;
    private readonly ILogger<BotService> _logger;
    private readonly ICallNotificationArchiver _archiver;
    private readonly ICallSessionStore _store;

    private readonly ConcurrentDictionary<string, AudioCaptureSession> _sessions = new();
    private ICommunicationsClient? _client;

    /// <summary>Exposes the underlying SDK client so the controller can route notifications.</summary>
    public ICommunicationsClient Client =>
        _client ?? throw new InvalidOperationException("BotService has not been started.");

    public BotService(
        IOptions<TeamsCallBotOptions> options,
        IGraphLogger graphLogger,
        ILogger<BotService> logger,
        ICallNotificationArchiver archiver,
        ICallSessionStore store)
    {
        _options = options.Value;
        _graphLogger = graphLogger;
        _logger = logger;
        _archiver = archiver;
        _store = store;
    }

    // ── IHostedService ────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting BotService...");

        var name = typeof(BotService).Assembly.GetName().Name!;
        var builder = new CommunicationsClientBuilder(name, _options.AadAppId, _graphLogger);

        var authProvider = new BotAuthenticationProvider(
            _options.AadAppId,
            _options.AadAppSecret,
            _options.TenantId,
            _graphLogger);

        // The notification URL is where Graph sends call-state change webhooks.
        // It must match the route registered in CallsController.
        var callbackHost = _options.ServiceCname.TrimEnd('/');
        if (!callbackHost.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            callbackHost = $"https://{callbackHost}";

        var callbackPath = _options.CallsEndpointPath.StartsWith('/')
            ? _options.CallsEndpointPath
            : $"/{_options.CallsEndpointPath}";

        var notificationUrl = new Uri($"{callbackHost}{callbackPath}");

        var mediaPlatformSettings = new MediaPlatformSettings
        {
            MediaPlatformInstanceSettings = new MediaPlatformInstanceSettings
            {
                CertificateThumbprint = _options.CertificateThumbprint,
                InstanceInternalPort = _options.InstanceInternalPort,
                InstancePublicIPAddress = IPAddress.Any,
                InstancePublicPort = _options.InstancePublicPort,
                ServiceFqdn = _options.MediaServiceFqdn,
            },
            ApplicationId = _options.AadAppId,
        };

        builder.SetAuthenticationProvider(authProvider);
        builder.SetNotificationUrl(notificationUrl);
        builder.SetMediaPlatformSettings(mediaPlatformSettings);
        builder.SetServiceBaseUrl(new Uri(_options.PlaceCallEndpointUrl));

        _client = builder.Build();
        _client.Calls().OnIncoming += OnIncoming;
        _client.Calls().OnUpdated += OnUpdated;

        _logger.LogInformation(
            "BotService started. NotificationUrl={Url}, MediaFqdn={Fqdn}, PublicPort={Port}",
            notificationUrl,
            _options.MediaServiceFqdn,
            _options.InstancePublicPort);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping BotService...");

        if (_client != null)
        {
            try { await _client.TerminateAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error terminating communications client."); }
        }

        foreach (var (callId, session) in _sessions)
        {
            try { await session.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error disposing session for CallId={CallId}", callId); }
        }
        _sessions.Clear();

        Dispose();
    }

    public void Dispose()
    {
        _client?.Dispose();
        _client = null;
    }

    // ── Call events ───────────────────────────────────────────────────────────

    private void OnIncoming(ICallCollection sender, CollectionEventArgs<ICall> args)
    {
        foreach (var call in args.AddedResources)
        {
            _logger.LogInformation("Incoming call {CallId}. Answering with app-hosted media.", call.Id);
            try
            {
                var mediaSession = CreateMediaSession(call);
                _ = call.AnswerAsync(mediaSession).ContinueWith(
                    t => _logger.LogError(t.Exception, "Error during AnswerAsync for CallId={CallId}", call.Id),
                    TaskContinuationOptions.OnlyOnFaulted);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error answering call {CallId}", call.Id);
            }
        }
    }

    private void OnUpdated(ICallCollection sender, CollectionEventArgs<ICall> args)
    {
        foreach (var call in args.AddedResources)
        {
            if (!_sessions.ContainsKey(call.Id))
            {
                try
                {
                    var session = new AudioCaptureSession(call, _logger);
                    _sessions[call.Id] = session;
                    _logger.LogInformation("Audio capture session started for CallId={CallId}", call.Id);

                    _store.Upsert(
                        new CallNotificationRecord(
                            null, "updated", null, null,
                            call.Id, call.Resource.State?.ToString(), Array.Empty<string>(),
                            DateTimeOffset.UtcNow, string.Empty),
                        null, "Audio capture session active");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error starting audio capture for CallId={CallId}", call.Id);
                }
            }
        }

        foreach (var call in args.RemovedResources)
        {
            if (_sessions.TryRemove(call.Id, out var session))
            {
                _ = session.DisposeAsync().AsTask().ContinueWith(
                    t => _logger.LogWarning(t.Exception, "Error disposing session for CallId={CallId}", call.Id),
                    TaskContinuationOptions.OnlyOnFaulted);

                _logger.LogInformation("Audio capture session stopped for CallId={CallId}", call.Id);
            }
        }
    }

    // ── Media session factory ─────────────────────────────────────────────────

    private ILocalMediaSession CreateMediaSession(ICall call)
    {
        var mediaSessionId = Guid.TryParse(call.Id, out var id) ? id : Guid.NewGuid();

        return _client!.CreateMediaSession(
            new AudioSocketSettings
            {
                StreamDirections = StreamDirection.Recvonly,
                SupportedAudioFormat = AudioFormat.Pcm16K,
            },
            new VideoSocketSettings
            {
                StreamDirections = StreamDirection.Inactive,
            },
            mediaSessionId: mediaSessionId);
    }
}
