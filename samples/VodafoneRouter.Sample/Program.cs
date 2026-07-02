using VodafoneRouter;

// Reads credentials from env vars so we don't bake them into source control.
var routerUrl = Environment.GetEnvironmentVariable("VODAFONE_ROUTER_URL") ?? "http://192.168.1.1/";
var username = Environment.GetEnvironmentVariable("VODAFONE_ROUTER_USER") ?? "vodafone";
var password = Environment.GetEnvironmentVariable("VODAFONE_ROUTER_PASS")
    ?? throw new InvalidOperationException("Set VODAFONE_ROUTER_PASS env var.");

await using var client = new VodafoneRouterClient(routerUrl);

Console.WriteLine($"Logging in to {client.BaseUrl} as {username}...");
await client.LoginAsync(username, password);
Console.WriteLine("OK");

Console.WriteLine();
Console.WriteLine("== Overview ==");
var overview = await client.GetOverviewAsync();
Console.WriteLine($"  Product:  {overview.ProductName}");
Console.WriteLine($"  Firmware: {overview.FirmwareVersion}");
Console.WriteLine($"  WAN IPv4: {overview.WanIPv4}");
Console.WriteLine($"  WAN IPv6: {overview.WanIPv6}");
Console.WriteLine($"  Uptime:   {overview.UptimeText}");

Console.WriteLine();
Console.WriteLine("== WAN Connections ==");
foreach (var wan in await client.GetWanStatusAsync())
{
    Console.WriteLine($"  [{wan.Name,-10}] {wan.Status,-10} type={wan.ConnectionType,-6} " +
                      $"ip={wan.IpAddress ?? "-"} rx={wan.BytesReceived} tx={wan.BytesSent}");
}

Console.WriteLine();
Console.WriteLine("== Wi-Fi radios / SSIDs ==");
foreach (var w in await client.GetWlanConfigAsync())
{
    Console.WriteLine($"  {w.InterfaceName,-6} service={w.ServiceEnabled} radio={w.RadioEnabled}  ({w.Domain})");
}

Console.WriteLine();
Console.WriteLine("== Connected devices ==");
var devices = await client.GetConnectedDevicesAsync();
Console.WriteLine($"  total={devices.Count}, active={devices.Count(d => d.IsActive)}");
foreach (var d in devices.Where(d => d.IsActive).Take(15))
{
    Console.WriteLine($"  {d.HostName,-22} {d.IpAddress,-15} {d.MacAddress,-17} " +
                      $"{(d.IsWireless ? "wifi" : "lan"),4}  {d.Alias ?? ""}");
}

// Reboot is destructive, so it's commented out by default:
// Console.WriteLine("Rebooting...");
// await client.RebootAsync();