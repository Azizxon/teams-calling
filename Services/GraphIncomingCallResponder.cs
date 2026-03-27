using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using teams_streaming_call.Configuration;
using teams_streaming_call.Models;

namespace teams_streaming_call.Services;

public sealed class GraphIncomingCallResponder : IIncomingCallResponder
{
    private const string GraphScope = "https://graph.microsoft.com/.default";

    private readonly HttpClient httpClient;
    private readonly TeamsCallBotOptions options;
    private readonly ILogger<GraphIncomingCallResponder> logger;

    public GraphIncomingCallResponder(
        HttpClient httpClient,
        IOptions<TeamsCallBotOptions> options,
        ILogger<GraphIncomingCallResponder> logger)
    {
        this.httpClient = httpClient;
        this.options = options.Value;
        this.logger = logger;
    }

    public async Task<bool> TryAcceptAsync(
        CallNotificationRecord notification,
        string? mediaConfiguration,
        CancellationToken cancellationToken)
    {
        if (!ShouldAccept(notification))
            return false;

        if (string.IsNullOrWhiteSpace(options.AadAppId) || string.IsNullOrWhiteSpace(options.AadAppSecret))
        {
            logger.LogWarning("Skipping incoming call acceptance because AAD app credentials are not configured.");
            return false;
        }

        var callId = notification.CallId;
        if (string.IsNullOrWhiteSpace(callId))
            return false;

        var tenantId = string.IsNullOrWhiteSpace(notification.TenantId)
            ? "organizations"
            : notification.TenantId;

        var token = await AcquireTokenAsync(tenantId, cancellationToken);
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
        logger.LogWarning(
            "Failed to accept incoming call {CallId}. Status={StatusCode} Body={Body}",
            callId,
            (int)response.StatusCode,
            errorBody);

        return false;
    }

    private async Task<string?> AcquireTokenAsync(string tenantId, CancellationToken cancellationToken)
    {
        var tokenEndpoint = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";
        var formBody = new Dictionary<string, string>
        {
            ["client_id"] = options.AadAppId,
            ["client_secret"] = options.AadAppSecret,
            ["grant_type"] = "client_credentials",
            ["scope"] = GraphScope,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(formBody),
        };

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning(
                "Failed to acquire Graph token for tenant {TenantId}. Status={StatusCode} Body={Body}",
                tenantId,
                (int)response.StatusCode,
                errorBody);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        return json.RootElement.TryGetProperty("access_token", out var tokenElement)
            ? tokenElement.GetString()
            : null;
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
        JsonDocument.Parse("{" +
            "\"@odata.type\":\"#microsoft.graph.serviceHostedMediaConfig\"" +
        "}")
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

        JsonElement mediaConfigToSend;
        var requireAppHosted = options.EnableWindowsMediaCapture && OperatingSystem.IsWindows();
        var parsedMediaConfig = TryParseJsonObject(mediaConfiguration);
        if (!parsedMediaConfig.HasValue)
        {
            mediaConfigToSend = ServiceHostedMediaConfig;
            reason = requireAppHosted
                ? "Media configuration is missing/invalid while Windows media capture is enabled. " +
                  "Falling back to #microsoft.graph.serviceHostedMediaConfig so the call can be answered."
                : "Media configuration is missing/invalid; using " +
                  "#microsoft.graph.serviceHostedMediaConfig in signaling-only mode.";
        }
        else if (!TryReadODataType(parsedMediaConfig.Value, out var oDataType))
        {
            mediaConfigToSend = ServiceHostedMediaConfig;
            reason = requireAppHosted
                ? "Media configuration JSON does not include @odata.type while Windows media capture is enabled. " +
                  "Falling back to #microsoft.graph.serviceHostedMediaConfig so the call can be answered."
                : "Media configuration JSON does not include @odata.type; using " +
                  "#microsoft.graph.serviceHostedMediaConfig in signaling-only mode.";
        }
        else if (oDataType.Equals("#microsoft.graph.appHostedMediaConfig", StringComparison.OrdinalIgnoreCase))
        {
            mediaConfigToSend = parsedMediaConfig.Value;
            reason = string.Empty;
        }
        else if (oDataType.Equals("#microsoft.graph.serviceHostedMediaConfig", StringComparison.OrdinalIgnoreCase))
        {
            mediaConfigToSend = parsedMediaConfig.Value;
            reason = requireAppHosted
                ? "Received #microsoft.graph.serviceHostedMediaConfig while Windows media capture is enabled. " +
                  "Proceeding so the call can be answered, but app-hosted capture may not start."
                : string.Empty;
        }
        else
        {
            mediaConfigToSend = ServiceHostedMediaConfig;
            reason = requireAppHosted
                ? $"Media configuration @odata.type '{oDataType}' is unsupported while Windows media capture is enabled. " +
                  "Falling back to #microsoft.graph.serviceHostedMediaConfig so the call can be answered."
                : $"Media configuration @odata.type '{oDataType}' is unsupported; using " +
                  "#microsoft.graph.serviceHostedMediaConfig in signaling-only mode.";
        }

        if (!string.IsNullOrWhiteSpace(reason))
        {
            logger.LogWarning("{Reason} CallId={CallId}", reason, notification.CallId);
        }

        payload = new
        {
            callbackUri,
            acceptedModalities,
            mediaConfig = mediaConfigToSend,
        };

        reason = string.Empty;
        return true;
    }

    private static bool TryReadODataType(JsonElement mediaConfig, out string oDataType)
    {
        oDataType = string.Empty;

        if (!mediaConfig.TryGetProperty("@odata.type", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var value = typeElement.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        oDataType = value;
        return true;
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
}