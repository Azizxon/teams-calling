namespace teams_streaming_call.Models;

public sealed record CallNotificationRecord(
    string? NotificationId,
    string? ChangeType,
    string? Resource,
    string? TenantId,
    string? CallId,
    string? CallState,
    IReadOnlyList<string> Modalities,
    DateTimeOffset ReceivedAtUtc,
    string RawJson);