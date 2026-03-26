using System.ComponentModel.DataAnnotations;

namespace teams_streaming_call.Configuration;

public sealed class TeamsCallBotOptions
{
    public const string SectionName = "TeamsCallBot";

    [Required]
    public string BotName { get; set; } = "ZeusBot";

    public string AadAppId { get; set; } = string.Empty;

    public string AadAppSecret { get; set; } = string.Empty;

    [Required]
    public string ServiceCname { get; set; } = string.Empty;

    [Required]
    public string MediaServiceFqdn { get; set; } = string.Empty;

     /// <summary>
    /// Public IPv4 of this Windows VM used by the media platform.
    /// If empty, the app will try to resolve ServiceDnsName or MediaServiceFqdn.
    /// </summary>
    public string InstancePublicIpAddress { get; set; } = string.Empty;

    [Required]
    public string ServiceDnsName { get; set; } = string.Empty;

    public string CertificateThumbprint { get; set; } = string.Empty;

    [Range(1, 65535)]
    public int InstancePublicPort { get; set; } = 18330;

    [Range(1, 65535)]
    public int CallSignalingPort { get; set; } = 9441;

    [Range(1, 65535)]
    public int InstanceInternalPort { get; set; } = 8445;

    [Required]
    [Url]
    public string PlaceCallEndpointUrl { get; set; } = "https://graph.microsoft.com/v1.0";

    [Required]
    public string CallsEndpointPath { get; set; } = "/api/calls";

    [Required]
    public string CaptureRoot { get; set; } = "captures";

    public bool PersistRawNotifications { get; set; } = true;

    public bool EnableWindowsMediaCapture { get; set; } = false;
}