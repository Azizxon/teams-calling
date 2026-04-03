using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Microsoft.Graph.Contracts;
using Microsoft.Graph.Models;
using teams_streaming_call.Configuration;
using teams_streaming_call.Models;

namespace teams_streaming_call.Services;

public sealed class GraphIncomingCallResponder : IIncomingCallResponder
{
    private readonly HttpClient httpClient;
    private readonly TeamsCallBotOptions options;
    private readonly BotAuthenticationProvider authProvider;
    private readonly ILogger<GraphIncomingCallResponder> logger;

    public GraphIncomingCallResponder(
        HttpClient httpClient,
        IOptions<TeamsCallBotOptions> options,
        BotAuthenticationProvider authProvider,
        ILogger<GraphIncomingCallResponder> logger)
    {
        this.httpClient = httpClient;
        this.options = options.Value;
        this.authProvider = authProvider;
        this.logger = logger;
    }

    public async Task<bool> TryAcceptAsync(
        CallNotificationRecord notification,
        string? mediaConfiguration,
        CancellationToken cancellationToken)
    {
        if (!ShouldAccept(notification))
            return false;

        var callId = notification.CallId;
        if (string.IsNullOrWhiteSpace(callId))
            return false;

        var tenantId = string.IsNullOrWhiteSpace(notification.TenantId)
            ? "organizations"
            : notification.TenantId;

        using var tokenRequest = new HttpRequestMessage();
        await authProvider.AuthenticateOutboundRequestAsync(tokenRequest, tenantId);
        var token = tokenRequest.Headers.Authorization?.Parameter;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        var requestUrl = BuildAnswerEndpoint(callId);
        if (!TryBuildAnswerPayload(notification, mediaConfiguration, out var payload, out var payloadError))
        {
            logger.LogWarning(
                "Skipping incoming call acceptance for {CallId}. {Reason}",
                callId,
                payloadError);
            return false;
        }

        var requestJson = JsonSerializer.Serialize(payload);
        logger.LogDebug("Graph answer payload for {CallId}: {Payload}", callId, requestJson);

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl)
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            logger.LogInformation("Accepted incoming call {CallId} via Microsoft Graph.", callId);
            return true;
        }

        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if ((int)response.StatusCode == 400)
        {
            logger.LogWarning(
                "Graph answer request returned 400 for CallId={CallId}. RequestPayload={Payload}",
                callId,
                requestJson);
        }

        logger.LogWarning(
            "Failed to accept incoming call {CallId}. Status={StatusCode} Body={Body}",
            callId,
            (int)response.StatusCode,
            errorBody);

        return false;
    }

    private string BuildAnswerEndpoint(string callId)
    {
        var root = options.PlaceCallEndpointUrl.TrimEnd('/');
        return $"{root}/communications/calls/{Uri.EscapeDataString(callId)}/answer";
    }

    private static readonly HashSet<string> SupportedModalities = new(StringComparer.OrdinalIgnoreCase)
    {
        "audio",
        "video",
        "videoBasedScreenSharing",
    };

    private static readonly JsonElement ServiceHostedMediaConfig =
        JsonDocument.Parse("{\"@odata.type\":\"#microsoft.graph.serviceHostedMediaConfig\"}")
            .RootElement
            .Clone();

    private bool TryBuildAnswerPayload(
        CallNotificationRecord notification,
        string? mediaConfiguration,
        out object? payload,
        out string reason)
    {
        payload = null;
        reason = string.Empty;

        var callbackUri = BuildCallbackUri();
        var acceptedModalities = notification.Modalities
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Where(value => SupportedModalities.Contains(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (acceptedModalities.Length == 0)
        {
            acceptedModalities = new[] { "audio" };
        }

        object mediaConfigToSend = ServiceHostedMediaConfig;
        var parsedMediaConfiguration = TryParseJsonObject(mediaConfiguration);
        if (parsedMediaConfiguration.HasValue)
        {
            if (TryBuildAppHostedMediaConfig(parsedMediaConfiguration.Value, out var appHostedMediaConfig))
            {
                mediaConfigToSend = appHostedMediaConfig;
            }
            else
            {
                logger.LogWarning(
                    "Provided media configuration JSON is present but does not contain a valid app-hosted blob for CallId={CallId}; " +
                    "falling back to serviceHostedMediaConfig.",
                    notification.CallId);
            }
        }
        else if (options.EnableWindowsMediaCapture && OperatingSystem.IsWindows())
        {
            logger.LogWarning(
                "Windows media capture is enabled but app-hosted media configuration is missing/invalid for CallId={CallId}; " +
                "falling back to serviceHostedMediaConfig and no raw audio frames will be delivered.",
                notification.CallId);
        }

        payload = new
        {
            callbackUri,
            acceptedModalities,
            mediaConfig = mediaConfigToSend,
        };

        var mediaType = TryGetMediaConfigType(mediaConfigToSend) ?? "unknown";
        logger.LogInformation(
            "Answer payload prepared for CallId={CallId}. acceptedModalities=[{Modalities}] mediaConfigType={MediaType}",
            notification.CallId,
            string.Join(",", acceptedModalities),
            mediaType);

        return true;
    }

    private static string? TryGetMediaConfigType(object mediaConfig)
    {
        if (mediaConfig is JsonElement jsonElement)
        {
            if (!jsonElement.TryGetProperty("@odata.type", out var typeProperty) ||
                typeProperty.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return typeProperty.GetString();
        }

        if (mediaConfig is JsonObject jsonObject)
        {
            return jsonObject["@odata.type"]?.GetValue<string>();
        }

        return null;
    }

    private static bool TryBuildAppHostedMediaConfig(JsonElement mediaConfiguration, out object appHostedMediaConfig)
    {
        appHostedMediaConfig = null!;

        if (!mediaConfiguration.TryGetProperty("blob", out var blobElement) ||
            blobElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var blob = blobElement.GetString();
        if (string.IsNullOrWhiteSpace(blob))
        {
            return false;
        }

        var jsonObject = new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.appHostedMediaConfig",
            ["blob"] = blob,
        };

        if (mediaConfiguration.TryGetProperty("receiveUnmixedMeetingAudio", out var receiveUnmixedElement) &&
            receiveUnmixedElement.ValueKind == JsonValueKind.True)
        {
            jsonObject["receiveUnmixedMeetingAudio"] = true;
        }

        appHostedMediaConfig = jsonObject;

        return true;
    }

    private static JsonElement? TryParseJsonObject(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(value);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private string BuildCallbackUri()
    {
        var baseAddress = options.ServiceCname;
        if (!baseAddress.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !baseAddress.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            baseAddress = $"https://{baseAddress}";
        }

        var callbackPath = options.CallsEndpointPath.StartsWith('/')
            ? options.CallsEndpointPath
            : $"/{options.CallsEndpointPath}";

        return new Uri(new Uri(baseAddress), callbackPath).AbsoluteUri;
    }

    private static bool ShouldAccept(CallNotificationRecord notification)
    {
        return !string.IsNullOrWhiteSpace(notification.CallId) &&
            notification.CallState is not null &&
            notification.CallState.Equals("incoming", StringComparison.OrdinalIgnoreCase);
    }
}