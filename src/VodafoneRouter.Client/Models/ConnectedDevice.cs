namespace VodafoneRouter.Models;

/// <summary>
/// A device known to the router (LAN or Wi-Fi). Mirrors the <c>USERDevice</c> JS
/// objects the router emits from <c>getLanUserDevInfo_ptvdf.asp</c>.
/// </summary>
public sealed record ConnectedDevice(
    string Domain,
    string IpAddress,
    string MacAddress,
    string Port,
    string PortType,
    string IpAcquisitionType,
    string DeviceType,
    bool IsActive,
    string HostName,
    string? Alias,
    bool IPv4Enabled,
    bool IPv6Enabled,
    string? OnlineDuration,
    string? LeaseTimeRemaining)
{
    public bool IsWireless => string.Equals(PortType, "WiFi", StringComparison.OrdinalIgnoreCase)
        || Port.StartsWith("SSID", StringComparison.OrdinalIgnoreCase);
}
