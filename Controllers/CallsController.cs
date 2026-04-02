using Microsoft.AspNetCore.Mvc;
using teams_streaming_call.Services;

namespace teams_streaming_call.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class CallsController : ControllerBase
{
    private readonly BotCallService _botService;
    private readonly ILogger<CallsController> _logger;

    public CallsController(
        BotCallService botService,
        ILogger<CallsController> logger)
    {
        _botService = botService;
        _logger = logger;
    }

    /// <summary>
    /// Receives Graph change-notification webhooks and routes them to the
    /// <see cref="ICommunicationsClient"/> so the SDK can fire OnIncoming /
    /// OnUpdated events and manage call state.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> ReceiveAsync(CancellationToken cancellationToken)
    {
        // Enable buffering so we can read the body twice.
        Request.EnableBuffering();
        using var reader = new System.IO.StreamReader(Request.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync(cancellationToken);
        Request.Body.Position = 0;

        // Deserialize via the SDK's serializer and dispatch to the client so it
        // can fire OnIncoming / OnUpdated on the call collection.
        var client = _botService.Client;
        var notifications = client.Serializer.DeserializeObject<Microsoft.Graph.Communications.Common.CommsNotifications>(body);
        if (notifications == null)
        {
            _logger.LogWarning("Received a notification payload that could not be deserialized.");
            return BadRequest();
        }

        var requestUri = new Uri($"{Request.Scheme}://{Request.Host}{Request.Path}{Request.QueryString}");
        var tenantId = Request.Headers.TryGetValue("x-ms-tenant-id", out var t) ? t.ToString() : string.Empty;
        var scenarioId = Request.Headers.TryGetValue("ScenarioId", out var s) && Guid.TryParse(s, out var sg) ? sg : Guid.NewGuid();
        var requestId = Request.Headers.TryGetValue("client-request-id", out var r) && Guid.TryParse(r, out var rg) ? rg : Guid.NewGuid();

        client.ProcessNotifications(requestUri, notifications, tenantId, scenarioId, requestId, null);

        return Accepted();
    }
}