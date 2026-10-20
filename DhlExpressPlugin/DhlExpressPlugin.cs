using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using QRpa.Core;

namespace DhlExpressPlugin;

/// <summary>
/// DHL Express third-party plugin for QRpa (QApp.QRpa).
/// It provides DHL international shipping automation over the MyDHL API (REST/JSON):
///   1. Dhl.CreateShipment  create a shipment and get the waybill number
///   2. Dhl.Label           fetch the shipping label document (ZIP or JSON)
///   3. Dhl.QueryOrder      query the shipment status by tracking number
///
/// Authentication: OAuth2 client_credentials against
/// POST {baseUrl}/auth/oauth/v2/token (form body + Basic auth). The access token
/// is cached per baseUrl+clientId in a static field so it is fetched only once;
/// a 401 response clears the cache and retries once with a fresh token.
///
/// Credentials: store the client id/secret in the QRpa CredentialStore and
/// reference them in the flow as {secret:name} so no plaintext secrets leak
/// into .flow.json.
///
/// Testing: the baseUrl argument of every activity can point to a local fake
/// server (see the dhlverify console project pattern), so a workflow can run
/// end-to-end without a live network. Fake client ids/secrets are fine.
/// </summary>
public sealed class DhlActivityPlugin : IActivityPlugin
{
    /// <summary>
    /// Production MyDHL endpoint used when baseUrl is empty.
    /// </summary>
    public const string DefaultBaseUrl = "https://api-eu.dhl.com";

    /// <summary>
    /// Default label document path template; {trackingNo} is replaced at run time.
    /// </summary>
    public const string DefaultLabelPath = "/shipments/{trackingNo}/documents";

    private readonly DhlClient _client;

    /// <summary>
    /// Parameterless constructor used by PluginHost (Activator.CreateInstance needs it).
    /// </summary>
    public DhlActivityPlugin()
        : this(new DhlClient())
    {
    }

    /// <summary>
    /// Test constructor: injects a custom <see cref="DhlClient"/> (fake HttpClient handler) for unit tests.
    /// </summary>
    internal DhlActivityPlugin(DhlClient client) => _client = client;

    /// <summary>
    /// Globally unique plugin identifier (naming convention: vendor.domain).
    /// </summary>
    public string Id => "dhl.express";

    /// <summary>
    /// Short plugin description (for display).
    /// </summary>
    public string Description => "DHL Express activities: create international shipment, print the label, query order status (MyDHL API).";

    /// <summary>
    /// Registers all activities of this plugin (called once at startup by PluginHost; never re-register during a run). The kind in the workflow JSON is the Name directly (e.g. "Dhl.CreateShipment"); Category uses the built-in "logistics", shown as "Logistics Center" in the palette.
    /// </summary>
    public void Register()
    {
        ActivityRegistry.RegisterPlugin(new PluginDescriptor
        {
            Name = "Dhl.CreateShipment",
            Title = "DHL Create Shipment (plugin)",
            Category = "logistics",
            Hint = "Create a DHL shipment. Required: clientId/clientSecret (use {secret:name}), fromContact/fromAddress, toContact/toAddress. "
                 + "Optional: accountNumber, shipmentDate, productCode, fromPhone/fromCity/fromPostal/fromCountry, "
                 + "toPhone/toCity/toPostal/toCountry, weight(kg), length/width/height(cm), description. "
                 + "baseUrl points to MyDHL or a local fake server. resultVar (default \"shipment\") = response JSON, "
                 + "{resultVar}.waybill = tracking number.",
            Defaults = new Dictionary<string, string>
            {
                ["baseUrl"] = DefaultBaseUrl,
                ["clientId"] = "{secret:dhlClientId}",
                ["clientSecret"] = "{secret:dhlClientSecret}",
                ["productCode"] = "D",
                ["weight"] = "1",
                ["resultVar"] = "shipment",
            },
            Handler = CreateShipment,
        });

        ActivityRegistry.RegisterPlugin(new PluginDescriptor
        {
            Name = "Dhl.Label",
            Title = "DHL Print Label (plugin)",
            Category = "logistics",
            Hint = "Fetch the DHL shipping label. Required: waybillNo (reference {shipment.waybill}), clientId/clientSecret (use {secret:name}), output (save path). "
                 + "Optional: labelPath template (default \"/shipments/{trackingNo}/documents\"), baseUrl. "
                 + "A ZIP document is saved with .zip, a JSON payload with .json. resultVar = saved file path.",
            Defaults = new Dictionary<string, string>
            {
                ["baseUrl"] = DefaultBaseUrl,
                ["clientId"] = "{secret:dhlClientId}",
                ["clientSecret"] = "{secret:dhlClientSecret}",
                ["labelPath"] = DefaultLabelPath,
                ["output"] = "{env.TEMP}\\dhl-label",
                ["resultVar"] = "dhlLabelPath",
            },
            Handler = PrintLabel,
        });

        ActivityRegistry.RegisterPlugin(new PluginDescriptor
        {
            Name = "Dhl.QueryOrder",
            Title = "DHL Query Order Status (plugin)",
            Category = "logistics",
            Hint = "Query the DHL shipment status. Required: waybillNo, clientId/clientSecret (use {secret:name}). "
                 + "Optional: baseUrl. resultVar (default \"track\") = response JSON, {resultVar}.status = current status "
                 + "(status field first, then the last event's status).",
            Defaults = new Dictionary<string, string>
            {
                ["baseUrl"] = DefaultBaseUrl,
                ["clientId"] = "{secret:dhlClientId}",
                ["clientSecret"] = "{secret:dhlClientSecret}",
                ["resultVar"] = "track",
            },
            Handler = QueryOrder,
        });
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Activity 1: DHL create shipment (POST /shipments)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// DHL create shipment: builds the shipment payload from the flow arguments, POSTs it, writes the full response JSON to resultVar and the extracted waybill/tracking number to {resultVar}.waybill.
    /// </summary>
    private async Task<IReadOnlyList<ActivityNode>?> CreateShipment(ActivityNode node, WorkflowContext ctx, CancellationToken ct)
    {
        var baseUrl = ctx.GetStr(node, "baseUrl", DefaultBaseUrl);
        var clientId = Require(node, ctx, "clientId", "clientId");
        var clientSecret = Require(node, ctx, "clientSecret", "clientSecret");

        var payload = BuildShipmentBody(node, ctx);
        var url = Combine(baseUrl, "/shipments");
        var result = await SendAsync(node, ctx, url, HttpMethod.Post, baseUrl, clientId, clientSecret, payload.ToJsonString(), ct);

        JsonObject? parsed;
        try { parsed = JsonNode.Parse(result.Text) as JsonObject; }
        catch (JsonException) { parsed = null; }
        if (parsed is null)
            throw new WorkflowException(node.Id, $"DHL create shipment: response is not a JSON object. {Truncate(result.Text)}");

        var resultVar = ctx.GetStr(node, "resultVar", "shipment");
        ctx.Set(resultVar, result.Text);

        var waybill = FindWaybill(parsed);
        if (string.IsNullOrEmpty(waybill))
            throw new WorkflowException(node.Id, $"DHL create shipment: no waybill/tracking number found in the response. {Truncate(result.Text)}");

        ctx.Set(resultVar + ".waybill", waybill);
        ctx.LogLine?.Invoke($"[DHL] shipment created, waybill {waybill}");
        return null;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Activity 2: DHL print label (GET /shipments/{trackingNo}/documents)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// DHL print label: fetches the label document for a waybill and saves it. A ZIP response (content-type application/zip) is written with a .zip extension, any other payload is written as .json. The saved path goes to resultVar.
    /// </summary>
    private async Task<IReadOnlyList<ActivityNode>?> PrintLabel(ActivityNode node, WorkflowContext ctx, CancellationToken ct)
    {
        var baseUrl = ctx.GetStr(node, "baseUrl", DefaultBaseUrl);
        var clientId = Require(node, ctx, "clientId", "clientId");
        var clientSecret = Require(node, ctx, "clientSecret", "clientSecret");
        var waybill = Require(node, ctx, "waybillNo", "waybillNo");
        var output = ctx.GetStr(node, "output");
        if (output.Length == 0)
            throw new WorkflowException(node.Id, "DHL print label: output save path is empty.");

        var pathTemplate = ctx.GetStr(node, "labelPath", DefaultLabelPath);
        var path = pathTemplate
            .Replace("{trackingNo}", waybill, StringComparison.OrdinalIgnoreCase)
            .Replace("{waybillNo}", waybill, StringComparison.OrdinalIgnoreCase);

        var url = Combine(baseUrl, path);
        var result = await SendAsync(node, ctx, url, HttpMethod.Get, baseUrl, clientId, clientSecret, null, ct);

        // ZIP is detected by content-type or by the "PK" magic bytes; everything else is saved as JSON.
        var isZip = result.ContentType?.Contains("zip", StringComparison.OrdinalIgnoreCase) == true
                    || (result.Bytes.Length > 2 && result.Bytes[0] == (byte)'P' && result.Bytes[1] == (byte)'K');
        var final = EnsureExtension(output, isZip ? ".zip" : ".json");
        SaveBytes(final, result.Bytes);

        ctx.Set(ctx.GetStr(node, "resultVar"), final);
        ctx.LogLine?.Invoke($"[DHL] label saved: {final} ({result.Bytes.Length} bytes)");
        return null;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Activity 3: DHL query order status (GET /shipments/{trackingNo})
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// DHL query order: fetches the shipment/tracking status and writes the response JSON to resultVar plus a {resultVar}.status summary (the status field wins; when absent, the last event's status is used).
    /// </summary>
    private async Task<IReadOnlyList<ActivityNode>?> QueryOrder(ActivityNode node, WorkflowContext ctx, CancellationToken ct)
    {
        var baseUrl = ctx.GetStr(node, "baseUrl", DefaultBaseUrl);
        var clientId = Require(node, ctx, "clientId", "clientId");
        var clientSecret = Require(node, ctx, "clientSecret", "clientSecret");
        var waybill = Require(node, ctx, "waybillNo", "waybillNo");

        var url = Combine(baseUrl, $"/shipments/{Uri.EscapeDataString(waybill)}")
                + $"?trackingNumber={Uri.EscapeDataString(waybill)}";
        var result = await SendAsync(node, ctx, url, HttpMethod.Get, baseUrl, clientId, clientSecret, null, ct);

        var resultVar = ctx.GetStr(node, "resultVar", "track");
        ctx.Set(resultVar, result.Text);
        ctx.Set(resultVar + ".json", result.Text);

        try
        {
            if (JsonNode.Parse(result.Text) is JsonObject parsed)
            {
                var status = FindStatus(parsed);
                if (!string.IsNullOrEmpty(status))
                    ctx.Set(resultVar + ".status", status);
            }
        }
        catch (JsonException)
        {
            throw new WorkflowException(node.Id, $"DHL query order: response is not valid JSON. {Truncate(result.Text)}");
        }

        ctx.LogLine?.Invoke($"[DHL] order status query done for {waybill}");
        return null;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Shared execution
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Sends an authenticated request: reads the cached OAuth token (or fetches one), sends the call, and on a 401 clears the token cache and retries once with a fresh token. HTTP >= 400 throws a <see cref="WorkflowException"/> carrying the status code and a response summary.
    /// </summary>
    private async Task<DhlHttpResult> SendAsync(
        ActivityNode node, WorkflowContext ctx, string url, HttpMethod method,
        string baseUrl, string clientId, string clientSecret, string? jsonPayload, CancellationToken ct)
    {
        try
        {
            var cacheKey = DhlClient.TokenKey(baseUrl, clientId);
            var token = await _client.GetTokenAsync(baseUrl, clientId, clientSecret, ct);
            var result = await _client.SendOnceAsync(method, url, token, jsonPayload, ct);

            if (result.StatusCode == 401)
            {
                // Token expired: drop it from the cache, fetch a fresh one, retry once.
                _client.ClearToken(cacheKey);
                ctx.LogLine?.Invoke("[DHL] access token rejected (401); re-fetching and retrying once.");
                token = await _client.GetTokenAsync(baseUrl, clientId, clientSecret, ct);
                result = await _client.SendOnceAsync(method, url, token, jsonPayload, ct);
            }

            if (result.StatusCode >= 400)
                throw new WorkflowException(node.Id, $"DHL API error: HTTP {result.StatusCode}; {Truncate(result.Text)}");
            return result;
        }
        catch (DhlApiException ex)
        {
            // Network/timeout/token-level errors are wrapped so logs identify the node.
            throw new WorkflowException(node.Id, ex.Message);
        }
    }

    /// <summary>
    /// Builds the MyDHL create-shipment JSON payload from the flow arguments (blank fields are skipped).
    /// </summary>
    private static JsonObject BuildShipmentBody(ActivityNode node, WorkflowContext ctx)
    {
        var body = new JsonObject();
        var date = ctx.GetStr(node, "shipmentDate");
        if (date.Length > 0) body["plannedShippingDate"] = date;
        var productCode = ctx.GetStr(node, "productCode");
        if (productCode.Length > 0) body["productCode"] = productCode;

        var account = ctx.GetStr(node, "accountNumber");
        if (account.Length > 0)
            body["accounts"] = new JsonArray(new JsonObject { ["typeCode"] = "shipper", ["number"] = account });

        var details = new JsonObject
        {
            ["isDutiable"] = "N",
            ["numberOfPieces"] = 1,
        };
        var description = ctx.GetStr(node, "description");
        if (description.Length > 0) details["description"] = description;

        var pkg = new JsonObject { ["weight"] = ctx.GetStr(node, "weight", "1") };
        var dims = new JsonObject();
        AddStr(dims, "length", ctx.GetStr(node, "length"));
        AddStr(dims, "width", ctx.GetStr(node, "width"));
        AddStr(dims, "height", ctx.GetStr(node, "height"));
        if (dims.Count > 0) pkg["dimensions"] = dims;
        details["packages"] = new JsonArray(pkg);
        body["shipmentDetails"] = details;

        body["customerDetails"] = new JsonObject
        {
            ["shipperDetails"] = ShippingParty(node, ctx, "from"),
            ["receiverDetails"] = ShippingParty(node, ctx, "to"),
        };
        return body;
    }

    /// <summary>
    /// Builds one shipping party (shipper/receiver) using the "from"/"to" argument prefixes.
    /// </summary>
    private static JsonObject ShippingParty(ActivityNode node, WorkflowContext ctx, string prefix)
    {
        var contact = new JsonObject { ["fullName"] = Require(node, ctx, prefix + "Contact", prefix + "Contact") };
        AddStr(contact, "phone", ctx.GetStr(node, prefix + "Phone"));

        var address = new JsonObject { ["addressLine1"] = Require(node, ctx, prefix + "Address", prefix + "Address") };
        AddStr(address, "cityName", ctx.GetStr(node, prefix + "City"));
        AddStr(address, "postalCode", ctx.GetStr(node, prefix + "Postal"));
        AddStr(address, "countryCode", ctx.GetStr(node, prefix + "Country"));

        return new JsonObject
        {
            ["postalAddress"] = address,
            ["contactInformation"] = contact,
        };
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Response helpers
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Extracts the waybill/tracking number from a create-shipment response. Preference: shipments[0].pieces[0].trackingNumber, then shipments[0] shipment-level numbers, then a deep search over the whole response.
    /// </summary>
    private static string? FindWaybill(JsonObject root)
    {
        if (Arr(root, "shipments") is { Count: > 0 } shipments && shipments[0] is JsonObject first)
        {
            if (Arr(first, "pieces") is { Count: > 0 } pieces && pieces[0] is JsonObject piece)
            {
                foreach (var key in new[] { "trackingNumber", "trackingNo" })
                {
                    var v = Pick(piece, key);
                    if (!string.IsNullOrEmpty(v)) return v;
                }
            }
            foreach (var key in new[] { "shipmentTrackingNumber", "waybillNumber", "trackingNumber" })
            {
                var v = Pick(first, key);
                if (!string.IsNullOrEmpty(v)) return v;
            }
        }
        foreach (var key in new[] { "trackingNumber", "waybillNumber", "trackingNo", "waybillNo" })
        {
            var v = DeepSearch(root, key);
            if (!string.IsNullOrEmpty(v)) return v;
        }
        return null;
    }

    /// <summary>
    /// Extracts the current status from a tracking response. Preference: shipments[0].status, top-level status, a deep search (skipping "events" subtrees), and finally the last event's status/description.
    /// </summary>
    private static string? FindStatus(JsonObject root)
    {
        if (Arr(root, "shipments") is { Count: > 0 } shipments && shipments[0] is JsonObject first)
        {
            foreach (var key in new[] { "status", "statusText", "statusDescription" })
            {
                var v = Pick(first, key);
                if (!string.IsNullOrEmpty(v)) return v;
            }
        }
        var top = Pick(root, "status");
        if (!string.IsNullOrEmpty(top)) return top;

        var deep = DeepSearchStatus(root);
        if (!string.IsNullOrEmpty(deep)) return deep;

        var events = FindEventsArray(root);
        if (events is { Count: > 0 } && events[^1] is JsonObject last)
        {
            foreach (var key in new[] { "status", "statusDescription", "description" })
            {
                var v = Pick(last, key);
                if (!string.IsNullOrEmpty(v)) return v;
            }
        }
        return null;
    }

    /// <summary>
    /// Finds the first JSON array named "events" (top level, then inside the first shipment).
    /// </summary>
    private static JsonArray? FindEventsArray(JsonObject root)
    {
        if (Arr(root, "events") is { } top) return top;
        if (Arr(root, "shipments") is { Count: > 0 } shipments && shipments[0] is JsonObject s && Arr(s, "events") is { } se)
            return se;
        return null;
    }

    /// <summary>
    /// Depth-first search for a string field by name; "events" subtrees are skipped so an event status never beats the shipment status.
    /// </summary>
    private static string? DeepSearchStatus(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var kv in obj)
            {
                if (string.Equals(kv.Key, "events", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(kv.Key, "status", StringComparison.OrdinalIgnoreCase) && kv.Value is JsonValue val)
                    return val.ToString();
                var hit = kv.Value is not null ? DeepSearchStatus(kv.Value) : null;
                if (!string.IsNullOrEmpty(hit)) return hit;
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                var hit = item is not null ? DeepSearchStatus(item) : null;
                if (!string.IsNullOrEmpty(hit)) return hit;
            }
        }
        return null;
    }

    /// <summary>
    /// Depth-first search for the first string value of a field name anywhere in the JSON tree.
    /// </summary>
    private static string? DeepSearch(JsonNode node, string key)
    {
        if (node is JsonObject obj)
        {
            foreach (var kv in obj)
            {
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase) && kv.Value is JsonValue val)
                    return val.ToString();
                var hit = kv.Value is not null ? DeepSearch(kv.Value, key) : null;
                if (!string.IsNullOrEmpty(hit)) return hit;
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                var hit = item is not null ? DeepSearch(item, key) : null;
                if (!string.IsNullOrEmpty(hit)) return hit;
            }
        }
        return null;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Small helpers
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Required-argument check: throws a node-readable error when the value is blank.
    /// </summary>
    private static string Require(ActivityNode node, WorkflowContext ctx, string arg, string label)
    {
        var v = ctx.GetStr(node, arg).Trim();
        if (v.Length == 0)
            throw new WorkflowException(node.Id, $"DHL activity: {label} (argument {arg}) is required.");
        return v;
    }

    /// <summary>
    /// Adds to the JSON object only when the value is non-empty, so blank fields never enter the request.
    /// </summary>
    private static void AddStr(JsonObject obj, string key, string value)
    {
        if (!string.IsNullOrEmpty(value)) obj[key] = value;
    }

    /// <summary>
    /// Reads a named JSON array from an object (null when absent or not an array).
    /// </summary>
    private static JsonArray? Arr(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out var el) ? el as JsonArray : null;

    /// <summary>
    /// Reads a named string field from an object (empty when absent).
    /// </summary>
    private static string Pick(JsonObject obj, string key)
    {
        if (obj.TryGetPropertyValue(key, out var el) && el is JsonValue val)
            return val.ToString();
        return "";
    }

    /// <summary>
    /// Joins a base URL and a path (tolerates missing/extra slashes).
    /// </summary>
    private static string Combine(string baseUrl, string path)
    {
        var b = baseUrl.TrimEnd('/');
        var p = path.StartsWith('/') ? path : "/" + path;
        return b + p;
    }

    /// <summary>
    /// Ensures the file name ends with the target extension: an existing ".zip"/".json" name is kept, otherwise the extension is appended.
    /// </summary>
    private static string EnsureExtension(string path, string ext)
    {
        var lower = path.ToLowerInvariant();
        if (lower.EndsWith(".zip") || lower.EndsWith(".json")) return path;
        return path.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? path : path + ext;
    }

    /// <summary>
    /// Ensures the parent directory exists, then writes the bytes.
    /// </summary>
    private static void SaveBytes(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>
    /// Truncates overly long response bodies so error messages do not flood the logs.
    /// </summary>
    private static string Truncate(string s) =>
        s.Length <= 500 ? s : s[..500] + "... (truncated)";
}

/// <summary>
/// HTTP-level client for the MyDHL API: OAuth2 token handling with a static per-endpoint cache, request sending, and status/body capture.
/// </summary>
public sealed class DhlClient
{
    /// <summary>
    /// Cached access tokens, keyed by baseUrl + "|" + clientId, so a long workflow fetches the token only once per credential pair.
    /// </summary>
    private static readonly ConcurrentDictionary<string, string> TokenCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly HttpClient _http;

    /// <summary>
    /// Creates the client; accepts an injected HttpClient (fake handler for tests) and a timeout.
    /// </summary>
    public DhlClient(HttpClient? http = null, TimeSpan? timeout = null)
        => _http = http ?? new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(60) };

    /// <summary>
    /// Builds the static cache key for a baseUrl and clientId pair.
    /// </summary>
    public static string TokenKey(string baseUrl, string clientId) =>
        (baseUrl.TrimEnd('/') + "|" + clientId).ToLowerInvariant();

    /// <summary>
    /// Clears one cached token (used when a 401 forces a re-fetch).
    /// </summary>
    public void ClearToken(string key) => TokenCache.TryRemove(key, out _);

    /// <summary>
    /// Returns the access token for the endpoint, fetching it through the OAuth endpoint only when the cache misses.
    /// </summary>
    public async Task<string> GetTokenAsync(string baseUrl, string clientId, string clientSecret, CancellationToken ct)
    {
        var key = TokenKey(baseUrl, clientId);
        if (TokenCache.TryGetValue(key, out var cached) && !string.IsNullOrEmpty(cached))
            return cached;

        var token = await FetchTokenAsync(baseUrl, clientId, clientSecret, ct);
        TokenCache[key] = token;
        return token;
    }

    /// <summary>
    /// POSTs {baseUrl}/auth/oauth/v2/token with a form body (grant_type=client_credentials) and Basic auth, and returns the access token.
    /// </summary>
    private async Task<string> FetchTokenAsync(string baseUrl, string clientId, string clientSecret, CancellationToken ct)
    {
        var url = Combine(baseUrl, "/auth/oauth/v2/token");
        var body = "grant_type=client_credentials"
                 + "&client_id=" + Uri.EscapeDataString(clientId)
                 + "&client_secret=" + Uri.EscapeDataString(clientSecret);
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(clientId + ":" + clientSecret));

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

        var result = await SendMessageAsync(req, ct);
        if (result.StatusCode >= 400)
            throw new DhlApiException($"DHL token request failed: HTTP {result.StatusCode}; {Truncate(result.Text)}");

        JsonObject? parsed;
        try { parsed = JsonNode.Parse(result.Text) as JsonObject; }
        catch (JsonException) { parsed = null; }
        var token = parsed is not null ? PickStr(parsed, "access_token") : null;
        if (string.IsNullOrEmpty(token))
            throw new DhlApiException($"DHL token response has no access_token. {Truncate(result.Text)}");
        return token;
    }

    /// <summary>
    /// Sends one authenticated request and captures status, text, bytes, and content type. Network failures become <see cref="DhlApiException"/>.
    /// </summary>
    public async Task<DhlHttpResult> SendOnceAsync(HttpMethod method, string url, string token, string? jsonPayload, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (jsonPayload is not null)
            req.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
        return await SendMessageAsync(req, ct);
    }

    /// <summary>
    /// Dispatches a request and reads the full response (status, UTF-8 text, raw bytes, media type).
    /// </summary>
    private async Task<DhlHttpResult> SendMessageAsync(HttpRequestMessage req, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DhlApiException($"DHL request timed out: {req.RequestUri}");
        }
        catch (HttpRequestException ex)
        {
            throw new DhlApiException($"DHL network error for {req.RequestUri}: {ex.Message}");
        }

        using (resp)
        {
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var mediaType = resp.Content.Headers.ContentType?.MediaType;
            return new DhlHttpResult((int)resp.StatusCode, Encoding.UTF8.GetString(bytes), bytes, mediaType);
        }
    }

    private static string PickStr(JsonObject obj, string key)
    {
        if (obj.TryGetPropertyValue(key, out var el) && el is JsonValue val)
            return val.ToString();
        return "";
    }

    private static string Combine(string baseUrl, string path)
    {
        var b = baseUrl.TrimEnd('/');
        var p = path.StartsWith('/') ? path : "/" + path;
        return b + p;
    }

    private static string Truncate(string s) =>
        s.Length <= 500 ? s : s[..500] + "... (truncated)";
}

/// <summary>
/// Captured HTTP response: status code, decoded UTF-8 text, raw bytes, and the media type.
/// </summary>
public sealed record DhlHttpResult(int StatusCode, string Text, byte[] Bytes, string? ContentType);

/// <summary>
/// HTTP-level error thrown by <see cref="DhlClient"/> on network failures or non-success HTTP statuses.
/// </summary>
public sealed class DhlApiException : Exception
{
    /// <summary>
    /// Creates the exception with a message.
    /// </summary>
    public DhlApiException(string message) : base(message) { }
}