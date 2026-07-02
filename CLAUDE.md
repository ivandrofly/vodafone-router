# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

A .NET 8 client (`VodafoneRouter.Client`) for the **Vodafone Smart Router**
admin web UI at `http://192.168.1.1/`. The hardware is a Huawei OEM
(`HG8247X6-8N`) running firmware family `V5R020C10S230`, customised for
Vodafone Portugal (the `*ptvdf*` URL fragments). The repository ships:

- `src/VodafoneRouter.Client/` — the library
- `samples/VodafoneRouter.Sample/` — a console demo that logs in and prints
  overview, WAN, Wi-Fi, and connected devices

## Build & run

```powershell
dotnet build VodafoneRouter.slnx -c Debug
$env:VODAFONE_ROUTER_PASS = '<password>'
dotnet run --project samples/VodafoneRouter.Sample
```

The sample reads `VODAFONE_ROUTER_URL`, `VODAFONE_ROUTER_USER`, and
`VODAFONE_ROUTER_PASS` from the environment. There are no tests yet.

## Non-obvious things to know

### 1. Login is a body-sniff, not a status code

`POST /login.cgi` always returns HTTP 200 with a tiny "Waiting…" HTML page
whose JS does `top.location.replace('index.asp')` on success or
`'login.asp'` on failure. Detection is by substring match on the response
body — see `RouterSession.LoginAsync`. **Do not** add a redirect-following
or status-code check; it will silently false-positive (the request URL stays
at `/login.cgi`).

The `Cookie=body:Language:...:id=-1` cookie that the JS UI tries to set is
*not* required — browsers reject it (cookie name "Cookie" is reserved) and
the router is fine without it. Don't try to "fix" the missing cookie code.

### 2. Anti-CSRF tokens — fresh per write

Every state-changing request (`/login.cgi`, `/set.cgi?x=...`) needs a fresh
48-hex token from `POST /asp/GetRandCount.asp`, sent as the
`x.X_HW_Token` form field. Reads do **not** need the token. Never reuse a
token across calls. `RouterSession.ExecuteCommandAsync` handles this
automatically.

### 3. Data lives in JS include scripts, not page HTML

The pattern is:
- `*.asp` admin pages contain mostly DOM scaffolding plus inline JS that
  *reads* data variables.
- The data variables themselves are populated by separate JS-served URLs
  loaded via `<script src="/html/bbsp/common/wan_list.asp">`-style tags.
- Each variable is a `new Array(new ConstructorName(arg, arg, ...), ...,
  null)` — Huawei's "JsObject" pattern, with a trailing `null` terminator.

So to read e.g. `WanList`, you must fetch `wan_list_info.asp` **and**
`wan_list.asp` and pass them concatenated to the parser. The page HTML
alone is empty. `RouterSession.GetConcatenatedAsync` exists for this.

The right include set per feature:

| Want | Fetch |
|------|-------|
| `WanList`, `WlanInfo`, `IPWanList` | `wan_list_info.asp` + `wan_list.asp` |
| `IPv6AddressList`, `IPv6PrefixList` | `wanipv6state.asp` |
| `UserDevinfo` (LAN/Wi-Fi devices) | `getLanUserDevInfo_ptvdf.asp` (single file) |
| `deviceInfos` (firmware/uptime) | inline scripts of `/index.asp` |

### 4. Field names come from the page's real constructor, not your map

`EmbeddedJsParser.ExtractVariables` accepts a `constructorFieldMaps`
parameter, but **the page's own `function Foo(a, b, c) { this.x = a; ... }`
will override your stub** when the script runs. Examples of the real names
that bit us during development:

- `stDeviceInfo` → `domain`, `SoftwareVersion`, `UpTimeStr` (not `Firmware`/`Uptime`)
- `USERDevice.DevStatus` is `"Online"` / `"Offline"` (not `"Active"`)
- `WanList` rows have `IPv4IPAddress`, `IPv4PrimaryDNS`, `BytesSent` etc. directly

Use field maps only for constructors **not** defined in the source you
loaded. When parsing returns an object with named fields that don't match
your map, that means the constructor *was* in the source — read the page's
JS to see what it actually assigns.

### 5. Jint parser is intentionally lenient

`EmbeddedJsParser` runs the inline JS in a sandboxed Jint engine with stubs
for `$`, `jQuery`, `document`, `setInterval`, etc. Unknown identifiers used
as constructors return positional-arg-capturing objects. Script execution
errors are **swallowed** as long as the named variables you want got
assigned. Don't try to make the JS "execute cleanly" — many pages depend
on DOM/jQuery state we don't simulate.

The 448-vs-16 number for connected devices is correct: `UserDevinfo` is the
*lifetime* device list; `IsActive` filters to currently online (matches the
UI's "16 active" badge).

## Adding a new wrapped operation

For a **read** operation:

1. Open the relevant `.asp` page in a browser, view source, and find the
   `<script src="...">` includes that load data.
2. Fetch those scripts, look for the variable name you want and what
   constructor populates it.
3. Add a method on `VodafoneRouterClient` that calls
   `_session.GetConcatenatedAsync(includes)` (or `GetHtmlAsync` for a
   single page), then `EmbeddedJsParser.ExtractVariables(...)` and
   `AsRecords(...)` to get a `List<IReadOnlyDictionary<string, object?>>`.
4. Map fields into a `record` in `Models/`. Use `EmbeddedJsParser.Str/Long/Bool`
   helpers for type coercion.

For a **write** operation:

1. Open the corresponding admin page in a browser with DevTools open and
   click the action.
2. Find the `POST /set.cgi?x=<TR-069 path>&RequestFile=...` request and
   capture the form fields.
3. Add a method calling `_session.ExecuteCommandAsync(tr069Path,
   requestFile, extraParams)` — token plumbing is automatic.

For anything not yet wrapped, callers can use the public escape hatches
`GetRawHtmlAsync` and `ExecuteCommandAsync` directly.

## Investigating the live router

Playwright was used during initial reverse-engineering to enumerate the API
surface (login form contents, network XHR list, embedded data variable
names, action endpoints). When extending the client, the same playbook
works: navigate to a page, dump `Object.keys(window)` filtered for
`/Info|Stat|List|Cfg/`, and inspect the values to learn variable names and
shapes before wiring them in C#.

Do **not** hard-code captured data into source — every record (devices,
WANs, leases) reflects this specific router; only schemas and variable
names should be encoded in the client.
