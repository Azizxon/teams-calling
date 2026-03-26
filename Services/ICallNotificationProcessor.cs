using System.Text.Json;
using teams_streaming_call.Models;

namespace teams_streaming_call.Services;

public interface ICallNotificationProcessor
{
    Task<IReadOnlyList<CallSessionSnapshot>> ProcessAsync(JsonElement payload, CancellationToken cancellationToken);
}