using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using teams_streaming_call.Services;

namespace teams_streaming_call.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class CallsController : ControllerBase
{
    private readonly ICallNotificationProcessor processor;
    private readonly ICallSessionStore store;

    public CallsController(ICallNotificationProcessor processor, ICallSessionStore store)
    {
        this.processor = processor;
        this.store = store;
    }

    [HttpPost]
    public async Task<IActionResult> ReceiveAsync([FromBody] JsonElement payload, CancellationToken cancellationToken)
    {
        var sessions = await processor.ProcessAsync(payload, cancellationToken);

        return Accepted(new
        {
            received = sessions.Count,
            calls = sessions,
        });
    }

    [HttpGet]
    public IActionResult GetAll()
    {
        return Ok(store.GetAll());
    }

    [HttpGet("{callId}")]
    public IActionResult GetById(string callId)
    {
        var snapshot = store.Get(callId);
        return snapshot is null ? NotFound() : Ok(snapshot);
    }
}