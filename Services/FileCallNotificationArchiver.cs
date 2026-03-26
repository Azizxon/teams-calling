using Microsoft.Extensions.Options;
using teams_streaming_call.Configuration;

namespace teams_streaming_call.Services;

public sealed class FileCallNotificationArchiver : ICallNotificationArchiver
{
    private readonly TeamsCallBotOptions options;
    private readonly ILogger<FileCallNotificationArchiver> logger;
    private readonly string contentRoot;

    public FileCallNotificationArchiver(
        IOptions<TeamsCallBotOptions> options,
        IWebHostEnvironment environment,
        ILogger<FileCallNotificationArchiver> logger)
    {
        this.options = options.Value;
        this.logger = logger;
        contentRoot = environment.ContentRootPath;
    }

    public async Task<string?> ArchiveAsync(string callId, string rawJson, CancellationToken cancellationToken)
    {
        if (!options.PersistRawNotifications)
        {
            return null;
        }

        var safeCallId = string.IsNullOrWhiteSpace(callId) ? "unknown" : callId.Replace(':', '-').Replace('/', '-');
        var directory = Path.Combine(contentRoot, options.CaptureRoot, "signaling", DateTime.UtcNow.ToString("yyyyMMdd"));
        Directory.CreateDirectory(directory);

        var fileName = $"{DateTime.UtcNow:HHmmssfff}-{safeCallId}-{Guid.NewGuid():N}.json";
        var path = Path.Combine(directory, fileName);

        await File.WriteAllTextAsync(path, rawJson, cancellationToken);
        logger.LogInformation("Archived call notification for {CallId} to {Path}", safeCallId, path);

        return path;
    }
}