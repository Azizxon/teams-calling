using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Graph.Communications.Client.Authentication;
using Microsoft.Graph.Communications.Common;
using Microsoft.Graph.Communications.Common.Telemetry;

namespace teams_streaming_call.Services;

/// <summary>
/// Authenticates outbound requests from <see cref="ICommunicationsClient"/> to the
/// Microsoft Graph API using the OAuth 2.0 client-credentials flow.
/// </summary>
internal sealed class BotAuthenticationProvider : IRequestAuthenticationProvider
{
    private readonly string _appId;
    private readonly string _appSecret;
    private readonly string _fallbackTenant;
    private readonly IGraphLogger _logger;
    private readonly HttpClient _httpClient = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Per-tenant token cache.
    private readonly Dictionary<string, (string Token, DateTimeOffset Expiry)> _cache = new();

    public BotAuthenticationProvider(string appId, string appSecret, string fallbackTenant, IGraphLogger logger)
    {
        _appId = appId;
        _appSecret = appSecret;
        _fallbackTenant = string.IsNullOrWhiteSpace(fallbackTenant) ? "organizations" : fallbackTenant;
        _logger = logger;
    }

    public async Task AuthenticateOutboundRequestAsync(HttpRequestMessage request, string tenant)
    {
        // The SDK passes an empty tenant when the tenant is not yet known.
        var effectiveTenant = string.IsNullOrWhiteSpace(tenant) ? _fallbackTenant : tenant;
        var token = await GetTokenAsync(effectiveTenant).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public Task<RequestValidationResult> ValidateInboundRequestAsync(HttpRequestMessage request)
    {
        return Task.FromResult(new RequestValidationResult{ IsValid = true });
    }

    private async Task<string> GetTokenAsync(string tenant)
    {
        if (_cache.TryGetValue(tenant, out var cached) && DateTimeOffset.UtcNow < cached.Expiry.AddMinutes(-5))
            return cached.Token;

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(tenant, out cached) && DateTimeOffset.UtcNow < cached.Expiry.AddMinutes(-5))
                return cached.Token;

            var tokenEndpoint = $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token";
            _logger.Info($"Requesting token for tenant={tenant}");

            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _appId,
                ["client_secret"] = _appSecret,
                ["grant_type"] = "client_credentials",
                ["scope"] = "https://graph.microsoft.com/.default",
            });

            using var response = await _httpClient.PostAsync(tokenEndpoint, form).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);

            var token = json.RootElement.GetProperty("access_token").GetString()!;
            var expiresIn = json.RootElement.GetProperty("expires_in").GetInt32();
            _cache[tenant] = (token, DateTimeOffset.UtcNow.AddSeconds(expiresIn));

            return token;
        }
        finally
        {
            _lock.Release();
        }
    }
}
