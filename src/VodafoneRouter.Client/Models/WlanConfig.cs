namespace VodafoneRouter.Models;

/// <summary>
/// A single Wi-Fi radio/SSID configuration entry as exposed by
/// <c>InternetGatewayDevice.LANDevice.1.WLANConfiguration.X</c>.
/// </summary>
public sealed record WlanConfig(
    string Domain,
    string InterfaceName,
    bool ServiceEnabled,
    bool RadioEnabled);
