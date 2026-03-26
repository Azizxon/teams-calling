namespace teams_streaming_call.Models;

public sealed record CallSessionSnapshot(
    string CallId,
    string? TenantId,
    string? Resource,
    string? State,
    IReadOnlyList<string> Modalities,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastUpdatedUtc,
    int NotificationCount,
    string? LastNotificationId,
    bool MediaCaptureRequested,
    string? LastArchivePath,
    string? LastMediaCaptureNote);