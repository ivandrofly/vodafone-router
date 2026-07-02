namespace VodafoneRouter.Models;

public sealed record WanConnection(
    string Domain,
    string Name,
    string Status,
    string ConnectionType,
    string? IpAddress,
    string? Gateway,
    string? Dns,
    string? MacAddress,
    long Uptime,
    long BytesSent,
    long BytesReceived,
    long PacketsSent,
    long PacketsReceived);
