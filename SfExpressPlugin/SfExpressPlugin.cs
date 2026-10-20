using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using QRpa.Core;

namespace SFExpressPlugin;

// ═══════════════════════════════════════════════════════════════════════════════
// SF Express third-party plugin for QRpa (QApp.QRpa).
// Provides SF Express logistics automation: order create, label print, and order status query.
//
// ██████████████████████████████████████████████████████████████████████████
// Open-source information
// ██████████████████████████████████████████████████████████████████████████
// Copyright: Copyright (c) 2026 Queeny App (Queeny App Ltd.). All rights reserved.
// License: MIT License. Anyone may use, copy, modify, merge, publish, distribute,
// sublicense, and sell copies of this software, provided that the above copyright
// notice and this permission notice are included in all copies or substantial
// portions of the software.
// Repository: announced on the QRpa website when released (https://qrpaapp.com).
// Author: Queeny App IT Department.
// Docs: QRpa website https://qrpaapp.com | SF open platform https://open.sf-express.com
// Disclaimer: this plugin is independently developed by Queeny App. It is not
// provided, endorsed, or sponsored by SF Express or its affiliates; all SF Express
// trademarks belong to their respective owners.
// Security: this plugin never writes or logs plaintext keys in flow files
// (.flow.json). Keep credentials in the QRpa CredentialStore and reference them
// as {secret:name}.
//
// ██████████████████████████████████████████████████████████████████████████
// Activity overview
// ██████████████████████████████████████████████████████████████████████████
//   Sf.OrderCreate  Unified order API (EXP_RECE_CREATE_ORDER)          creates a waybill
//   Sf.LabelPrint   Cloud print API (COM_RECE_CLOUD_PRINT_WAYBILLS)     fetches the label PDF/image
//   Sf.QueryOrder   Route query API (EXP_RECE_SEARCH_ROUTES)            queries the order status
//
// ██████████████████████████████████████████████████████████████████████████
// Build and install
// ██████████████████████████████████████████████████████████████████████████
// Build:   dotnet build SfExpressPlugin.csproj
// Install: copy the built SfExpressPlugin.dll into the plugins folder next to the host:
//            - Robot/Studio auto-scan the plugins folder beside the executable on startup.
//            - Robot can also pass --pluginDir <folder>.
// Remove: delete SfExpressPlugin.dll from the plugins folder.
//
// ██████████████████████████████████████████████████████████████████████████
// Prerequisites
// ██████████████████████████████████████████████████████████████████████████
// 1. Register on the SF open platform (open.sf-express.com) to obtain the
//    partnerID (customer code) and the checkword.
// 2. Store the partnerID/checkword in the QRpa CredentialStore and reference
//    them in the flow as {secret:name} so no plaintext secrets leak into .flow.json.
//
// ██████████████████████████████████████████████████████████████████████████
// Parameters (see each activity's comments for details)
// ██████████████████████████████████████████████████████████████████████████
// Each activity reads its arguments from the workflow node. Secrets use {secret:name}.
//
// ██████████████████████████████████████████████████████████████████████████
// Signing (per the official Fengqiao SDK)
// ██████████████████████████████████████████████████████████████████████████
// msgDigest = base64( md5( urlencode(msgData) + timestamp + checkword ) )
// The timestamp is Unix milliseconds; the body is posted as a form
// (application/x-www-form-urlencoded), with msgData being the string value of the
// business JSON. Signing and transmission use the same PHP-style urlencode so they
// always match, letting the server recompute and verify the digest from the
// untouched message.
//
// ██████████████████████████████████████████████████████████████████████████
// Testing
// ██████████████████████████████████████████████████████████████████████████
// The baseUrl argument of every activity can point to a local fake server (or the
// Fengqiao SIT environment), so a workflow can run end-to-end without a live network.
// Fake partnerID/checkword values are fine for testing.
//
// ██████████████████████████████████████████████████████████████████████████
// The full MIT license text is kept in the project root LICENSE file (if any).
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// SF Express plugin entry point: implements <see cref="IActivityPlugin"/> and registers three logistics activities. It also exposes an internal constructor taking an injected <see cref="SfClient"/> for unit-testing the API responses.
/// </summary>
public sealed class SfActivityPlugin : IActivityPlugin
{
    /// <summary>
    /// Globally unique plugin identifier (naming convention: vendor.domain).
    /// </summary>
    public string Id => "sf.express";

    /// <summary>
    /// Short plugin description (for display).
    /// </summary>
    public string Description => "順豐物流活動：下單、打印面單、查詢訂單狀態（對接順豐丰橋開放平台）。";

    private readonly SfClient _client;

    /// <summary>
    /// Parameterless constructor used by PluginHost (Activator.CreateInstance needs it).
    /// </summary>
    public SfActivityPlugin()
        : this(new SfClient())
    {
    }

    /// <summary>
    /// Test constructor: injects a custom <see cref="SfClient"/> to simulate the SF API responses.
    /// </summary>
    internal SfActivityPlugin(SfClient client) => _client = client;

    /// <summary>
    /// Registers all activities of this plugin (called once at startup by PluginHost; never re-register during a run). The kind in the workflow JSON is the Name directly (e.g. "Sf.OrderCreate"); Category uses the built-in "logistics", shown as "Logistics Center" in the palette.
    /// </summary>
    public void Register()
    {
        ActivityRegistry.RegisterPlugin(new PluginDescriptor
        {
            Name = "Sf.OrderCreate",
            Title = "順豐下單（插件）",
            Category = "logistics",
            Hint = "下單：fromContact/fromPhone/fromAddress=寄件人 | toContact/toPhone/toAddress=收件人 | "
                 + "weight/length/width/height/quantity=包裹 | payMethod=1寄付|2到付|3第三方付 | "
                 + "partnerID/checkword=丰橋憑證(建議{secret:}) | baseUrl=接口地址(可空) | resultVar=運單號變數",
            Defaults = new Dictionary<string, string>
            {
                ["baseUrl"] = SfClient.DefaultBaseUrl,
                ["partnerID"] = "{secret:sfPartner}",
                ["checkword"] = "{secret:sfCheckword}",
                ["requestID"] = "{uid}",
                ["expressType"] = "1",
                ["payMethod"] = "1",
                ["quantity"] = "1",
                ["weight"] = "1",
                ["resultVar"] = "sfWaybill",
            },
            Handler = OrderCreate,
        });

        ActivityRegistry.RegisterPlugin(new PluginDescriptor
        {
            Name = "Sf.LabelPrint",
            Title = "順豐打印面單（插件）",
            Category = "logistics",
            Hint = "打印面單：waybillNo=運單號(可引用{sfWaybill}) | output=保存路徑(如{env.TEMP}\\sf-label.pdf) | "
                 + "templateCode=面單模板(可空) | partnerID/checkword=丰橋憑證(建議{secret:}) | resultVar=文件路徑變數",
            Defaults = new Dictionary<string, string>
            {
                ["baseUrl"] = SfClient.DefaultBaseUrl,
                ["partnerID"] = "{secret:sfPartner}",
                ["checkword"] = "{secret:sfCheckword}",
                ["requestID"] = "{uid}",
                ["output"] = "{env.TEMP}\\sf-label.pdf",
                ["resultVar"] = "sfLabelPath",
            },
            Handler = LabelPrint,
        });

        ActivityRegistry.RegisterPlugin(new PluginDescriptor
        {
            Name = "Sf.QueryOrder",
            Title = "順豐查詢訂單狀態（插件）",
            Category = "logistics",
            Hint = "查詢狀態：waybillNo=運單號(可引用{sfWaybill}) | trackingType=1按運單號|2按客戶訂單號 | "
                 + "partnerID/checkword=丰橋憑證(建議{secret:}) | resultVar=路由JSON | statusVar/lastTimeVar/lastRemarkVar=摘要變數",
            Defaults = new Dictionary<string, string>
            {
                ["baseUrl"] = SfClient.DefaultBaseUrl,
                ["partnerID"] = "{secret:sfPartner}",
                ["checkword"] = "{secret:sfCheckword}",
                ["requestID"] = "{uid}",
                ["trackingType"] = "1",
                ["resultVar"] = "sfRoutes",
                ["statusVar"] = "sfStatus",
            },
            Handler = QueryOrder,
        });
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Activity 1: SF Express order create (EXP_RECE_CREATE_ORDER)
    // ═══════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// SF order create: submits the shipper, receiver, and parcel data, creates a waybill, and writes the waybill number into the variable named by resultVar; the full response is written to detailVar (optional).
    /// </summary>
    private Task<IReadOnlyList<ActivityNode>?> OrderCreate(ActivityNode node, WorkflowContext ctx, CancellationToken ct)
    {
        // Assemble the business message (JsonObject skips empty fields; blank ones are not sent).
        var data = new JsonObject();
        AddStr(data, "orderid", ctx.GetStr(node, "orderId"));
        AddStr(data, "j_contact", Require(node, ctx, "fromContact", "寄件人聯絡人"));
        AddStr(data, "j_tel", Require(node, ctx, "fromPhone", "寄件人電話"));
        AddStr(data, "j_address", Require(node, ctx, "fromAddress", "寄件人地址"));
        AddStr(data, "j_company", ctx.GetStr(node, "fromCompany"));
        AddStr(data, "d_contact", Require(node, ctx, "toContact", "收件人聯絡人"));
        AddStr(data, "d_tel", Require(node, ctx, "toPhone", "收件人電話"));
        AddStr(data, "d_address", Require(node, ctx, "toAddress", "收件人地址"));
        AddStr(data, "d_company", ctx.GetStr(node, "toCompany"));
        AddStr(data, "express_type", ctx.GetStr(node, "expressType", "1"));          // product type: 1 = standard express
        AddStr(data, "pay_method", ctx.GetStr(node, "payMethod", "1"));              // 1 = shipper pays, 2 = receiver pays, 3 = third party
        AddStr(data, "custid", ctx.GetStr(node, "monthAccount"));                    // monthly settlement card (required for third-party pay)
        AddStr(data, "parcel_quantity", ctx.GetStr(node, "quantity", "1"));
        AddStr(data, "cargo_length", ctx.GetStr(node, "length"));
        AddStr(data, "cargo_width", ctx.GetStr(node, "width"));
        AddStr(data, "cargo_height", ctx.GetStr(node, "height"));
        AddStr(data, "cargo_weight", ctx.GetStr(node, "weight", "1"));               // unit: kilograms
        AddStr(data, "remark", ctx.GetStr(node, "remark"));

        // Call the Fengqiao API synchronously (SfClient handles signing and form posting).
        return CreateAsync(node, ctx, ct, "EXP_RECE_CREATE_ORDER", data);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Activity 2: SF Express label print (COM_RECE_CLOUD_PRINT_WAYBILLS)
    // ═══════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// SF label print: requests the label from the cloud-print API (sync mode returns a PDF/image) and saves it to the output path. The label payload may be Base64 (matched by field names such as label/pdf/imgData) or a download URL (printUrl/pdfUrl etc.); both are supported. The output file extension follows the output argument (use .pdf or .png).
    /// </summary>
    private Task<IReadOnlyList<ActivityNode>?> LabelPrint(ActivityNode node, WorkflowContext ctx, CancellationToken ct)
    {
        var data = new JsonObject();
        AddStr(data, "waybillNo", Require(node, ctx, "waybillNo", "順豐運單號"));
        AddStr(data, "templateCode", ctx.GetStr(node, "templateCode"));

        var output = ctx.GetStr(node, "output");
        if (output.Length == 0)
            throw new WorkflowException(node.Id, "順豐打印面單：output 保存路徑未填。");

        return PrintAsync(node, ctx, ct, "COM_RECE_CLOUD_PRINT_WAYBILLS", data, output);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Activity 3: SF Express order status query (EXP_RECE_SEARCH_ROUTES)
    // ═══════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// SF order status query: fetches the routing (waybill tracking) and outputs a JSON array plus summary variables. Routes go to resultVar (a JSON array); statusVar holds the latest milestone description, lastTimeVar the latest time, and lastRemarkVar the latest remark; the full response is written to detailVar (optional).
    /// </summary>
    private Task<IReadOnlyList<ActivityNode>?> QueryOrder(ActivityNode node, WorkflowContext ctx, CancellationToken ct)
    {
        var data = new JsonObject();
        AddStr(data, "trackingType", ctx.GetStr(node, "trackingType", "1"));   // 1 = by waybill number, 2 = by customer order id
        AddStr(data, "methodType", ctx.GetStr(node, "methodType", "1"));       // 1 = standard query
        AddStr(data, "waybillNo", ctx.GetStr(node, "waybillNo"));

        var orderId = ctx.GetStr(node, "orderId");
        if (data["waybillNo"] is null && orderId.Length == 0)
            throw new WorkflowException(node.Id, "順豐查詢訂單狀態：waybillNo（或 orderId）未填。");
        AddStr(data, "orderid", orderId);

        return QueryAsync(node, ctx, ct, "EXP_RECE_SEARCH_ROUTES", data);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Shared execution logic
    // ═══════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// Common order-create flow: build the request → call the API → check head → read waybill_no into the variable.
    /// </summary>
    private async Task<IReadOnlyList<ActivityNode>?> CreateAsync(
        ActivityNode node, WorkflowContext ctx, CancellationToken ct,
        string serviceCode, JsonObject data)
    {
        var result = await CallAsync(node, ctx, ct, serviceCode, data);

        // The inner business reply (msgData itself is a JSON string).
        var inner = ParseInnerObject(node, result);
        var waybill = PickStr(inner, "waybill_no") ?? PickStr(inner, "waybillNo");
        if (string.IsNullOrEmpty(waybill))
            throw new WorkflowException(node.Id, $"順豐下單：回應未包含運單號。原始回覆：{Truncate(result.Raw)}");

        ctx.Set(ctx.GetStr(node, "resultVar"), waybill);
        var detailVar = ctx.GetStr(node, "detailVar");
        if (detailVar.Length > 0) ctx.Set(detailVar, result.MsgData);

        ctx.LogLine?.Invoke($"[順豐] 下單成功：運單號 {waybill}");
        return null;
    }

    /// <summary>
    /// Common label-print flow: call the cloud-print API → extract the Base64 label or URL from the response → save the file.
    /// </summary>
    private async Task<IReadOnlyList<ActivityNode>?> PrintAsync(
        ActivityNode node, WorkflowContext ctx, CancellationToken ct,
        string serviceCode, JsonObject data, string output)
    {
        var result = await CallAsync(node, ctx, ct, serviceCode, data);

        // Try to read the label payload from msgData (Base64 or URL).
        var inner = TryParseInnerObject(result.MsgData) ?? ParseInnerObject(node, result);
        byte[]? bytes = null;
        // Common Base64 field names for the label payload (priority order).
        foreach (var key in new[] { "pdf", "label", "labelBase64", "pdfBase64", "imgData", "waybillImg", "fileData", "printData", "imageBase64", "waybillPdf" })
        {
            var candidate = PickStr(inner, key);
            if (string.IsNullOrEmpty(candidate)) continue;
            if (TryDecodeBase64(candidate, out bytes!)) break;
        }

        if (bytes is not null)
        {
            SaveBytes(output, bytes);
        }
        else
        {
            // If there is no Base64, try downloading from a URL (common field names are scanned).
            var url = PickStr(inner, "printUrl") ?? PickStr(inner, "pdfUrl")
                   ?? PickStr(inner, "fileUrl") ?? PickStr(inner, "labelUrl")
                   ?? PickStr(inner, "imgUrl") ?? PickStr(inner, "url");
            if (string.IsNullOrEmpty(url))
                throw new WorkflowException(node.Id, $"順豐打印面單：回應無 Base64 資料或可下載網址。原始回覆：{Truncate(result.Raw)}");
            bytes = _client.DownloadAsync(url, ct).GetAwaiter().GetResult();
            SaveBytes(output, bytes);
        }

        ctx.Set(ctx.GetStr(node, "resultVar"), output);
        var detailVar = ctx.GetStr(node, "detailVar");
        if (detailVar.Length > 0) ctx.Set(detailVar, result.MsgData);

        ctx.LogLine?.Invoke($"[順豐] 面單已保存：{output}（{bytes.Length} 字節）");
        return null;
    }

    /// <summary>
    /// Common status-query flow: call the route API → extract the route array → write the JSON and summary variables.
    /// </summary>
    private async Task<IReadOnlyList<ActivityNode>?> QueryAsync(
        ActivityNode node, WorkflowContext ctx, CancellationToken ct,
        string serviceCode, JsonObject data)
    {
        var result = await CallAsync(node, ctx, ct, serviceCode, data);
        var inner = ParseInnerObject(node, result);

        // Compatible with two shapes: routeResps[].routes[] (grouped) or a flat routes[].
        var routes = new List<JsonObject>();
        if (inner.TryGetPropertyValue("routeResps", out var groups) && groups is JsonArray ga)
        {
            foreach (var group in ga.OfType<JsonObject>())
            {
                if (group.TryGetPropertyValue("routes", out var rs) && rs is JsonArray ra)
                    routes.AddRange(ra.OfType<JsonObject>());
                else
                    routes.Add(group);   // flattened routes keyed by waybill
            }
        }
        else if (inner.TryGetPropertyValue("routes", out var all) && all is JsonArray ar)
        {
            routes.AddRange(ar.OfType<JsonObject>());
        }

        if (routes.Count == 0)
            throw new WorkflowException(node.Id, $"順豐查詢訂單狀態：回應無路由資料。原始回覆：{Truncate(result.Raw)}");

        // Normalize each milestone to three columns: time, location, and description (handy for later Table or Log use).
        var list = new JsonArray();
        foreach (var r in routes)
        {
            var item = new JsonObject
            {
                ["time"] = PickStr(r, "acceptTime") ?? PickStr(r, "accept_time"),
                ["location"] = PickStr(r, "acceptAddress") ?? PickStr(r, "location") ?? PickStr(r, "opcode"),
                ["remark"] = PickStr(r, "remark") ?? PickStr(r, "status"),
            };
            list.Add(item);
        }

        var json = list.ToJsonString(new JsonSerializerOptions
        {
            // Keep CJK characters readable (no \uXXXX escapes) so the flow variable is human-readable.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        var resultVar = ctx.GetStr(node, "resultVar");
        if (resultVar.Length > 0) ctx.Set(resultVar, json);

        // Summary: the latest milestone (last item) is treated as the current status.
        var last = routes[^1];
        ctx.Set(ctx.GetStr(node, "statusVar", "sfStatus"),
                PickStr(last, "remark") ?? PickStr(last, "status") ?? "已攬收");
        var timeVar = ctx.GetStr(node, "lastTimeVar");
        if (timeVar.Length > 0) ctx.Set(timeVar, PickStr(last, "acceptTime") ?? PickStr(last, "accept_time") ?? "");
        var remarkVar = ctx.GetStr(node, "lastRemarkVar");
        if (remarkVar.Length > 0) ctx.Set(remarkVar, PickStr(last, "remark") ?? PickStr(last, "status") ?? "");

        var detailVar = ctx.GetStr(node, "detailVar");
        if (detailVar.Length > 0) ctx.Set(detailVar, result.MsgData);

        ctx.LogLine?.Invoke($"[順豐] 訂單狀態查詢完成：共 {routes.Count} 站路由");
        return null;
    }

    /// <summary>
    /// Reads the flow arguments and calls the Fengqiao API, unifying signing, HTTP errors, and head=ERR handling. The four shared arguments baseUrl, partnerID, checkword, and requestID are resolved here.
    /// </summary>
    private async Task<SfApiResult> CallAsync(
        ActivityNode node, WorkflowContext ctx, CancellationToken ct,
        string serviceCode, JsonObject data)
    {
        var partnerId = Require(node, ctx, "partnerID", "順豐客戶編碼 partnerID");
        var checkword = Require(node, ctx, "checkword", "順豐校驗碼 checkword");
        var requestId = ctx.GetStr(node, "requestID");
        if (requestId.Length == 0) requestId = Guid.NewGuid().ToString("N");
        var baseUrl = ctx.GetStr(node, "baseUrl", SfClient.DefaultBaseUrl);

        try
        {
            return await _client.PostAsync(baseUrl, partnerId, checkword, requestId, serviceCode, data.ToJsonString(), ct);
        }
        catch (SfApiException ex)
        {
            // SfClient only throws HTTP-level errors; wrap them into WorkflowException so logs identify the node.
            throw new WorkflowException(node.Id, ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// Required-argument check: throws a node-readable error when the value is blank.
    /// </summary>
    private static string Require(ActivityNode node, WorkflowContext ctx, string arg, string label)
    {
        var v = ctx.GetStr(node, arg).Trim();
        if (v.Length == 0)
            throw new WorkflowException(node.Id, $"順豐活動：{label}（參數 {arg}）未填。");
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
    /// Reads a string field from the JSON object (returns only when the key exists and is non-empty).
    /// </summary>
    private static string? PickStr(JsonObject obj, string key)
    {
        if (obj is null || !obj.TryGetPropertyValue(key, out var el)) return null;
        var s = el.Deserialize<string>() ?? el?.ToJsonString();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    /// <summary>
    /// Parses the outer response's msgData string into a JSON object (msgData is a JSON string inside).
    /// </summary>
    private static JsonObject ParseInnerObject(ActivityNode node, SfApiResult result)
    {
        var obj = TryParseInnerObject(result.MsgData);
        if (obj is not null) return obj;
        throw new WorkflowException(node.Id, $"順豐活動：msgData 無法解析為 JSON。原始回覆：{Truncate(result.Raw)}");
    }

    /// <summary>
    /// Safely parses a JSON string into an object (returns null on malformed input instead of throwing).
    /// </summary>
    private static JsonObject? TryParseInnerObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Tries to decode a string as Base64 bytes (allows a data-URI prefix); returns false on failure.
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
    /// Truncates overly long response bodies so error messages do not flood the logs.
    /// </summary>
    private static string Truncate(string s) =>
        s.Length <= 500 ? s : s[..500] + "…（已截斷）";
}

// ═══════════════════════════════════════════════════════════════════════════════
// SF API client
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Fengqiao HTTP-level error: thrown by <see cref="SfClient"/> on network failures or non-success HTTP statuses.
/// </summary>
public sealed class SfApiException : Exception
{
    /// <summary>
    /// Creates the exception with a message.
    /// </summary>
    public SfApiException(string message) : base(message) { }
}

/// <summary>
/// Simplified Fengqiao API response (only the fields the business needs are extracted).
/// </summary>
public sealed record SfApiResult(string Head, string MsgData, string ErrorCode, string ErrorMessage, string Raw)
{
    /// <summary>
    /// Whether the business call succeeded (head == "OK").
    /// </summary>
    public bool Success => string.Equals(Head, "OK", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Fengqiao API client: signing, form posting, and response parsing; the HttpClient is injectable for tests. Follows the same "optional HttpClient injection" pattern as the core <c>LlmClient</c>.
/// </summary>
public sealed class SfClient
{
    /// <summary>
    /// Production Fengqiao endpoint (the baseUrl argument can override it for a sandbox).
    /// </summary>
    public const string DefaultBaseUrl = "https://sfapi.sf-express.com/std/service";

    private readonly HttpClient _http;

    /// <summary>
    /// Creates the client; accepts an injected HttpClient (fake handler for tests) and a timeout.
    /// </summary>
    public SfClient(HttpClient? http = null, TimeSpan? timeout = null)
        => _http = http ?? new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(60) };

    /// <summary>
    /// Sends a Fengqiao business request and parses the response. Signing: msgDigest = base64( md5( urlencode(msgData) + timestamp + checkword ) ), timestamp in Unix milliseconds. The body is posted as a form; msgData is transported with the same PHP-style urlencode used for signing.
    /// </summary>
    public async Task<SfApiResult> PostAsync(
        string baseUrl, string partnerId, string checkword, string requestId,
        string serviceCode, string msgData, CancellationToken ct)
    {
        var timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var digest = Convert.ToBase64String(
            MD5.HashData(Encoding.UTF8.GetBytes(PhpUrlEncode(msgData) + timestamp + checkword)));

        var body = $"partnerID={PhpUrlEncode(partnerId)}"
                 + $"&requestID={PhpUrlEncode(requestId)}"
                 + $"&serviceCode={PhpUrlEncode(serviceCode)}"
                 + $"&timestamp={timestamp}"
                 + $"&msgData={PhpUrlEncode(msgData)}"
                 + $"&msgDigest={digest}";

        using var content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");
        string raw;
        try
        {
            using var resp = await _http.PostAsync(baseUrl, content, ct).ConfigureAwait(false);
            raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new SfApiException($"順豐接口 HTTP {(int)resp.StatusCode}：{Truncate(raw)}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SfApiException($"順豐接口請求逾時：{Truncate(baseUrl)}");
        }
        catch (SfApiException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new SfApiException($"順豐接口網絡錯誤：{ex.Message}");
        }

        return Parse(raw);
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
            throw new SfApiException($"順豐面單網址下載失敗：{ex.Message}");
        }
    }

    /// <summary>
    /// Parses the Fengqiao outer response: extracts head, error, and the msgData string.
    /// </summary>
    private static SfApiResult Parse(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new SfApiResult("ERR", raw, "", "回應非 JSON 物件", Truncate(raw));

            var head = doc.RootElement.TryGetProperty("head", out var h) ? h.GetString() ?? "" : "";
            var msgData = doc.RootElement.TryGetProperty("msgData", out var m) ? m.GetString() ?? "" : "";

            // The error field may be an object ({code,message}) or a JSON string.
            var code = "";
            var message = "";
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.Object)
                {
                    code = err.TryGetProperty("code", out var ec) ? ec.GetString() ?? "" : "";
                    message = err.TryGetProperty("message", out var em) ? em.GetString() ?? "" : "";
                }
                else if (err.ValueKind == JsonValueKind.String)
                {
                    var s = err.GetString() ?? "";
                    var inner = TryParse(s);
                    if (inner is not null)
                    {
                        code = inner.RootElement.TryGetProperty("code", out var ic) ? ic.GetString() ?? "" : "";
                        message = inner.RootElement.TryGetProperty("message", out var im) ? im.GetString() ?? "" : "";
                    }
                    else
                    {
                        message = s;
                    }
                }
            }
            var msg = string.IsNullOrEmpty(message)
                ? (doc.RootElement.TryGetProperty("errorMessage", out var em2) ? em2.GetString() ?? "" : "")
                : message;
            return new SfApiResult(head, msgData, code, msg, Truncate(raw));
        }
        catch (JsonException)
        {
            return new SfApiResult("ERR", "", "", "回應無法解析為 JSON", Truncate(raw));
        }
    }

    private static JsonDocument? TryParse(string json)
    {
        try { return JsonDocument.Parse(json); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// PHP urlencode-style encoding (RFC 1738): space becomes +, everything else except alphanumerics and -_. becomes %XX uppercase hex. Signing and transport share this encoding, so the server can recompute the digest from the decoded message.
    /// </summary>
    internal static string PhpUrlEncode(string s)
    {
        var sb = new StringBuilder(s.Length * 3);
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            if ((b >= '0' && b <= '9') || (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z')
                || b == (byte)'-' || b == (byte)'_' || b == (byte)'.')
            {
                sb.Append((char)b);
            }
            else if (b == (byte)' ')
            {
                sb.Append('+');
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Truncates overly long response bodies so error messages do not flood the logs.
    /// </summary>
    private static string Truncate(string s) =>
        s.Length <= 500 ? s : s[..500] + "…（已截斷）";
}