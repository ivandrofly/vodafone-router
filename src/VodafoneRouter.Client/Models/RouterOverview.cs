namespace VodafoneRouter.Models;

public sealed record RouterOverview(
    string FirmwareVersion,
    string? WanIPv4,
    string? WanIPv6,
    string? UptimeText,
    string? ProductName);
