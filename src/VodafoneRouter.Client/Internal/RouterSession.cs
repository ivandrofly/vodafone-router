using System.Net;
using System.Text;

namespace VodafoneRouter.Internal;

/// <summary>
/// Owns the HTTP transport for a single router session: cookies, base URL,
/// authenticated state, and the per-request token (X_HW_Token) dance.
/// </summary>
internal sealed class RouterSession : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly CookieContainer _cookies;

    public Uri BaseUrl { get; }
    public bool IsAuthenticated { get; private set; }
    public string Language { get; }

    public RouterSession(Uri baseUrl, string language, HttpClient? http)
    {
        BaseUrl = baseUrl ?? throw new ArgumentNullException(nameof(baseUrl));
        Language = language;

        if (http is null)
        {
            _cookies = new CookieContainer();
            var handler = new HttpClientHandler
            {
                CookieContainer = _cookies,
                UseCookies = true,
                AllowAutoRedirect = true,
            };
            _http = new HttpClient(handler) { BaseAddress = baseUrl, Timeout = TimeSpan.FromSeconds(30) };
            _ownsHttp = true;
        }
        else
        {
            _http = http;
            _http.BaseAddress ??= baseUrl;
            // We can't introspect the user-supplied handler's cookie container,
            // so we keep our own only for direct-set scenarios (the Language cookie).
            _cookies = new CookieContainer();
            _ownsHttp = false;
        }

        // The web UI sets a UA-like header; some endpoints care.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (compatible; VodafoneRouter.NET/1.0)");
    }

    public async Task LoginAsync(string username, string password, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(username)) throw new ArgumentException("Username is required.", nameof(username));
        if (password is null) throw new ArgumentNullException(nameof(password));

        // Prime the session by visiting the login page (some firmware variants
        // set initial cookies here).
        using (var primer = await _http.GetAsync("/", ct).ConfigureAwait(false))
        {
            primer.EnsureSuccessStatusCode();
        }

        // Fetch a fresh anti-CSRF token, then post credentials. Password is
        // base64-encoded plaintext (not hashed).
        var token = await GetTokenAsync(ct).ConfigureAwait(false);
        var passwordB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(password));
        var form = new Dictionary<string, string>
        {
            ["UserName"] = username,
            ["PassWord"] = passwordB64,
            ["Language"] = Language,
            ["x.X_HW_Token"] = token,
        };

        using var resp = await PostFormCoreAsync("/login.cgi", form, referer: "/", ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        // The router never sends a 302; instead it returns a tiny HTML page
        // whose JS redirects to either index.asp (success) or login.asp
        // (failure). Sniff the body.
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (body.Contains("index.asp", StringComparison.OrdinalIgnoreCase))
        {
            IsAuthenticated = true;
            return;
        }

        if (body.Contains("login.asp", StringComparison.OrdinalIgnoreCase))
            throw new VodafoneRouterAuthException(
                "Login failed: invalid credentials or router lockout (3 failed attempts triggers a temporary block).");

        // Unknown response — be loud about it instead of pretending success.
        throw new VodafoneRouterAuthException(
            $"Unexpected login response (HTTP {(int)resp.StatusCode}, {body.Length} bytes). First 200 chars: " +
            body.Substring(0, Math.Min(200, body.Length)));
    }

    public async Task LogoutAsync(CancellationToken ct)
    {
        if (!IsAuthenticated) return;
        try
        {
            // The UI POSTs to /logout.cgi without parameters; some firmware also accepts GET.
            using var resp = await _http.PostAsync("/logout.cgi", new StringContent(""), ct).ConfigureAwait(false);
        }
        catch
        {
            /* logout is best-effort */
        }
        finally
        {
            IsAuthenticated = false;
        }
    }

    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        using var resp = await _http.PostAsync("/asp/GetRandCount.asp", new StringContent(""), ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var token = (await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
        if (string.IsNullOrEmpty(token))
            throw new VodafoneRouterException("Empty token from /asp/GetRandCount.asp.");
        return token;
    }

    public async Task<string> GetHtmlAsync(string path, CancellationToken ct)
    {
        EnsureAuthenticated();
        using var resp = await _http.GetAsync(path, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches several script/HTML resources sequentially and concatenates
    /// their bodies. Used to assemble the JS environment for pages that load
    /// data through chained &lt;script src&gt; includes.
    /// </summary>
    public async Task<string> GetConcatenatedAsync(IEnumerable<string> paths, CancellationToken ct)
    {
        EnsureAuthenticated();
        var sb = new StringBuilder();
        foreach (var p in paths)
        {
            using var resp = await _http.GetAsync(p, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) continue;
            sb.AppendLine(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public async Task<string> PostXhrAsync(string path, IDictionary<string, string>? form, CancellationToken ct)
    {
        EnsureAuthenticated();
        using var resp = await PostFormCoreAsync(path, form, referer: "/index.asp", ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Posts to <c>/set.cgi?x=&lt;TR-069 path&gt;&amp;RequestFile=...</c> with a fresh token.
    /// Used for actions that mutate state (reboot, reset, set wifi, ...).
    /// </summary>
    public async Task<string> ExecuteCommandAsync(
        string tr069Path,
        string requestFile,
        IDictionary<string, string>? extra,
        CancellationToken ct)
    {
        EnsureAuthenticated();
        var token = await GetTokenAsync(ct).ConfigureAwait(false);
        var form = new Dictionary<string, string>
        {
            ["x.X_HW_Token"] = token,
        };
        if (extra is not null)
            foreach (var kv in extra)
                form[kv.Key] = kv.Value;

        var url = $"/set.cgi?x={Uri.EscapeDataString(tr069Path)}&RequestFile={Uri.EscapeDataString(requestFile)}";
        using var resp = await PostFormCoreAsync(url, form, referer: "/" + requestFile, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> PostFormCoreAsync(
        string url, IDictionary<string, string>? form, string? referer, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form ?? new Dictionary<string, string>());
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        if (referer is not null)
            req.Headers.TryAddWithoutValidation("Referer", new Uri(BaseUrl, referer).ToString());
        return await _http.SendAsync(req, ct).ConfigureAwait(false);
    }

    private void EnsureAuthenticated()
    {
        if (!IsAuthenticated)
            throw new VodafoneRouterAuthException("Not authenticated. Call LoginAsync first.");
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}