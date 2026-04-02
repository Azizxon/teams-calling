using Microsoft.AspNetCore.Mvc;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;

namespace teams_streaming_call.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class MessagesController : ControllerBase
{
    private readonly IBotFrameworkHttpAdapter _adapter;
    private readonly IBot _bot;

    public MessagesController(IBotFrameworkHttpAdapter adapter, IBot bot)
    {
        _adapter = adapter;
        _bot = bot;
    }

    /// <summary>
    /// Receives Bot Framework Activity payloads sent by the Teams channel
    /// (or the Bot Framework Emulator) to the /api/messages endpoint.
    /// </summary>
    [HttpPost]
    public async Task PostAsync(CancellationToken cancellationToken)
        => await _adapter.ProcessAsync(Request, Response, _bot, cancellationToken);
}
