using VodafoneRouter.Internal;
using VodafoneRouter.Models;

namespace VodafoneRouter;

/// <summary>
/// .NET client for the Vodafone Smart Router web admin (Huawei OEM, firmware
/// family <c>V5R020C10S230</c>).
/// </summary>
/// <remarks>
/// Usage:
/// <code>
/// using var client = new VodafoneRouterClient();
/// await client.LoginAsync("vodafone", "your-password");
/// var devices = await client.GetConnectedDevicesAsync();
/// </code>
/// </remarks>
public sealed class VodafoneRouterClient : IAsyncDisposable, IDisposable
{
    private readonly RouterSession _session;

    public VodafoneRouterClient(string baseUrl = "http://192.168.1.1/", string language = "english", HttpClient? httpClient = null)
        : this(new Uri(baseUrl, UriKind.Absolute), language, httpClient)
    {
    }

    public VodafoneRouterClient(Uri baseUrl, string language = "english", HttpClient? httpClient = null)
    {
        _session = new RouterSession(baseUrl, language, httpClient);
    }

    public Uri BaseUrl => _session.BaseUrl;
    public bool IsAuthenticated => _session.IsAuthenticated;

    public Task LoginAsync(string username, string password, CancellationToken ct = default)
        => _session.LoginAsync(username, password, ct);

    public Task LogoutAsync(CancellationToken ct = default)
        => _session.LogoutAsync(ct);

    /// <summary>
    /// Reads firmware version, model, uptime and WAN IPs by parsing the
    /// inline scripts on <c>/index.asp</c> and the WAN list include.
    /// </summary>
    public async Task<RouterOverview> GetOverviewAsync(CancellationToken ct = default)
    {
        var indexHtml = await _session.GetHtmlAsync("/index.asp", ct).ConfigureAwait(false);
        var wanScript = await _session.GetConcatenatedAsync(
                new[] { "/html/bbsp/common/wan_list_info.asp", "/html/bbsp/common/wan_list.asp" }, ct)
            .ConfigureAwait(false);

        var indexJs = EmbeddedJsParser.ExtractInlineScripts(indexHtml);
        var combined = wanScript + "\n" + indexJs;

        var vars = EmbeddedJsParser.ExtractVariables(
            combined,
            new[] { "deviceInfos", "productName", "WanList" });

        // deviceInfos[0] is built by `function stDeviceInfo(domain, SoftwareVersion, UpTimeStr)`
        // which the page itself defines, so we read the resulting field names.
        var info = EmbeddedJsParser.AsRecords(vars.GetValueOrDefault("deviceInfos")).FirstOrDefault();
        var firmware = EmbeddedJsParser.Str(info?.GetValueOrDefault("SoftwareVersion"))
                       ?? EmbeddedJsParser.Str(info?.GetValueOrDefault("FirmwareVersion"))
                       ?? "";
        var uptime = EmbeddedJsParser.Str(info?.GetValueOrDefault("UpTimeStr"))
                     ?? EmbeddedJsParser.Str(info?.GetValueOrDefault("Uptime"));

        var product = EmbeddedJsParser.Str(vars.GetValueOrDefault("productName")) ?? "Vodafone Smart Router";

        // WAN IPv4: first WanList entry that carries the Internet service.
        var internetWan = EmbeddedJsParser.AsRecords(vars.GetValueOrDefault("WanList"))
            .FirstOrDefault(r =>
                (EmbeddedJsParser.Str(r.GetValueOrDefault("ServiceList")) ?? "")
                .Contains("INTERNET", StringComparison.OrdinalIgnoreCase));
        var wanV4 = internetWan is null ? null : EmbeddedJsParser.Str(internetWan.GetValueOrDefault("IPv4IPAddress"));

        // WAN IPv6: best-effort fetch from the IPv6 include.
        string? wanV6 = null;
        try
        {
            var v6 = await _session.GetHtmlAsync("/html/bbsp/common/wanipv6state.asp", ct).ConfigureAwait(false);
            var v6Vars = EmbeddedJsParser.ExtractVariables(v6, new[] { "IPv6AddressList" });
            wanV6 = EmbeddedJsParser.AsRecords(v6Vars.GetValueOrDefault("IPv6AddressList"))
                .Select(r => EmbeddedJsParser.Str(r.GetValueOrDefault("IPAddress")))
                .FirstOrDefault(ip => !string.IsNullOrEmpty(ip));
        }
        catch
        {
            /* IPv6 status is optional */
        }

        return new RouterOverview(firmware, wanV4, wanV6, uptime, product);
    }

    /// <summary>
    /// Returns every device the router knows about (LAN + Wi-Fi). Data comes
    /// from <c>/html/bbsp/common/getLanUserDevInfo_ptvdf.asp</c>, which is a
    /// JS file (not HTML) with a top-level <c>UserDevinfo</c> array.
    /// </summary>
    public async Task<IReadOnlyList<ConnectedDevice>> GetConnectedDevicesAsync(CancellationToken ct = default)
    {
        var js = await _session.GetHtmlAsync(
            "/html/bbsp/common/getLanUserDevInfo_ptvdf.asp", ct).ConfigureAwait(false);

        var maps = new Dictionary<string, IReadOnlyList<string>>
        {
            // Constructor signature in the script:
            //   USERDevice(Domain,IpAddr,MacAddr,Port,IpType,DevType,DevStatus,
            //              PortType,Time,HostName,IPv4Enabled,IPv6Enabled,
            //              DeviceType,UserDevAlias,UserSpecifiedDeviceType,LeaseTimeRemaining)
            ["USERDevice"] = new[]
            {
                "Domain", "IpAddr", "MacAddr", "Port", "IpType", "DevType", "DevStatus",
                "PortType", "Time", "HostName", "IPv4Enabled", "IPv6Enabled",
                "DeviceType", "UserDevAlias", "UserSpecifiedDeviceType", "LeaseTimeRemaining"
            }
        };

        var vars = EmbeddedJsParser.ExtractVariables(js, new[] { "UserDevinfo" }, maps);
        var rows = EmbeddedJsParser.AsRecords(vars.GetValueOrDefault("UserDevinfo"));

        var result = new List<ConnectedDevice>(rows.Count);
        foreach (var d in rows)
        {
            result.Add(new ConnectedDevice(
                Domain: EmbeddedJsParser.Str(d.GetValueOrDefault("Domain")) ?? "",
                IpAddress: EmbeddedJsParser.Str(d.GetValueOrDefault("IpAddr")) ?? "",
                MacAddress: EmbeddedJsParser.Str(d.GetValueOrDefault("MacAddr")) ?? "",
                Port: EmbeddedJsParser.Str(d.GetValueOrDefault("Port")) ?? "",
                PortType: EmbeddedJsParser.Str(d.GetValueOrDefault("PortType")) ?? "",
                IpAcquisitionType: EmbeddedJsParser.Str(d.GetValueOrDefault("IpType")) ?? "",
                DeviceType: EmbeddedJsParser.Str(d.GetValueOrDefault("DeviceType"))
                            ?? EmbeddedJsParser.Str(d.GetValueOrDefault("DevType"))
                            ?? "",
                IsActive: string.Equals(EmbeddedJsParser.Str(d.GetValueOrDefault("DevStatus")), "Online", StringComparison.OrdinalIgnoreCase),
                HostName: EmbeddedJsParser.Str(d.GetValueOrDefault("HostName")) ?? "",
                Alias: NullIfDash(EmbeddedJsParser.Str(d.GetValueOrDefault("UserDevAlias"))),
                IPv4Enabled: EmbeddedJsParser.Bool(d.GetValueOrDefault("IPv4Enabled")),
                IPv6Enabled: EmbeddedJsParser.Bool(d.GetValueOrDefault("IPv6Enabled")),
                OnlineDuration: NullIfDash(EmbeddedJsParser.Str(d.GetValueOrDefault("Time"))),
                LeaseTimeRemaining: NullIfDash(EmbeddedJsParser.Str(d.GetValueOrDefault("LeaseTimeRemaining")))));
        }

        return result;
    }

    /// <summary>
    /// Reads WAN status (per-connection statistics, IPs, uptime) by combining
    /// <c>/html/bbsp/common/wan_list.asp</c> (definitions) with the per-WAN
    /// statistics embedded in <c>wanStatus_ptvdf.asp</c>.
    /// </summary>
    public async Task<IReadOnlyList<WanConnection>> GetWanStatusAsync(CancellationToken ct = default)
    {
        var combined = await _session.GetConcatenatedAsync(new[]
        {
            "/html/bbsp/common/wan_list_info.asp",
            "/html/bbsp/common/wan_list.asp",
        }, ct).ConfigureAwait(false);

        var vars = EmbeddedJsParser.ExtractVariables(combined, new[] { "WanList" });
        var wans = EmbeddedJsParser.AsRecords(vars.GetValueOrDefault("WanList"));

        var result = new List<WanConnection>();
        foreach (var w in wans)
        {
            // Field discovery: WanList rows expose IPv4IPAddress, IPv4PrimaryDNS,
            // BytesSent/Received and PacketsSent/Received directly.
            var dns1 = EmbeddedJsParser.Str(w.GetValueOrDefault("IPv4PrimaryDNS"));
            var dns2 = EmbeddedJsParser.Str(w.GetValueOrDefault("IPv4SecondaryDNS"));
            var dns = string.Join(" ", new[] { dns1, dns2 }.Where(d => !string.IsNullOrEmpty(d)));

            result.Add(new WanConnection(
                Domain: EmbeddedJsParser.Str(w.GetValueOrDefault("domain")) ?? "",
                Name: EmbeddedJsParser.Str(w.GetValueOrDefault("RealName"))
                      ?? EmbeddedJsParser.Str(w.GetValueOrDefault("Name")) ?? "",
                Status: EmbeddedJsParser.Str(w.GetValueOrDefault("Status")) ?? "",
                ConnectionType: EmbeddedJsParser.Str(w.GetValueOrDefault("IPv4AddressMode"))
                                ?? EmbeddedJsParser.Str(w.GetValueOrDefault("ProtocolType"))
                                ?? "",
                IpAddress: EmbeddedJsParser.Str(w.GetValueOrDefault("IPv4IPAddress")),
                Gateway: EmbeddedJsParser.Str(w.GetValueOrDefault("IPv4Gateway")),
                Dns: string.IsNullOrEmpty(dns) ? null : dns,
                MacAddress: EmbeddedJsParser.Str(w.GetValueOrDefault("MACAddress")),
                Uptime: EmbeddedJsParser.Long(w.GetValueOrDefault("Uptime")),
                BytesSent: EmbeddedJsParser.Long(w.GetValueOrDefault("BytesSent")),
                BytesReceived: EmbeddedJsParser.Long(w.GetValueOrDefault("BytesReceived")),
                PacketsSent: EmbeddedJsParser.Long(w.GetValueOrDefault("PacketsSent")),
                PacketsReceived: EmbeddedJsParser.Long(w.GetValueOrDefault("PacketsReceived"))));
        }

        return result;
    }

    /// <summary>
    /// Lists configured Wi-Fi SSIDs (radios + guest) and their enable state.
    /// </summary>
    public async Task<IReadOnlyList<WlanConfig>> GetWlanConfigAsync(CancellationToken ct = default)
    {
        // WlanInfo lives in the wan_list.asp include alongside the WAN data.
        var combined = await _session.GetConcatenatedAsync(new[]
        {
            "/html/bbsp/common/wan_list_info.asp",
            "/html/bbsp/common/wan_list.asp",
        }, ct).ConfigureAwait(false);

        var vars = EmbeddedJsParser.ExtractVariables(combined, new[] { "WlanInfo" });
        var rows = EmbeddedJsParser.AsRecords(vars.GetValueOrDefault("WlanInfo"));
        var result = new List<WlanConfig>(rows.Count);
        foreach (var r in rows)
        {
            result.Add(new WlanConfig(
                Domain: EmbeddedJsParser.Str(r.GetValueOrDefault("domain")) ?? "",
                InterfaceName: EmbeddedJsParser.Str(r.GetValueOrDefault("name")) ?? "",
                ServiceEnabled: EmbeddedJsParser.Bool(r.GetValueOrDefault("X_HW_ServiceEnable")),
                RadioEnabled: EmbeddedJsParser.Bool(r.GetValueOrDefault("enable"))));
        }

        return result;
    }

    /// <summary>
    /// Reboots the router. Returns once the request is accepted; the device
    /// itself will become unreachable for ~60–120 s afterwards.
    /// </summary>
    public Task RebootAsync(CancellationToken ct = default)
        => _session.ExecuteCommandAsync(
            tr069Path: "InternetGatewayDevice.X_HW_DEBUG.SMP.DM.ResetBoard",
            requestFile: "html/ssmp/reset/resetptvdf.asp",
            extra: null,
            ct);

    /// <summary>
    /// Escape hatch for endpoints not yet wrapped: returns the raw HTML of any
    /// admin page (must be authenticated).
    /// </summary>
    public Task<string> GetRawHtmlAsync(string path, CancellationToken ct = default)
        => _session.GetHtmlAsync(path, ct);

    /// <summary>
    /// Escape hatch for ad-hoc TR-069 actions. See <c>RebootAsync</c> for an example.
    /// </summary>
    public Task<string> ExecuteCommandAsync(
        string tr069Path,
        string requestFile,
        IDictionary<string, string>? extraParameters = null,
        CancellationToken ct = default)
        => _session.ExecuteCommandAsync(tr069Path, requestFile, extraParameters, ct);

    private static string? NullIfDash(string? s)
        => string.IsNullOrEmpty(s) || s == "--" ? null : s;

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _session.LogoutAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }

        _session.Dispose();
    }

    public void Dispose()
    {
        try
        {
            _session.LogoutAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
        }

        _session.Dispose();
    }
}