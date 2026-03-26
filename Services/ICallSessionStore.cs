using teams_streaming_call.Models;

namespace teams_streaming_call.Services;

public interface ICallSessionStore
{
    CallSessionSnapshot Upsert(CallNotificationRecord notification, string? archivePath, string? mediaCaptureNote);

    IReadOnlyCollection<CallSessionSnapshot> GetAll();

    CallSessionSnapshot? Get(string callId);
}