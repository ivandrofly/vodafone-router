using System.Text.RegularExpressions;
using Jint;
using Jint.Native;
using Jint.Native.Array;
using Jint.Native.Object;

namespace VodafoneRouter.Internal;

/// <summary>
/// Evaluates the inline JavaScript data the router emits in pages and XHR
/// responses (e.g. <c>var IPWanList = new Array(new IPWANInfo(...), null);</c>),
/// returning the named variables as plain CLR objects.
/// </summary>
/// <remarks>
/// The router uses a "JsObject" pattern where every record is a positional
/// constructor call (e.g. <c>USERDevice("...", "...", ...)</c>). To handle
/// these robustly we run the script inside a sandboxed Jint engine after
/// stubbing every unknown identifier as a constructor that captures its
/// arguments and (optionally) maps them to named fields.
/// </remarks>
public static class EmbeddedJsParser
{
    /// <summary>
    /// Pulls every <c>&lt;script&gt;...&lt;/script&gt;</c> block out of an HTML
    /// page and concatenates their bodies, ignoring scripts with a <c>src</c>.
    /// </summary>
    public static string ExtractInlineScripts(string html)
    {
        var matches = Regex.Matches(
            html,
            @"<script\b(?<attrs>[^>]*)>(?<body>[\s\S]*?)</script>",
            RegexOptions.IgnoreCase);

        var sb = new System.Text.StringBuilder();
        foreach (Match m in matches)
        {
            // Skip external scripts.
            if (Regex.IsMatch(m.Groups["attrs"].Value, @"\bsrc\s*=", RegexOptions.IgnoreCase)) continue;
            sb.AppendLine(m.Groups["body"].Value);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// Parses the given JS source and returns the values of the requested
    /// top-level variables. Unknown identifiers used as constructors or
    /// functions are tolerated (they return objects/arrays of their args),
    /// so business-logic JS does not have to evaluate cleanly.
    /// </summary>
    /// <param name="js">JavaScript source.</param>
    /// <param name="variableNames">Names of variables whose values you want.</param>
    /// <param name="constructorFieldMaps">
    /// Optional: for known constructors, the names of the positional
    /// arguments to use as object properties. Other constructors fall back
    /// to <c>arg0</c>, <c>arg1</c>, ... names.
    /// </param>
    public static IReadOnlyDictionary<string, object?> ExtractVariables(
        string js,
        IEnumerable<string> variableNames,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? constructorFieldMaps = null)
    {
        var engine = new Engine(o => o
            .Strict(false)
            .LimitRecursion(64)
            .TimeoutInterval(TimeSpan.FromSeconds(5))
            .CatchClrExceptions());

        InstallStubs(engine, constructorFieldMaps);

        try
        {
            engine.Execute(js);
        }
        catch (Jint.Runtime.JavaScriptException jex)
        {
            // Some inline scripts reference DOM/jquery which we don't supply.
            // We tolerate execution errors as long as we got the variables we want.
            _ = jex;
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var name in variableNames)
        {
            try
            {
                var v = engine.GetValue(name);
                result[name] = ToClr(v);
            }
            catch
            {
                result[name] = null;
            }
        }
        return result;
    }

    private static void InstallStubs(
        Engine engine,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? maps)
    {
        // Make the engine forgiving: unknown identifiers used as functions
        // produce objects that capture their args; unknown identifiers used
        // as values become empty objects. We achieve "unknown identifier"
        // behaviour by populating the global with a Proxy.
        const string proxySetup = @"
            var __mkCtor = function(name, fields) {
                return function() {
                    var o = { __ctor: name };
                    for (var i = 0; i < arguments.length; i++) {
                        var k = (fields && fields[i]) ? fields[i] : ('arg' + i);
                        o[k] = arguments[i];
                    }
                    return o;
                };
            };
            var __register = function(name, fields) {
                this[name] = __mkCtor(name, fields);
            };
            // Common no-op shims to avoid explosion when pages run logic.
            var $ = function(){ return { ajax: function(){ return null; }, ready: function(){} }; };
            $.ajax = function(opts){ if (opts && opts.success) try { opts.success(''); } catch(e){} };
            var jQuery = $;
            var window = this;
            var document = { getElementById: function(){ return null; }, getElementsByTagName: function(){ return []; }, scripts: [], cookie: '' };
            var navigator = { userAgent: 'NETCLR', appName: 'Netscape', appVersion: '5.0' };
            var location = { href: '/' };
            var setTimeout = function(){}; var clearTimeout = function(){};
            var setInterval = function(){}; var clearInterval = function(){};
            var alert = function(){}; var console = { log: function(){}, error: function(){}, warn: function(){} };
            // Helpers used by router pages:
            var changespace = function(v){ return v == '' ? '*' : v; };
            var GetcurLanguage = function(){ return 'english'; };
            var GetWanList = function(){ return (typeof WanList !== 'undefined') ? WanList : []; };
            var GetLanguage = function(){ return ''; };
        ";
        engine.Execute(proxySetup);

        // Pre-register known constructors with named fields so that
        // result objects come back with proper property names.
        if (maps is not null)
        {
            foreach (var (name, fields) in maps)
            {
                var arr = "[" + string.Join(",", fields.Select(f => "'" + Escape(f) + "'")) + "]";
                engine.Execute($"__register('{Escape(name)}', {arr});");
            }
        }

        // Install a Proxy on the global object so any *other* unknown
        // identifier returns a generic capturing function.
        engine.Execute(@"
            (function(){
                var orig = this;
                var handler = {
                    has: function(t, k) { return true; },
                    get: function(t, k) {
                        if (k in t) return t[k];
                        if (typeof k === 'symbol') return undefined;
                        var fn = function() {
                            var o = { __ctor: k };
                            for (var i = 0; i < arguments.length; i++) o['arg' + i] = arguments[i];
                            return o;
                        };
                        fn.prototype = {};
                        return fn;
                    }
                };
                // Jint doesn't fully proxy globalThis lookups for free identifiers
                // in non-strict mode; this Proxy is defensive but not load-bearing
                // for variable retrieval (we read via engine.GetValue afterwards).
                try { this.__proxy = new Proxy(this, handler); } catch (e) {}
            })();
        ");
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

    private static object? ToClr(JsValue value)
    {
        if (value is null || value.IsUndefined() || value.IsNull()) return null;
        if (value.IsBoolean()) return value.AsBoolean();
        if (value.IsNumber())
        {
            var d = value.AsNumber();
            if (Math.Truncate(d) == d && !double.IsInfinity(d)
                && d >= long.MinValue && d <= long.MaxValue)
                return (long)d;
            return d;
        }
        if (value.IsString()) return value.AsString();
        if (value is JsArray arr)
        {
            var len = (int)arr.Length;
            var list = new List<object?>(len);
            for (uint i = 0; i < len; i++)
                list.Add(ToClr(arr[i]));
            return list;
        }
        if (value is ObjectInstance obj)
        {
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var key in obj.GetOwnPropertyKeys())
            {
                var k = key.ToString();
                if (k is null) continue;
                var prop = obj.GetOwnProperty(key);
                if (prop is null) continue;
                dict[k] = ToClr(prop.Value);
            }
            return dict;
        }
        return value.ToString();
    }

    // Convenience accessors for the dictionary-based shape we get back from ToClr.
    public static string? Str(object? o)
        => o switch { string s => s, null => null, _ => o.ToString() };

    public static long Long(object? o, long fallback = 0)
        => o switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            string s when long.TryParse(s, out var v) => v,
            _ => fallback
        };

    public static bool Bool(object? o)
        => o switch
        {
            bool b => b,
            long l => l != 0,
            int i => i != 0,
            string s => s == "1" || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase),
            _ => false
        };

    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> AsRecords(object? o)
    {
        if (o is not List<object?> list) return Array.Empty<IReadOnlyDictionary<string, object?>>();
        var result = new List<IReadOnlyDictionary<string, object?>>(list.Count);
        foreach (var item in list)
        {
            if (item is Dictionary<string, object?> d) result.Add(d);
        }
        return result;
    }
}
