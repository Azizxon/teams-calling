using System.Text.Json;
using teams_streaming_call.Models;

namespace teams_streaming_call.Services;

public sealed class CallNotificationProcessor : ICallNotificationProcessor
{
    private readonly ICallNotificationArchiver archiver;
    private readonly ICallSessionStore store;
    private readonly IMediaCaptureCoordinator mediaCaptureCoordinator;
    private readonly IIncomingCallResponder incomingCallResponder;
    private readonly ILogger<CallNotificationProcessor> logger;

    public CallNotificationProcessor(
        ICallNotificationArchiver archiver,
        ICallSessionStore store,
        IMediaCaptureCoordinator mediaCaptureCoordinator,
        IIncomingCallResponder incomingCallResponder,
        ILogger<CallNotificationProcessor> logger)
    {
        this.archiver = archiver;
        this.store = store;
        this.mediaCaptureCoordinator = mediaCaptureCoordinator;
        this.incomingCallResponder = incomingCallResponder;
        this.logger = logger;
    }

    public async Task<IReadOnlyList<CallSessionSnapshot>> ProcessAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var rawJson = payload.GetRawText();
        var notifications = ExtractNotifications(payload, rawJson);
        var snapshots = new List<CallSessionSnapshot>(notifications.Count);

        foreach (var notification in notifications)
        {
            var archivePath = await archiver.ArchiveAsync(notification.CallId ?? "unknown", notification.RawJson, cancellationToken);

            string? mediaCaptureNote = null;

            if (IsIncoming(notification.CallState))
            {
                // Prepare media config before answer so Graph can route app-hosted media.
                mediaCaptureNote = await mediaCaptureCoordinator.PrepareCaptureAsync(notification, cancellationToken);
                await incomingCallResponder.TryAcceptAsync(notification, mediaCaptureNote, cancellationToken);
            }
            else if (IsEstablished(notification.CallState) || IsParticipantUpdate(notification.Resource))
            {
                // Keep the existing session (or lazily recover one) after the call is established.
                mediaCaptureNote = await mediaCaptureCoordinator.PrepareCaptureAsync(notification, cancellationToken);
            }
            else if (IsTerminated(notification.CallState))
            {
                await mediaCaptureCoordinator.StopCaptureAsync(notification.CallId ?? "unknown", cancellationToken);
            }

            var snapshot = store.Upsert(notification, archivePath, mediaCaptureNote);
            snapshots.Add(snapshot);

            logger.LogInformation(
                "Processed notification {NotificationId} for call {CallId} with state {State} and modalities [{Modalities}]",
                notification.NotificationId,
                snapshot.CallId,
                snapshot.State,
                string.Join(", ", snapshot.Modalities));
        }

        return snapshots;
    }

    private static List<CallNotificationRecord> ExtractNotifications(JsonElement payload, string rawJson)
    {
        var receivedAt = DateTimeOffset.UtcNow;
        var notifications = new List<CallNotificationRecord>();

        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("value", out var valueElement) &&
            valueElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in valueElement.EnumerateArray())
            {
                notifications.Add(ParseNotification(item, item.GetRawText(), receivedAt));
            }

            return notifications;
        }

        notifications.Add(ParseNotification(payload, rawJson, receivedAt));
        return notifications;
    }

    private static CallNotificationRecord ParseNotification(JsonElement item, string rawJson, DateTimeOffset receivedAt)
    {
        var notificationId = ReadString(item, "id");
        var changeType = ReadString(item, "changeType");
        var resource = ReadString(item, "resource");
        var topLevelTenantId = ReadString(item, "tenantId");

        JsonElement resourceData = default;
        var hasResourceData = item.ValueKind == JsonValueKind.Object &&
            item.TryGetProperty("resourceData", out resourceData) &&
            resourceData.ValueKind == JsonValueKind.Object;

        var tenantId = topLevelTenantId ?? (hasResourceData ? ReadString(resourceData, "tenantId") : null);

        var callId = hasResourceData
            ? ReadString(resourceData, "id") ?? ExtractCallId(resource)
            : ExtractCallId(resource);

        var callState = hasResourceData
            ? ReadString(resourceData, "state")
            : null;

        var modalities = hasResourceData
            ? ReadStringArray(resourceData, "requestedModalities")
            : Array.Empty<string>();

        return new CallNotificationRecord(
            notificationId,
            changeType,
            resource,
            tenantId,
            callId,
            callState,
            modalities,
            receivedAt,
            rawJson);
    }

    private static string? ExtractCallId(string? resource)
    {
        if (string.IsNullOrWhiteSpace(resource))
        {
            return null;
        }

        var segments = resource.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Resource paths can include nested nodes like /app/calls/{id}/participants.
        // Prefer the segment right after "calls" so all call-scoped notifications
        // are consistently correlated to the same call session.
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("calls", StringComparison.OrdinalIgnoreCase))
            {
                return segments[i + 1];
            }
        }

        return segments.LastOrDefault();
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Null => null,
            _ => property.ToString(),
        };
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
        {
            return Array.Empty<string>();
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            var singleValue = property.ToString();
            return string.IsNullOrWhiteSpace(singleValue) ? Array.Empty<string>() : new[] { singleValue };
        }

        return property.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();
    }

    private static bool IsIncoming(string? state) =>
        state is not null &&
        state.Equals("incoming", StringComparison.OrdinalIgnoreCase);

    private static bool IsEstablished(string? state) =>
        state is not null &&
        state.Equals("established", StringComparison.OrdinalIgnoreCase);

    private static bool IsTerminated(string? state) =>
        state is not null &&
        (state.Equals("terminated", StringComparison.OrdinalIgnoreCase) ||
         state.Equals("disconnected", StringComparison.OrdinalIgnoreCase));

    private static bool IsParticipantUpdate(string? resource)
    {
        if (string.IsNullOrWhiteSpace(resource))
        {
            return false;
        }

        return resource.Contains("/participants", StringComparison.OrdinalIgnoreCase);
    }
}