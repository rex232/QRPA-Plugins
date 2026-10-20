using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using QRpa.Core;

namespace JntExpressPlugin;

// ═══════════════════════════════════════════════════════════════════════════════
// J&T Express third-party plugin for QRpa (QApp.QRpa).
// Provides J&T Express (J&T international logistics) automation:
//   Jnt.OrderCreate  Order create API      creates a waybill
//   Jnt.QueryOrder   Trace query API       queries the shipment status
//   Jnt.LabelPrint   Label print API       fetches the label image or text
//
// ██████████████████████████████████████████████████████████████████████████
// Open-source information
// ██████████████████████████████████████████████████████████████████████████
// Copyright: Copyright (c) 2026 Queeny App (Queeny App Ltd.). All rights reserved.
// License: MIT License.
// This plugin is open-sourced under the MIT License. Anyone may use, copy, modify,
// merge, publish, distribute, sublicense, and sell copies of the software.
// Repository: announced on the QRpa official website https://qrpaapp.com when released.
// Author: Queeny App IT Department.
// Docs: QRpa official website https://qrpaapp.com | J&T Express open platform https://open.jtexpress.cn.
// Disclaimer: this plugin is independently developed by Queeny App. It is not
// provided, endorsed, or sponsored by J&T Express or its affiliates; all J&T
// Express trademarks belong to their respective owners.
// Security: this plugin never writes or logs plaintext keys in flow files
// (.flow.json). Keep credentials in QRpa's CredentialStore and reference them
// as {secret:name}.
//
// ██████████████████████████████████████████████████████████████████████████
// Build and install
// ██████████████████████████████████████████████████████████████████████████
// Build:   dotnet build JntExpressPlugin.csproj
// Install: copy the built JntExpressPlugin.dll into the plugins folder next to the
// host (Robot / Studio auto-scan the plugins folder beside the executable at startup;
// Robot also accepts --pluginDir <folder>).
// Remove: delete JntExpressPlugin.dll from the plugins folder.
//
// ██████████████████████████████████████████████████████████████████████████
// Prerequisites
// ██████████████████████████████████████████████████████████████████████████
// 1. Register on the J&T Express open platform (open.jtexpress.cn) to obtain the
//    customer code (customerCode) and the secret key (secretKey).
// 2. Keep customerCode/secretKey in the QRpa CredentialStore and reference them
//    in the flow as {secret:name}, so no plaintext secret leaks into .flow.json.
//
// ██████████████████████████████████████████████████████████████████████████
// Signing (per the J&T Express open-platform md5 digest rule)
// ██████████████████████████████████████████████████████████████████████████
//   sign = md5HexLower( paramsJson + secretKey )
// where:
//   - paramsJson is the exact UTF-8 JSON string of the business payload (the request
//     body, serialized with the same options used for transmission; it does NOT
//     contain the signature field itself, which is appended afterwards);
//   - md5HexLower is the 32-character lowercase hex MD5 digest;
//   - secretKey is the customer secret issued by the J&T open platform.
// The digest is carried inside the request JSON under the field named by the
// signerName argument (default "sign"). The server recomputes md5HexLower( body
// minus the signerName field + secretKey ) and compares it with the received
// digest, so signing and verification always agree.
//
// ██████████████████████████████████████████████████████████████████████████
// Testing
// ██████████████████████████████████████████████████████████████████████████
// The baseUrl argument of every activity can point to a local fake server (or a
// J&T sandbox), so a workflow can run end-to-end without a live network.
// Fake customerCode/secretKey values are fine for testing; the plugin's signing
// is internally consistent with the verification rule above.
//
// ██████████████████████████████████████████████████████████████████████████
// The full MIT license text is kept in the project root LICENSE file, if any.
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// J&T Express plugin entry point: implements <see cref="IActivityPlugin"/> and registers three
/// logistics activities. It also exposes an internal constructor accepting an injected
/// <see cref="JntClient"/> for testing the API responses.
/// </summary>
public sealed class JntActivityPlugin : IActivityPlugin
{
    /// <summary>
    /// Globally unique plugin identifier (naming convention: vendor.domain).
    /// </summary>
    public string Id => "jnt.express";

    /// <summary>
    /// Short plugin description (for display).
    /// </summary>
    public string Description => "J&T Express logistics activities: create order, query order status, print label (J&T Express open platform).";

    private readonly JntClient _client;

    /// <summary>
    /// Parameterless constructor used by PluginHost (Activator.CreateInstance needs it).
    /// </summary>
    public JntActivityPlugin()
        : this(new JntClient())
    {
    }

    /// <summary>
    /// Test constructor: injects a custom <see cref="JntClient"/> to simulate J&T API responses.
    /// </summary>
    internal JntActivityPlugin(JntClient client) => _client = client;

    /// <summary>
    /// Registers every activity of this plugin (called once at startup by PluginHost; never
    /// re-register during a run). The kind in the workflow JSON is the activity name directly
    /// (for example "Jnt.OrderCreate"); Category uses the built-in "logistics" palette group.
    /// </summary>
    public void Register()
    {
        ActivityRegistry.RegisterPlugin(new PluginDescriptor
        {
            Name = "Jnt.OrderCreate",
            Title = "J&T create order",
            Category = "logistics",
            Hint = "J&T create order: customerCode/secretKey=J&T credentials (use {secret:name}) | "
                 + "senderName/senderPhone/senderAddress=shipper (required, senderProvince/senderCity optional) | "
                 + "receiverName/receiverPhone/receiverAddress=receiver (required, receiverProvince/receiverCity optional) | "
                 + "weight=kg (default 1) | orderNo=your order no (optional) | goodsType/productType/remark=optional | "
                 + "extraParams=extra JSON fields (optional) | baseUrl=API host (default https://open.jtexpress.cn) | "
                 + "orderPath=API path (default /waybill/jstmt-trace-order-custom/create) | signerName=signature field (default sign) | "
                 + "resultVar=response JSON variable (default order); the waybill number is written to {resultVar}.waybill",
            Defaults = new Dictionary<string, string>
            {
                ["baseUrl"] = JntClient.DefaultBaseUrl,
                ["orderPath"] = "/waybill/jstmt-trace-order-custom/create",
                ["customerCode"] = "{secret:jntCustomerCode}",
                ["secretKey"] = "{secret:jntSecretKey}",
                ["signerName"] = "sign",
                ["weight"] = "1",
                ["resultVar"] = "order",
            },
            Handler = OrderCreate,
        });

        ActivityRegistry.RegisterPlugin(new PluginDescriptor
        {
            Name = "Jnt.QueryOrder",
            Title = "J&T query order",
            Category = "logistics",
            Hint = "J&T query order: waybillNo=waybill number (required, may reference {order.waybill}) | "
                 + "baseUrl=API host (default https://open.jtexpress.cn) | queryPath=API path (default /waybill/jstmt-trace-query) | "
                 + "signerName=signature field (default sign) | resultVar=track JSON variable (default track); "
                 + "the latest milestone is written to {resultVar}.status and {resultVar}.desc",
            Defaults = new Dictionary<string, string>
            {
                ["baseUrl"] = JntClient.DefaultBaseUrl,
                ["queryPath"] = "/waybill/jstmt-trace-query",
                ["customerCode"] = "{secret:jntCustomerCode}",
                ["secretKey"] = "{secret:jntSecretKey}",
                ["signerName"] = "sign",
                ["resultVar"] = "track",
            },
            Handler = QueryOrder,
        });

        ActivityRegistry.RegisterPlugin(new PluginDescriptor
        {
            Name = "Jnt.LabelPrint",
            Title = "J&T label print",
            Category = "logistics",
            Hint = "J&T label print: waybillNo=waybill number (required, may reference {order.waybill}) | "
                 + "output=save path (e.g. {env.TEMP}\\jnt-label.png) | baseUrl=API host (default https://open.jtexpress.cn) | "
                 + "labelPath=API path (default /waybill/order/jstmt-trace-by-waybill) | signerName=signature field (default sign) | "
                 + "resultVar=file path variable (default labelPath)",
            Defaults = new Dictionary<string, string>
            {
                ["baseUrl"] = JntClient.DefaultBaseUrl,
                ["labelPath"] = "/waybill/order/jstmt-trace-by-waybill",
                ["customerCode"] = "{secret:jntCustomerCode}",
                ["secretKey"] = "{secret:jntSecretKey}",
                ["signerName"] = "sign",
                ["output"] = "{env.TEMP}\\jnt-label.png",
                ["resultVar"] = "labelPath",
            },
            Handler = LabelPrint,
        });
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Activity 1: J&T order create
    // ═══════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// J&T order create: submits the customer, shipper, receiver, and parcel data to create a
    /// waybill. The full response JSON goes to resultVar, and the waybill number detected from
    /// common response fields (waybillNo/waybill/number and so on) goes to {resultVar}.waybill.
    /// </summary>
    private Task<IReadOnlyList<ActivityNode>?> OrderCreate(ActivityNode node, WorkflowContext ctx, CancellationToken ct)
    {
        // Assemble the business payload (JsonObject skips empty fields; blanks are not sent).
        var body = new JsonObject();
        AddStr(body, "customerCode", Require(node, ctx, "customerCode"));
        AddStr(body, "childCode", ctx.GetStr(node, "childCode"));
        AddStr(body, "orderNo", ctx.GetStr(node, "orderNo"));
        AddStr(body, "senderName", Require(node, ctx, "senderName"));
        AddStr(body, "senderPhone", Require(node, ctx, "senderPhone"));
        AddStr(body, "senderProvince", ctx.GetStr(node, "senderProvince"));
        AddStr(body, "senderCity", ctx.GetStr(node, "senderCity"));
        AddStr(body, "senderAddress", Require(node, ctx, "senderAddress"));
        AddStr(body, "receiverName", Require(node, ctx, "receiverName"));
        AddStr(body, "receiverPhone", Require(node, ctx, "receiverPhone"));
        AddStr(body, "receiverProvince", ctx.GetStr(node, "receiverProvince"));
        AddStr(body, "receiverCity", ctx.GetStr(node, "receiverCity"));
        AddStr(body, "receiverAddress", Require(node, ctx, "receiverAddress"));
        AddStr(body, "weight", ctx.GetStr(node, "weight", "1"));   // unit: kilograms
        AddStr(body, "goodsType", ctx.GetStr(node, "goodsType"));
        AddStr(body, "productType", ctx.GetStr(node, "productType"));
        AddStr(body, "remark", ctx.GetStr(node, "remark"));

        // Contract-specific escape hatch: extraParams is a JSON object merged key-by-key.
        MergeExtra(body, ctx.GetStr(node, "extraParams"));

        return CreateAsync(node, ctx, ct, body);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Activity 2: J&T order status query
    // ═══════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// J&T order status query: fetches the shipment trace by waybill number. The full response
    /// JSON goes to resultVar; the latest milestone is written to {resultVar}.status and
    /// {resultVar}.desc (both empty when no trace is found).
    /// </summary>
    private Task<IReadOnlyList<ActivityNode>?> QueryOrder(ActivityNode node, WorkflowContext ctx, CancellationToken ct)
    {
        var body = new JsonObject();
        AddStr(body, "customerCode", Require(node, ctx, "customerCode"));
        AddStr(body, "waybillNo", Require(node, ctx, "waybillNo"));

        return QueryAsync(node, ctx, ct, body);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Activity 3: J&T label print
    // ═══════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// J&T label print: fetches the label by waybill number and saves it to the output path.
    /// The label may be Base64 image data (matched by field names such as label/img/imageBase64/
    /// pdf/data/file), a download URL, or plain text; all three are saved accordingly.
    /// </summary>
    private Task<IReadOnlyList<ActivityNode>?> LabelPrint(ActivityNode node, WorkflowContext ctx, CancellationToken ct)
    {
        var body = new JsonObject();
        AddStr(body, "customerCode", Require(node, ctx, "customerCode"));
        AddStr(body, "waybillNo", Require(node, ctx, "waybillNo"));

        var output = ctx.GetStr(node, "output");
        if (output.Length == 0)
            throw new WorkflowException(node.Id, "J&T label print: 'output' save path is empty.");

        return PrintAsync(node, ctx, ct, body, output);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Shared execution logic
    // ═══════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// Common order-create flow: call the API, write the raw response to resultVar, then detect
    /// the waybill number and write it to {resultVar}.waybill (empty when not found).
    /// </summary>
    private async Task<IReadOnlyList<ActivityNode>?> CreateAsync(
        ActivityNode node, WorkflowContext ctx, CancellationToken ct, JsonObject body)
    {
        var result = await CallAsync(node, ctx, ct, "orderPath", "/waybill/jstmt-trace-order-custom/create", body);

        var resultVar = ctx.GetStr(node, "resultVar", "order");
        ctx.Set(resultVar, result.Raw);

        var waybill = PickWaybill(result.Raw);
        ctx.Set(resultVar + ".waybill", waybill ?? "");

        ctx.LogLine?.Invoke(waybill is null
            ? "[J&T] order submitted (no waybill number found in the response)."
            : $"[J&T] order submitted, waybill {waybill}");
        return null;
    }

    /// <summary>
    /// Common label-print flow: call the API, extract Base64 / URL / plain text from the response,
    /// and save the file. resultVar receives the saved file path.
    /// </summary>
    private async Task<IReadOnlyList<ActivityNode>?> PrintAsync(
        ActivityNode node, WorkflowContext ctx, CancellationToken ct, JsonObject body, string output)
    {
        var result = await CallAsync(node, ctx, ct, "labelPath", "/waybill/order/jstmt-trace-by-waybill", body);

        // 1) Base64 payload scanned by common field names (deep search through the response).
        foreach (var key in new[] { "label", "img", "imageBase64", "pdf", "data", "file", "fileData", "printData", "imgData" })
        {
            foreach (var candidate in CollectValues(result.Raw, key))
            {
                if (string.IsNullOrEmpty(candidate)) continue;
                if (TryDecodeBase64(candidate, out var bytes))
                {
                    SaveBytes(output, bytes);
                    FinishLabel(node, ctx, result, output);
                    return null;
                }
            }
        }

        // 2) Download URL (common field names are scanned).
        foreach (var key in new[] { "printUrl", "fileUrl", "labelUrl", "imgUrl", "imageUrl", "pdfUrl", "url" })
        {
            foreach (var candidate in CollectValues(result.Raw, key))
            {
                if (string.IsNullOrEmpty(candidate)) continue;
                if (candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    var bytes = _client.DownloadAsync(candidate, ct).GetAwaiter().GetResult();
                    SaveBytes(output, bytes);
                    FinishLabel(node, ctx, result, output);
                    return null;
                }
            }
        }

        // 3) Plain text payload: the first non-empty candidate value, or the raw response.
        var text = PickFirstValue(result.Raw, "label", "img", "imageBase64", "pdf", "data", "file", "content", "html");
        SaveText(output, text ?? result.Raw);
        FinishLabel(node, ctx, result, output);
        return null;
    }

    /// <summary>
    /// Variable write, resultVar handling, and logging after the label is saved.
    /// </summary>
    private void FinishLabel(ActivityNode node, WorkflowContext ctx, JntApiResult result, string output)
    {
        ctx.Set(ctx.GetStr(node, "resultVar", "labelPath"), output);
        ctx.LogLine?.Invoke($"[J&T] label saved: {output}");
    }

    /// <summary>
    /// Common status-query flow: call the API, write the raw response to resultVar, then extract
    /// the latest milestone into {resultVar}.status and {resultVar}.desc (empty when none found).
    /// </summary>
    private async Task<IReadOnlyList<ActivityNode>?> QueryAsync(
        ActivityNode node, WorkflowContext ctx, CancellationToken ct, JsonObject body)
    {
        var result = await CallAsync(node, ctx, ct, "queryPath", "/waybill/jstmt-trace-query", body);

        var resultVar = ctx.GetStr(node, "resultVar", "track");
        ctx.Set(resultVar, result.Raw);

        var milestones = FindMilestones(result.Raw);
        var status = "";
        var desc = "";
        if (milestones.Count > 0)
        {
            var last = milestones[^1];
            status = last.TryGetValue("status", out var s) && s is string ss ? ss : "";
            desc = last.TryGetValue("desc", out var d) && d is string dd ? dd : "";
        }
        ctx.Set(resultVar + ".status", status);
        ctx.Set(resultVar + ".desc", desc);

        ctx.LogLine?.Invoke($"[J&T] order query done: {milestones.Count} milestone(s).");
        return null;
    }

    /// <summary>
    /// Reads the flow arguments and calls the J&T API, unifying signing, path resolution, and
    /// HTTP-level error wrapping. baseUrl, secretKey, and signerName are resolved here.
    /// </summary>
    private async Task<JntApiResult> CallAsync(
        ActivityNode node, WorkflowContext ctx, CancellationToken ct,
        string pathArg, string defaultPath, JsonObject body)
    {
        var secretKey = Require(node, ctx, "secretKey");
        var baseUrl = ctx.GetStr(node, "baseUrl", JntClient.DefaultBaseUrl);
        var path = ctx.GetStr(node, pathArg, defaultPath);
        var signerName = ctx.GetStr(node, "signerName", "sign");

        try
        {
            return await _client.PostAsync(baseUrl, path, secretKey, signerName, body, ct);
        }
        catch (JntApiException ex)
        {
            // JntClient only throws HTTP-level errors; wrap them into WorkflowException so logs identify the node.
            throw new WorkflowException(node.Id, ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Response parsing helpers
    // ═══════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// Detects the waybill number from the raw response: common field names are probed by deep
    /// search, the first non-empty hit wins.
    /// </summary>
    private static string? PickWaybill(string raw)
    {
        foreach (var key in new[] { "waybillNo", "waybill_No", "waybill", "mailNo", "mail_no", "number", "waybill_number", "billCode" })
        {
            var v = PickFirstValue(raw, key);
            if (!string.IsNullOrEmpty(v)) return v;
        }
        return null;
    }

    /// <summary>
    /// Finds the first JSON array whose items look like trace milestones (contain status/desc/
    /// time/remark-like fields), then normalizes each item to a {status, desc} object.
    /// </summary>
    private static List<Dictionary<string, string>> FindMilestones(string raw)
    {
        var list = new List<Dictionary<string, string>>();
        try
        {
            if (JsonNode.Parse(raw) is not { } root) return list;
            var arr = FindTrackArray(root);
            if (arr is null) return list;
            foreach (var item in arr.OfType<JsonObject>())
            {
                var status = GetProp(item, "status") ?? GetProp(item, "statusCode")
                          ?? GetProp(item, "code") ?? GetProp(item, "opcode") ?? "";
                var desc = GetProp(item, "desc") ?? GetProp(item, "description")
                        ?? GetProp(item, "remark") ?? GetProp(item, "processInfo") ?? "";
                list.Add(new Dictionary<string, string> { ["status"] = status, ["desc"] = desc });
            }
        }
        catch (JsonException)
        {
            // unparseable responses simply yield no milestones
        }
        return list;
    }

    /// <summary>
    /// Deep search for the track array: the first JSON array whose items contain at least one
    /// trace-like key is returned; the search walks the whole tree in document order.
    /// </summary>
    private static JsonArray? FindTrackArray(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (_, child) in obj)
                {
                    if (FindTrackArray(child) is { } hit) return hit;
                }
                return null;
            case JsonArray arr:
                if (arr.OfType<JsonObject>().Any(HasTrackKey)) return arr;
                foreach (var child in arr)
                {
                    if (FindTrackArray(child) is { } hit) return hit;
                }
                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// Whether the milestone object carries any trace-like key.
    /// </summary>
    private static bool HasTrackKey(JsonObject o) => o.Any(kv =>
        kv.Key is "status" or "statusCode" or "code" or "opcode"
            or "desc" or "description" or "remark" or "processInfo"
            or "time" or "acceptTime" or "location" or "city");

    /// <summary>
    /// Deep search: collects every string value stored under the given key anywhere in the
    /// response tree (handles nested wrappers such as data/result/info).
    /// </summary>
    private static IEnumerable<string> CollectValues(string raw, string key)
    {
        // yield cannot live inside try+catch, so the parse is kept separate.
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            // unparseable bodies contribute nothing
            yield break;
        }
        if (root is not null)
        {
            foreach (var v in CollectValues(root, key)) yield return v;
        }
    }

    private static IEnumerable<string> CollectValues(JsonNode? node, string key)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (k, child) in obj)
                {
                    if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)
                        && child is JsonValue v && v.TryGetValue<string>(out var value) && !string.IsNullOrEmpty(value))
                    {
                        yield return value;
                    }
                    else
                    {
                        foreach (var nested in CollectValues(child, key)) yield return nested;
                    }
                }
                break;
            case JsonArray arr:
                foreach (var child in arr)
                foreach (var nested in CollectValues(child, key))
                    yield return nested;
                break;
        }
    }

    /// <summary>
    /// Picks the first non-empty string stored under one of the given keys (deep search; the
    /// first key with a hit wins, so the key order expresses the priority).
    /// </summary>
    private static string? PickFirstValue(string raw, params string[] keys)
    {
        foreach (var key in keys)
        {
            foreach (var v in CollectValues(raw, key))
            {
                if (!string.IsNullOrEmpty(v)) return v;
            }
        }
        return null;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// Required-argument check: throws a node-readable error when the value is blank.
    /// </summary>
    private static string Require(ActivityNode node, WorkflowContext ctx, string arg)
    {
        var v = ctx.GetStr(node, arg).Trim();
        if (v.Length == 0)
            throw new WorkflowException(node.Id, $"J&T activity: required argument '{arg}' is empty.");
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
    /// Merges extraParams (a JSON object string) key-by-key into the business payload; malformed
    /// or non-object input is ignored (contract extras must never break the flow).
    /// </summary>
    private static void MergeExtra(JsonObject target, string extra)
    {
        if (string.IsNullOrEmpty(extra)) return;
        try
        {
            if (JsonNode.Parse(extra) is JsonObject obj)
            {
                foreach (var (k, v) in obj) target[k] = v?.DeepClone();
            }
        }
        catch (JsonException)
        {
            // bad contract fields are skipped, not fatal
        }
    }

    /// <summary>
    /// Reads a string field from the JSON object (returns only when the key exists and is non-empty).
    /// </summary>
    private static string? GetProp(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var el)) return null;
        var s = el.Deserialize<string>() ?? el?.ToJsonString();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    /// <summary>
    /// Tries to decode a string as Base64 bytes (allows a data-URI prefix such as
    /// data:image/png;base64,...); returns false on failure.
    /// </summary>
    private static bool TryDecodeBase64(string candidate, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        var s = candidate.Trim();
        var comma = s.IndexOf("base64,", StringComparison.OrdinalIgnoreCase);
        if (comma >= 0) s = s[(comma + 7)..];
        if (s.Length < 20) return false;   // too short to be label data
        try
        {
            bytes = Convert.FromBase64String(s);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Ensures the directory exists, then writes the bytes.
    /// </summary>
    private static void SaveBytes(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>
    /// Ensures the directory exists, then writes the text as UTF-8 (plain-text labels).
    /// </summary>
    private static void SaveText(string path, string text)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, text, Encoding.UTF8);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// J&T API client
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// J&T HTTP-level error: thrown by <see cref="JntClient"/> on network failures or non-success
/// HTTP statuses.
/// </summary>
public sealed class JntApiException : Exception
{
    /// <summary>
    /// Creates the exception with a message.
    /// </summary>
    public JntApiException(string message) : base(message) { }
}

/// <summary>
/// J&T API response (raw text preserved for the activities to parse themselves).
/// </summary>
public sealed record JntApiResult(string Raw)
{
    /// <summary>
    /// The raw response body.
    /// </summary>
    public string Raw { get; } = Raw;
}

/// <summary>
/// J&T API client: signing, JSON posting, and HTTP error handling; the HttpClient is injectable
/// for tests. Follows the same optional HttpClient injection pattern as the core <c>LlmClient</c>.
/// </summary>
public sealed class JntClient
{
    /// <summary>
    /// Production J&T Express open-platform endpoint (the baseUrl argument can override it for a sandbox).
    /// </summary>
    public const string DefaultBaseUrl = "https://open.jtexpress.cn";

    private readonly HttpClient _http;

    /// <summary>
    /// Creates the client; accepts an injected HttpClient (fake handler for tests) and a timeout.
    /// </summary>
    public JntClient(HttpClient? http = null, TimeSpan? timeout = null)
        => _http = http ?? new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(60) };

    /// <summary>
    /// Sends a J&T business request and returns the raw response text.
    /// Signing (per the J&T Express open-platform md5 digest rule):
    ///   sign = md5HexLower( paramsJson + secretKey )
    /// paramsJson is the exact JSON string of the payload without the signature field; the digest
    /// is appended under the field named by signerName ("sign" by default) and transmitted in the
    /// same JSON body. Both signing and transmission serialize with the same options, so the
    /// server can recompute the digest after removing the signature field and compare.
    /// </summary>
    public async Task<JntApiResult> PostAsync(
        string baseUrl, string path, string secretKey, string signerName, JsonObject body, CancellationToken ct)
    {
        var options = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        // The signed string is the body as it will be transmitted, minus the signature field.
        var unsigned = body.ToJsonString(options);
        if (!string.IsNullOrEmpty(signerName))
        {
            body[signerName] = Md5Hex(unsigned + secretKey);
        }
        var json = body.ToJsonString(options);

        var url = baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        string raw;
        try
        {
            using var resp = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
            raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new JntApiException($"J&T API HTTP {(int)resp.StatusCode}: {Truncate(raw, 300)}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new JntApiException($"J&T API request timed out: {Truncate(url, 120)}");
        }
        catch (JntApiException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new JntApiException($"J&T API network error: {ex.Message}");
        }

        return new JntApiResult(raw);
    }

    /// <summary>
    /// Downloads a label URL into bytes (when the API responds with an image/PDF URL instead of Base64).
    /// </summary>
    public async Task<byte[]> DownloadAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TaskCanceledException or HttpRequestException)
        {
            throw new JntApiException($"J&T label URL download failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Computes the 32-character lowercase hex MD5 digest of the UTF-8 input.
    /// </summary>
    private static string Md5Hex(string input)
        => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(input)));

    /// <summary>
    /// Truncates overly long text so error messages do not flood the logs.
    /// </summary>
    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}