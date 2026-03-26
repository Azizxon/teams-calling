using System.Collections.Concurrent;
using teams_streaming_call.Models;

namespace teams_streaming_call.Services;

public sealed class InMemoryCallSessionStore : ICallSessionStore
{
    private readonly ConcurrentDictionary<string, MutableCallSession> sessions = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<CallSessionSnapshot> GetAll()
    {
        return sessions.Values
            .Select(session => session.ToSnapshot())
            .OrderByDescending(session => session.LastUpdatedUtc)
            .ToArray();
    }

    public CallSessionSnapshot? Get(string callId)
    {
        return sessions.TryGetValue(callId, out var session)
            ? session.ToSnapshot()
            : null;
    }

    public CallSessionSnapshot Upsert(CallNotificationRecord notification, string? archivePath, string? mediaCaptureNote)
    {
        var key = string.IsNullOrWhiteSpace(notification.CallId)
            ? $"unknown:{notification.ReceivedAtUtc:yyyyMMddHHmmssfff}:{Guid.NewGuid():N}"
            : notification.CallId;

        var session = sessions.AddOrUpdate(
            key,
            _ => MutableCallSession.Create(notification, archivePath, mediaCaptureNote),
            (_, existing) => existing.Update(notification, archivePath, mediaCaptureNote));

        return session.ToSnapshot();
    }

    private sealed class MutableCallSession
    {
        private readonly HashSet<string> modalities;

        private MutableCallSession(
            string callId,
            string? tenantId,
            string? resource,
            string? state,
            IEnumerable<string> modalities,
            DateTimeOffset firstSeenUtc,
            DateTimeOffset lastUpdatedUtc,
            int notificationCount,
            string? lastNotificationId,
            bool mediaCaptureRequested,
            string? lastArchivePath,
            string? lastMediaCaptureNote)
        {
            CallId = callId;
            TenantId = tenantId;
            Resource = resource;
            State = state;
            this.modalities = new HashSet<string>(modalities, StringComparer.OrdinalIgnoreCase);
            FirstSeenUtc = firstSeenUtc;
            LastUpdatedUtc = lastUpdatedUtc;
            NotificationCount = notificationCount;
            LastNotificationId = lastNotificationId;
            MediaCaptureRequested = mediaCaptureRequested;
            LastArchivePath = lastArchivePath;
            LastMediaCaptureNote = lastMediaCaptureNote;
        }

        public string CallId { get; }

        public string? TenantId { get; private set; }

        public string? Resource { get; private set; }

        public string? State { get; private set; }

        public DateTimeOffset FirstSeenUtc { get; }

        public DateTimeOffset LastUpdatedUtc { get; private set; }

        public int NotificationCount { get; private set; }

        public string? LastNotificationId { get; private set; }

        public bool MediaCaptureRequested { get; private set; }

        public string? LastArchivePath { get; private set; }

        public string? LastMediaCaptureNote { get; private set; }

        public static MutableCallSession Create(CallNotificationRecord notification, string? archivePath, string? mediaCaptureNote)
        {
            return new MutableCallSession(
                notification.CallId ?? "unknown",
                notification.TenantId,
                notification.Resource,
                notification.CallState,
                notification.Modalities,
                notification.ReceivedAtUtc,
                notification.ReceivedAtUtc,
                1,
                notification.NotificationId,
                !string.IsNullOrWhiteSpace(mediaCaptureNote),
                archivePath,
                mediaCaptureNote);
        }

        public MutableCallSession Update(CallNotificationRecord notification, string? archivePath, string? mediaCaptureNote)
        {
            TenantId = notification.TenantId ?? TenantId;
            Resource = notification.Resource ?? Resource;
            State = notification.CallState ?? State;
            LastUpdatedUtc = notification.ReceivedAtUtc;
            NotificationCount += 1;
            LastNotificationId = notification.NotificationId ?? LastNotificationId;
            LastArchivePath = archivePath ?? LastArchivePath;
            LastMediaCaptureNote = mediaCaptureNote ?? LastMediaCaptureNote;
            MediaCaptureRequested |= !string.IsNullOrWhiteSpace(mediaCaptureNote);

            foreach (var modality in notification.Modalities)
            {
                if (!string.IsNullOrWhiteSpace(modality))
                {
                    modalities.Add(modality);
                }
            }

            return this;
        }

        public CallSessionSnapshot ToSnapshot()
        {
            return new CallSessionSnapshot(
                CallId,
                TenantId,
                Resource,
                State,
                modalities.OrderBy(item => item).ToArray(),
                FirstSeenUtc,
                LastUpdatedUtc,
                NotificationCount,
                LastNotificationId,
                MediaCaptureRequested,
                LastArchivePath,
                LastMediaCaptureNote);
        }
    }
}