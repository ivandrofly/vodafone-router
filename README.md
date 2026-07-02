# VodafoneRouter.Client

A .NET 8 client for the **Vodafone Smart Router** (Huawei OEM, firmware family
`V5R020C10S230` — the "192.168.1.1" admin UI shipped by Vodafone Portugal and
several other Vodafone markets).

It speaks the same HTTP API as the web UI: form-based login (`/login.cgi`),
per-request anti-CSRF tokens (`/asp/GetRandCount.asp`), TR-069-style action
endpoints (`/set.cgi?x=InternetGatewayDevice...`), and inline-JS data pages
(`/html/.../*.asp`) — the data is parsed with a sandboxed Jint engine so we
get strongly typed results without scraping HTML.

## Quick start

```csharp
using VodafoneRouter;

await using var client = new VodafoneRouterClient("http://192.168.1.1/");
await client.LoginAsync("vodafone", "your-password");

var overview = await client.GetOverviewAsync();
Console.WriteLine($"Firmware {overview.FirmwareVersion}, WAN IPv4 {overview.WanIPv4}");

foreach (var d in await client.GetConnectedDevicesAsync())
    Console.WriteLine($"{d.HostName,-20} {d.IpAddress,-15} {d.MacAddress}");
```

## Currently implemented

| Operation                       | Method                            |
| ------------------------------- | --------------------------------- |
| Login / logout                  | `LoginAsync`, `LogoutAsync`       |
| Router overview / firmware      | `GetOverviewAsync`                |
| Connected devices (LAN + Wi-Fi) | `GetConnectedDevicesAsync`        |
| WAN status & statistics         | `GetWanStatusAsync`               |
| Wi-Fi radio/SSID list           | `GetWlanConfigAsync`              |
| Reboot                          | `RebootAsync`                     |
| Raw HTML / ad-hoc TR-069 action | `GetRawHtmlAsync`, `ExecuteCommandAsync` |

The escape hatches make it easy to add wrappers for any other UI feature
(Wi-Fi rename, port forwarding, DMZ, parental control, DDNS, UPnP, USB
sharing, call log, speed test, ...) — the same login session covers them.

## How it works

1. `GET /` to seed cookies, then `POST /asp/GetRandCount.asp` to get a
   48-hex anti-CSRF token (the router calls it `x.X_HW_Token`).
2. `POST /login.cgi` with `UserName`, `PassWord` (Base64), `Language`,
   and the token. The router sets an HttpOnly session cookie.
3. Read endpoints return HTML with embedded JavaScript like
   `var IPWanList = new Array(new IPWANInfo(...), ...);`. We extract the
   `<script>` blocks, run them through a Jint sandbox that stubs unknown
   constructors as positional-arg capturers, and read the resulting
   variables back as plain dictionaries/lists.
4. Write endpoints (`/set.cgi?x=<TR-069-path>`) require a fresh token for
   each call; `RouterSession.ExecuteCommandAsync` handles that.

## Project layout

```
src/VodafoneRouter.Client/    class library
samples/VodafoneRouter.Sample/   console app demo
```

## Sample app

```powershell
$env:VODAFONE_ROUTER_PASS = '<your password>'
dotnet run --project samples/VodafoneRouter.Sample
```
