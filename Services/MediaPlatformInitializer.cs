using System.Runtime.Versioning;
using System.Net;
using Microsoft.Extensions.Options;
using Microsoft.Skype.Bots.Media;
using teams_streaming_call.Configuration;

namespace teams_streaming_call.Services;

/// <summary>
/// Hosted service that calls <c>MediaPlatform.Initialize</c> once at startup on Windows.
/// On non-Windows hosts it logs a warning and does nothing, letting the rest of the
/// application continue in signaling-only mode.
/// </summary>
internal sealed class MediaPlatformInitializer : IHostedService
{
    private readonly TeamsCallBotOptions _options;
    private readonly ILogger<MediaPlatformInitializer> _logger;

    public MediaPlatformInitializer(
        IOptions<TeamsCallBotOptions> options,
        ILogger<MediaPlatformInitializer> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.EnableWindowsMediaCapture)
        {
            _logger.LogInformation(
                "Media platform initialization skipped (TeamsCallBot:EnableWindowsMediaCapture = false).");
            return Task.CompletedTask;
        }

        if (!OperatingSystem.IsWindows())
        {
            _logger.LogWarning(
                "Windows media capture is enabled in config but the host OS is {OS}. " +
                "The Microsoft.Skype.Bots.Media native library only runs on Windows. " +
                "Media platform will NOT be initialized.",
                Environment.OSVersion.Platform);
            return Task.CompletedTask;
        }

        InitializeOnWindows();
        return Task.CompletedTask;
    }

    [SupportedOSPlatform("windows")]
    private void InitializeOnWindows()
    {
        try
        {
            var publicIp = ResolvePublicIpAddress();

            MediaPlatform.Initialize(new MediaPlatformSettings
            {
                MediaPlatformInstanceSettings = new MediaPlatformInstanceSettings
                {
                    ServiceFqdn = _options.MediaServiceFqdn,
                    InstancePublicIPAddress = publicIp,
                    InstancePublicPort = _options.InstancePublicPort,
                    InstanceInternalPort = _options.InstanceInternalPort,
                    CertificateThumbprint = _options.CertificateThumbprint,
                },
                ApplicationId = _options.AadAppId,
            });

            _logger.LogInformation(
                "Media platform initialized. ServiceFqdn={Fqdn}, PublicIP={IP}, PublicPort={Port}",
                _options.MediaServiceFqdn,
                publicIp,
                _options.InstancePublicPort);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize the media platform.");
        }
    }

    private IPAddress ResolvePublicIpAddress()
    {
        if (IPAddress.TryParse(_options.InstancePublicIpAddress, out var configuredIp))
            return configuredIp;

        var hostCandidates = new[]
        {
            _options.ServiceDnsName,
            _options.MediaServiceFqdn,
            _options.ServiceCname,
        }
        .Where(static host => !string.IsNullOrWhiteSpace(host))
        .ToArray();

        foreach (var host in hostCandidates)
        {
            try
            {
                var addresses = Dns.GetHostAddresses(host);
                var ipv4 = addresses.FirstOrDefault(static ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                if (ipv4 is not null)
                {
                    _logger.LogInformation("Resolved public media IP {IP} from host {Host}", ipv4, host);
                    return ipv4;
                }
            }
            catch
            {
                // Try next candidate.
            }
        }

        throw new InvalidOperationException(
            "Could not determine InstancePublicIPAddress. Set TeamsCallBot:InstancePublicIpAddress explicitly " +
            "to the VM public IPv4 address.");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_options.EnableWindowsMediaCapture && OperatingSystem.IsWindows())
            ShutdownOnWindows();

        return Task.CompletedTask;
    }

    [SupportedOSPlatform("windows")]
    private void ShutdownOnWindows()
    {
        try
        {
            MediaPlatform.Shutdown();
            _logger.LogInformation("Media platform shut down.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during media platform shutdown.");
        }
    }
}
