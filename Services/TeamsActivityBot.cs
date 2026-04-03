using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Teams;
using Microsoft.Bot.Schema;
using Microsoft.Bot.Schema.Teams;

namespace teams_streaming_call.Services;

/// <summary>
/// Handles Bot Framework Activity events forwarded from the /api/messages endpoint.
/// </summary>
public sealed class TeamsActivityBot : TeamsActivityHandler
{
    private readonly BotCallService _botService;
    private readonly ILogger<TeamsActivityBot> _logger;

    public TeamsActivityBot(BotCallService botService, ILogger<TeamsActivityBot> logger)
    {
        _botService = botService;
        _logger = logger;
    }

    protected override async Task OnMessageActivityAsync(ITurnContext<IMessageActivity> turnContext, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Message received: {Text}", turnContext.Activity.Text);
        var replyText = $"Echo: {turnContext.Activity.Text}";
        await turnContext.SendActivityAsync(MessageFactory.Text(replyText), cancellationToken);
    }

    protected override async Task OnTeamsMeetingStartAsync(
        MeetingStartEventDetails meeting,
        ITurnContext<IEventActivity> turnContext,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Meeting started: {MeetingId}", meeting.Id);
        var callId = meeting.Id;
        var joinUrl = meeting.JoinUrl.AbsoluteUri;
        var displayName = meeting.Title;
        await _botService.JoinCallAsync(callId, joinUrl, displayName).ConfigureAwait(false);
    }

    protected override async Task OnTeamsMeetingEndAsync(
        MeetingEndEventDetails meeting,
        ITurnContext<IEventActivity> turnContext,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Meeting ended: {MeetingId}", meeting.Id);
    }
}
