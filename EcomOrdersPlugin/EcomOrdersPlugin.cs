using System.Globalization;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using QRpa.Core;

namespace EcomOrdersPlugin;

/// <summary>
/// Ecom Orders plugin for QRpa: an e-commerce order source backed by the Shopify
/// Admin REST API. It registers two activities:
///   Eco.PullOrders - list orders (GET /admin/api/2024-01/orders.json)
///   Eco.OrderById  - get one order (GET /admin/api/2024-01/orders/{id}.json)
///
/// Auth: the token travels in the X-Shopify-Access-Token header, and an
/// Authorization: Bearer header is also sent. The REST API is not signed, so no
/// signature/digest computation is needed.
///
/// Testing: every activity accepts a baseUrl argument. Point it at a local fake
/// server (or a sandbox) to run end-to-end without a live Shopify store. The
/// plugin also allows an injected HttpClient with a fake message handler.
/// </summary>
public sealed class EcomActivityPlugin : IActivityPlugin
{
    /// <summary>Unique plugin identifier (vendor.domain convention).</summary>
    public string Id => "ecom.orders";

    /// <summary>Short plugin description (for display).</summary>
    public string Description => "E-commerce order source: pull and query Shopify orders via the Admin REST API.";

    private const string ApiVersion = "2024-01";

    private readonly ShopifyApiClient _client;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        // Keep non-ASCII text readable (do not escape it to \uXXXX sequences).
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Parameterless constructor used by PluginHost (Activator.CreateInstance needs it).</summary>
    public EcomActivityPlugin()
        : this(new ShopifyApiClient())
    {
    }

    /// <summary>Test constructor: injects a client backed by a fake message handler or a fake server.</summary>
    internal EcomActivityPlugin(ShopifyApiClient client) => _client = client;

    /// <summary>
    /// Registers the two plugin activities. The Name is the kind used in the
    /// workflow JSON (e.g. "Eco.PullOrders"); the Category "logistics" shows them
    /// in the Logistics Center palette group like the shipping plugins.
    /// </summary>
    public void Register()
    {
        ActivityRegistry.RegisterPlugin(new PluginDescriptor
        {
            Name = "Eco.PullOrders",
            Title = "Pull Shopify orders",
            Category = "logistics",
            Hint = "Pull orders (GET /admin/api/2024-01/orders.json) | shop=shop domain (required) | "
                 + "accessToken=API token (required, {secret:name} recommended) | status=any/open/closed (optional) | "
                 + "limit=page size (default 50) | extraParams=JSON object merged into the query string (optional) | "
                 + "baseUrl=API base URL for tests (optional) | resultVar=orders JSON array variable "
                 + "(default \"orders\", plus {resultVar}.count)",
            Defaults = new Dictionary<string, string>
            {
                ["status"] = "any",
                ["limit"] = "50",
                ["resultVar"] = "orders",
            },
            Handler = PullOrders,
        });

        ActivityRegistry.RegisterPlugin(new PluginDescriptor
        {
            Name = "Eco.OrderById",
            Title = "Find Shopify order by ID",
            Category = "logistics",
            Hint = "Get one order (GET /admin/api/2024-01/orders/{id}.json) | shop=shop domain (required) | "
                 + "accessToken=API token (required, {secret:name} recommended) | orderId=Shopify order ID (required) | "
                 + "baseUrl=API base URL for tests (optional) | resultVar=order JSON variable "
                 + "(default \"order\", plus {resultVar}.phone with the shipping address phone)",
            Defaults = new Dictionary<string, string>
            {
                ["resultVar"] = "order",
            },
            Handler = OrderById,
        });
    }

    // ---------------------------------------------------------------
    // Activity 1: Eco.PullOrders
    // ---------------------------------------------------------------
    /// <summary>
    /// Pulls a page of Shopify orders, writes the JSON array into resultVar and
    /// the order count into {resultVar}.count.
    /// </summary>
    private async Task<IReadOnlyList<ActivityNode>?> PullOrders(ActivityNode node, WorkflowContext ctx, CancellationToken ct)
    {
        const string activity = "Eco.PullOrders";
        var shop = Required(node, ctx, "shop", activity);
        var token = Required(node, ctx, "accessToken", activity);
        var baseUrl = ResolveBase(node, ctx, shop);
        var resultVar = ctx.GetStr(node, "resultVar", "orders");
        if (resultVar.Trim().Length == 0) resultVar = "orders";

        var query = new List<string>();

        var status = ctx.GetStr(node, "status").Trim();
        if (status.Length > 0) query.Add("status=" + Uri.EscapeDataString(status));

        var limit = ctx.GetInt(node, "limit", 50);
        if (limit < 1) limit = 50;
        query.Add("limit=" + limit.ToString(CultureInfo.InvariantCulture));

        // extraParams: a JSON object merged key-by-key into the query string.
        foreach (var (key, value) in ReadExtraParams(node, ctx))
            query.Add(Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(value));

        var url = baseUrl + "/orders.json" + (query.Count > 0 ? "?" + string.Join("&", query) : "");
        var raw = await _client.GetAsync(url, token, node, activity, ct).ConfigureAwait(false);

        var orders = ExtractArray(node, raw, "orders", activity);
        var count = orders.Count;
        ctx.Set(resultVar, orders.ToJsonString(JsonOpts));
        ctx.Set(resultVar + ".count", count);

        // Also register the array as a table so {resultVar.i.field} works like LoadCsv/LoadExcel.
        // Scalar values keep their JSON form (numbers stay numeric text, objects nested as JSON).
        var table = new TableData();
        if (orders.Count > 0 && orders[0] is JsonObject first)
        {
            foreach (var kv in first) table.Columns.Add(kv.Key);
            foreach (var item in orders)
            {
                if (item is not JsonObject o) continue;
                table.Rows.Add(table.Columns.ToDictionary(
                    col => col,
                    col => o[col] is null ? "" : (o[col] is JsonValue v && v.TryGetValue<string>(out var s) ? s : o[col]!.ToJsonString(JsonOpts)),
                    StringComparer.OrdinalIgnoreCase));
            }
        }
        ctx.SetTable(resultVar, table);

        ctx.LogLine?.Invoke($"[Ecom] {activity}: fetched {count} order(s) into '{resultVar}'.");
        return null;
    }

    // ---------------------------------------------------------------
    // Activity 2: Eco.OrderById
    // ---------------------------------------------------------------
    /// <summary>
    /// Gets one Shopify order. The order JSON goes into resultVar; the shipping
    /// address phone (when present) is copied to {resultVar}.phone. HTTP 404 (or
    /// any error) raises a WorkflowException.
    /// </summary>
    private async Task<IReadOnlyList<ActivityNode>?> OrderById(ActivityNode node, WorkflowContext ctx, CancellationToken ct)
    {
        const string activity = "Eco.OrderById";
        var shop = Required(node, ctx, "shop", activity);
        var token = Required(node, ctx, "accessToken", activity);
        var orderId = Required(node, ctx, "orderId", activity);
        var baseUrl = ResolveBase(node, ctx, shop);
        var resultVar = ctx.GetStr(node, "resultVar", "order");
        if (resultVar.Trim().Length == 0) resultVar = "order";

        var url = baseUrl + "/orders/" + Uri.EscapeDataString(orderId) + ".json";
        var raw = await _client.GetAsync(url, token, node, activity, ct).ConfigureAwait(false);

        var order = ExtractObject(node, raw, "order", activity);
        ctx.Set(resultVar, order.ToJsonString(JsonOpts));

        var phone = ShippingPhone(order);
        if (phone.Length > 0)
        {
            ctx.Set(resultVar + ".phone", phone);
            ctx.LogLine?.Invoke($"[Ecom] {activity}: order {orderId} fetched into '{resultVar}', shipping phone '{phone}'.");
        }
        else
        {
            ctx.LogLine?.Invoke($"[Ecom] {activity}: order {orderId} fetched into '{resultVar}' (no shipping phone).");
        }
        return null;
    }

    // ---------------------------------------------------------------
    // Shared helpers
    // ---------------------------------------------------------------
    /// <summary>
    /// Resolves the API base URL: the baseUrl argument wins (used for local fake
    /// servers / sandboxes); otherwise it is built from the shop domain.
    /// </summary>
    private static string ResolveBase(ActivityNode node, WorkflowContext ctx, string shop)
    {
        var baseUrl = ctx.GetStr(node, "baseUrl").Trim().TrimEnd('/');
        return baseUrl.Length > 0 ? baseUrl : $"https://{shop}/admin/api/{ApiVersion}";
    }

    /// <summary>
    /// Required-argument check: throws a node-readable error when the value is blank.
    /// </summary>
    private static string Required(ActivityNode node, WorkflowContext ctx, string arg, string activity)
    {
        var v = ctx.GetStr(node, arg).Trim();
        if (v.Length == 0)
            throw new WorkflowException(node.Id, $"{activity}: missing required argument '{arg}'.");
        return v;
    }

    /// <summary>
    /// Parses the extraParams JSON object into one key/value pair per entry
    /// (scalar values only; nested values are serialized as JSON).
    /// </summary>
    private static IEnumerable<KeyValuePair<string, string>> ReadExtraParams(ActivityNode node, WorkflowContext ctx)
    {
        var json = ctx.GetStr(node, "extraParams").Trim();
        if (json.Length == 0) yield break;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new WorkflowException(node.Id, $"Eco.PullOrders: extraParams is not valid JSON ({ex.Message}).");
        }
        if (root is not JsonObject obj)
            throw new WorkflowException(node.Id, "Eco.PullOrders: extraParams must be a JSON object.");

        foreach (var kv in obj)
        {
            if (kv.Key.Length == 0 || kv.Value is null) continue;
            yield return new KeyValuePair<string, string>(kv.Key, ScalarText(kv.Value));
        }
    }

    /// <summary>
    /// Renders a scalar JSON value as a plain string (numbers/bools are invariant).
    /// </summary>
    private static string ScalarText(JsonNode value) => value switch
    {
        JsonValue jv when jv.TryGetValue<string>(out var s) => s,
        JsonValue jv when jv.TryGetValue<double>(out var d) => d.ToString(CultureInfo.InvariantCulture),
        JsonValue jv when jv.TryGetValue<bool>(out var b) => b ? "true" : "false",
        _ => value.ToJsonString(),
    };

    /// <summary>
    /// Reads the shipping address phone from the order object (empty when absent).
    /// </summary>
    private static string ShippingPhone(JsonObject order)
    {
        if (order?["shipping_address"] is JsonObject sa && sa["phone"] is JsonValue phone)
            return phone.TryGetValue<string>(out var s) ? s.Trim() : "";
        return "";
    }

    /// <summary>
    /// Extracts a named JSON array from the response body (throws on malformed
    /// JSON or a missing property).
    /// </summary>
    private static JsonArray ExtractArray(ActivityNode node, string raw, string prop, string activity)
    {
        var root = ParseBody(node, raw, activity);
        if (root is JsonObject obj && obj[prop] is JsonArray arr) return arr;
        throw new WorkflowException(node.Id, $"{activity}: response has no \"{prop}\" array. Body: {Truncate(raw)}");
    }

    /// <summary>
    /// Extracts a named JSON object from the response body (throws on malformed
    /// JSON or a missing property).
    /// </summary>
    private static JsonObject ExtractObject(ActivityNode node, string raw, string prop, string activity)
    {
        var root = ParseBody(node, raw, activity);
        if (root is JsonObject obj && obj[prop] is JsonObject json) return json;
        throw new WorkflowException(node.Id, $"{activity}: response has no \"{prop}\" object. Body: {Truncate(raw)}");
    }

    /// <summary>
    /// Parses the response body into a JSON node (malformed JSON becomes a WorkflowException).
    /// </summary>
    private static JsonNode? ParseBody(ActivityNode node, string raw, string activity)
    {
        try
        {
            return JsonNode.Parse(raw);
        }
        catch (JsonException ex)
        {
            throw new WorkflowException(node.Id, $"{activity}: response is not valid JSON ({ex.Message}). Body: {Truncate(raw)}");
        }
    }

    /// <summary>
    /// Truncates long response bodies so error messages never flood the logs.
    /// </summary>
    private static string Truncate(string s) =>
        s.Length <= 300 ? s : s[..300] + "... (truncated)";
}

/// <summary>
/// Minimal Shopify Admin REST client: injectable HttpClient, token headers, and
/// uniform error translation (HTTP &gt;= 400 becomes a WorkflowException carrying
/// the status code and a truncated response summary).
/// </summary>
public sealed class ShopifyApiClient
{
    private readonly HttpClient _http;

    /// <summary>Creates the client; accepts an injected HttpClient (fake handler in tests) and a timeout.</summary>
    public ShopifyApiClient(HttpClient? http = null, TimeSpan? timeout = null)
        => _http = http ?? new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(60) };

    /// <summary>
    /// Sends a GET request to the Shopify Admin REST API. The token is sent both as
    /// X-Shopify-Access-Token (the documented header) and as an Authorization Bearer
    /// header. Errors are thrown as WorkflowException bound to the calling node.
    /// </summary>
    public async Task<string> GetAsync(string url, string token, ActivityNode node, string activity, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("X-Shopify-Access-Token", token);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if ((int)resp.StatusCode >= 400)
            {
                var detail = resp.StatusCode == HttpStatusCode.NotFound
                    ? $"{activity}: order not found (HTTP 404). Body: {Truncate(raw)}"
                    : $"{activity}: Shopify API error HTTP {(int)resp.StatusCode}. Body: {Truncate(raw)}";
                throw new WorkflowException(node.Id, detail);
            }
            return raw;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new WorkflowException(node.Id, $"{activity}: request timed out: {Truncate(url)}");
        }
        catch (WorkflowException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new WorkflowException(node.Id, $"{activity}: request failed: {ex.Message}");
        }
    }

    /// <summary>Truncates long response bodies so error messages never flood the logs.</summary>
    private static string Truncate(string s) =>
        s.Length <= 300 ? s : s[..300] + "... (truncated)";
}