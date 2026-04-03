using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Options;
using Microsoft.Graph.Communications.Calls;
using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Graph.Communications.Client;
using Microsoft.Graph.Communications.Resources;
using Microsoft.Graph.Communications.Common.Telemetry;
using Microsoft.Skype.Bots.Media;
using teams_streaming_call.Configuration;
using Microsoft.Graph.Models;
using Microsoft.Graph.Contracts;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace teams_streaming_call.Services;

/// <summary>
/// Central hosted service that owns the <see cref="ICommunicationsClient"/> — the SDK
/// entry point that handles call signaling and media for the bot.
/// Replaces <c>MediaPlatformInitializer</c>, <c>GraphIncomingCallResponder</c>, and
/// <c>MediaCaptureCoordinator</c>.
/// </summary>
public sealed class BotCallService : IHostedService, IDisposable
{
    private readonly TeamsCallBotOptions _options;
    private readonly IGraphLogger _graphLogger;
    private readonly ILogger<BotCallService> _logger;
    private readonly BotAuthenticationProvider _authProvider;

    private readonly ConcurrentDictionary<string, AudioCaptureSession> _sessions = new();
    private ICommunicationsClient? _client;

    /// <summary>Exposes the underlying SDK client so the controller can route notifications.</summary>
    public ICommunicationsClient Client =>
        _client ?? throw new InvalidOperationException("BotService has not been started.");

    public BotCallService(
        IOptions<TeamsCallBotOptions> options,
        IGraphLogger graphLogger,
        ILogger<BotCallService> logger,
        BotAuthenticationProvider authProvider)
    {
        _options = options.Value;
        _graphLogger = graphLogger;
        _logger = logger;
        _authProvider = authProvider;
    }

    // ── IHostedService ────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting BotService...");

        var name = typeof(BotCallService).Assembly.GetName().Name!;
        var builder = new CommunicationsClientBuilder(name, _options.AadAppId, _graphLogger);

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

        builder.SetAuthenticationProvider(_authProvider);
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

    public async Task<ICall> JoinCallAsync(string callId, string joinUrl, string? displayName)
    {
        _ = callId;

        // A tracking id for logging purposes. Helps identify this call in logs.
        var scenarioId = Guid.NewGuid();

        var (chatInfo, meetingInfo) = ParseJoinURL(joinUrl);
        var organizerMeetingInfo = meetingInfo as OrganizerMeetingInfo
            ?? throw new InvalidOperationException("Join URL did not produce OrganizerMeetingInfo.");
        var tenantId = organizerMeetingInfo.Organizer.GetPrimaryIdentity().GetTenantId();
        var mediaSession = CreateMediaSession(scenarioId);

        var joinParams = new JoinMeetingParameters(chatInfo, meetingInfo, mediaSession)
        {
            TenantId = tenantId,
        };

        if (!string.IsNullOrWhiteSpace(displayName))
        {
            // Teams client does not allow changing of ones own display name.
            // If display name is specified, we join as anonymous (guest) user
            // with the specified display name.  This will put bot into lobby
            // unless lobby bypass is disabled.
            joinParams.GuestIdentity = new Identity
            {
                Id = Guid.NewGuid().ToString(),
                DisplayName = displayName,
            };
        }

        var statefulCall = await _client.Calls().AddAsync(joinParams, scenarioId).ConfigureAwait(false);
        statefulCall.GraphLogger.Info($"Call creation complete: {statefulCall.Id}");
        return statefulCall;
    }

    // ── Media session factory ─────────────────────────────────────────────────

    private ILocalMediaSession CreateMediaSession(ICall call)
    {
        var mediaSessionId = Guid.TryParse(call.Id, out var id) ? id : Guid.NewGuid();

        return CreateMediaSessionCore(mediaSessionId);
    }

    private ILocalMediaSession CreateMediaSession(Guid callId)
    {
        var mediaSessionId = callId != Guid.Empty ? callId : Guid.NewGuid();

        return CreateMediaSessionCore(mediaSessionId);
    }

    private ILocalMediaSession CreateMediaSessionCore(Guid mediaSessionId)
    {
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

    /// <summary>
    /// Parse Join URL into its components.
    /// </summary>
    /// <param name="joinURL">Join URL from Team's meeting body.</param>
    /// <returns>Parsed data.</returns>
    /// <exception cref="ArgumentException">Join URL cannot be null or empty: {joinURL} - joinURL</exception>
    /// <exception cref="ArgumentException">Join URL cannot be parsed: {joinURL} - joinURL</exception>
    /// <exception cref="ArgumentException">Join URL is invalid: missing Tid - joinURL</exception>
    private (ChatInfo, Microsoft.Graph.Models.MeetingInfo) ParseJoinURL(string joinURL)
    {
        if (string.IsNullOrEmpty(joinURL))
        {
            throw new ArgumentException($"Join URL cannot be null or empty: {joinURL}", nameof(joinURL));
        }

        var decodedURL = WebUtility.UrlDecode(joinURL);

        //// URL being needs to be in this format.
        //// https://teams.microsoft.com/l/meetup-join/19:cd9ce3da56624fe69c9d7cd026f9126d@thread.skype/1509579179399?context={"Tid":"72f988bf-86f1-41af-91ab-2d7cd011db47","Oid":"550fae72-d251-43ec-868c-373732c2704f","MessageId":"1536978844957"}

        var regex = new Regex("https://teams\\.microsoft\\.com.*/(?<thread>[^/]+)/(?<message>[^/]+)\\?context=(?<context>{.*})");
        var match = regex.Match(decodedURL);
        if (!match.Success)
        {
            throw new ArgumentException($"Join URL cannot be parsed: {joinURL}", nameof(joinURL));
        }

        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(match.Groups["context"].Value)))
        {
            var contextObject = new DataContractJsonSerializer(typeof(Meeting)).ReadObject(stream);
            var ctxt = contextObject as Meeting
                ?? throw new ArgumentException("Join URL context is invalid.", nameof(joinURL));

            if (string.IsNullOrEmpty(ctxt.Tid))
            {
                throw new ArgumentException("Join URL is invalid: missing Tid", nameof(joinURL));
            }

            var chatInfo = new ChatInfo
            {
                ThreadId = match.Groups["thread"].Value,
                MessageId = match.Groups["message"].Value,
                ReplyChainMessageId = ctxt.MessageId,
            };

            var meetingInfo = new OrganizerMeetingInfo
            {
                Organizer = new IdentitySet
                {
                    User = new Identity { Id = ctxt.Oid },
                },
            };
            meetingInfo.Organizer.User.SetTenantId(ctxt.Tid);

            return (chatInfo, meetingInfo);
        }
    }
}
