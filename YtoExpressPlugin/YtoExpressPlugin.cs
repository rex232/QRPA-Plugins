using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using QRpa.Core;

namespace YtoExpressPlugin;

// ═══════════════════════════════════════════════════════════════════════════════
// YTO Express Third-party Plugin
// ─────────────────────────────────────────────────────────────
// Provides YTO Express logistics automation for QRpa (QApp.QRpa):
// order create, label print, and track query.
//
// ██████████████████████████████████████████████████████████████████████████
// Open-source information
// ██████████████████████████████████████████████████████████████████████████
// Copyright: Copyright (c) 2026 Queeny App (Queeny App Ltd.). All rights reserved.
// License: MIT License.
// This plugin is open-sourced under the MIT License. Anyone may use, copy,
// modify, merge, publish, distribute, sublicense, and sell copies of the
// software, provided that the above copyright notice and this permission
// notice are included in all copies or substantial portions of the software.
// Repository: announced on the QRpa official website https://qrpaapp.com when released.
// Author: Queeny App IT Department.
// Docs: QRpa official website https://qrpaapp.com and YTO open platform http://open.yto.net.cn.
// Disclaimer: This plugin is independently developed by Queeny App and is not
// provided, endorsed, or sponsored by YTO Express or its affiliates; all YTO
// trademarks belong to their respective owners.
// Security: This plugin never writes or logs plaintext keys in flow files
// (.flow.json). Keep credentials in QRpa's CredentialStore and reference them
// as {secret:name}.
//
// ██████████████████████████████████████████████████████████████████████████
// Activity overview
// ██████████████████████████████████████████████████████████████████████████
//   Yto.OrderCreate  Home-delivery service (order API)   Creates a waybill.
//   Yto.LabelPrint   Label print API                     Fetches label HTML or image.
//   Yto.QueryOrder   Track query API                     Queries shipment status.
//
// ██████████████████████████████████████████████████████████████████████████
// Build and install
// ██████████████████████████████████████████████████████████████████████████
// Build: dotnet build YtoExpressPlugin.csproj
// Install: copy the built YtoExpressPlugin.dll into the plugins folder next to
// the host:
//   - Robot / Studio: they auto-scan the plugins folder beside the executable
//     on startup.
//   - Robot: you may also pass --pluginDir <folder>.
// Remove: delete YtoExpressPlugin.dll from the plugins folder to uninstall.
//
// ██████████████████████████████████████████████████████████████████████████
// Prerequisites
// ██████████████████████████████████████████████████████████████████████████
// 1. Register and pass enterprise verification on the YTO open platform
//    (open.yto.net.cn), then apply for the required APIs. The console's
//    Interface Management provides the test address (baseUrl), method (method),
//    version (v), and the customer secret (secret).
// 2. Keep the customer secret in QRpa's CredentialStore and reference it as
//    {secret:name} in flows, so no plaintext secret leaks into .flow.json.
// 3. The method, version, and business field names of each API follow your
//    contract with YTO. The extraParams escape hatch attaches contract-specific
//    fields without code changes.
//
// ██████████████████████████████████████████████████████████████████████████
// Parameters — see each activity's comments
// ██████████████████████████████████████████████████████████████████████████
//   Yto.OrderCreate:
//     fromContact/fromPhone/fromAddress = shipper (required) | toContact/toPhone/toAddress = receiver (required)
//     orderId = customer order number (optional) | customerId = customer number (optional) | goodsName = goods name (optional)
//     weight = weight in kilograms (default 1) | length/width/height = dimensions (optional) | quantity = item count (default 1)
//     baseUrl/method/v = provided by the console | secret = customer secret (use {secret:ytoSecret})
//     format = reply format (default JSON) | extraParams = contract extra fields as JSON (optional)
//     resultVar = waybill number variable | detailVar = full response variable (optional)
//   Yto.LabelPrint:
//     waybillNo = waybill number (required, may reference {ytoWaybill}) | customerId = customer number (optional)
//     printName = print name (optional) | output = save path (default {env.TEMP}\yto-label.html)
//     resultVar = file path variable
//   Yto.QueryOrder:
//     waybillNo = waybill number (required, one waybill per call) | resultVar = track JSON
//     statusVar/lastTimeVar/lastRemarkVar = summary variables (optional)
//
// ██████████████████████████████████████████████████████████████████████████
// Unified envelope and signing
// ██████████████████████████████████████████████████████████████████████████
// The request is submitted over HTTPS POST; the body is JSON:
// { timestamp, param, sign, format: "JSON", method, v }.
// Signing: sign = Base64( MD5( param + method + v + secret ) ).
// param is the business argument as a JSON string embedded in the outer body;
// the timestamp is Unix milliseconds. Signing and transport share the exact
// same param string, so the server can recompute and verify.
//
// ██████████████████████████████████████████████████████████████████████████
// Testing
// ██████████████████████████████████████████████████████████████████████████
// The baseUrl argument of every activity can point to a local fake server or
// the YTO SIT environment for end-to-end runs. A fake secret works too, since
// the plugin's signing is internally consistent.
//
// ██████████████████████████████████████████████████████████████████████████
// The full MIT license text is kept in the project root LICENSE file, if any.
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// YTO Express plugin entry point: implements <see cref="IActivityPlugin"/> and registers three logistics activities.
/// It also exposes an internal constructor accepting an injected <see cref="YtoClient"/> for unit-testing API responses.
/// </summary>
public sealed class YtoActivityPlugin : IActivityPlugin
{
    /// <summary>
    /// Globally unique plugin identifier (naming convention: vendor.domain).
    /// </summary>
    public string Id => "yto.express";

    /// <summary>
    /// Short plugin description (for display).
    /// </summary>
    public string Description => "圓通物流活動：下單、打印面單、查詢物流軌跡（對接圓通開放平台）。";

    private readonly YtoClient _client;

    /// <summary>
    /// Parameterless constructor used by PluginHost (Activator.CreateInstance needs it).
    /// </summary>
    public YtoActivityPlugin()
        : this(new YtoClient())
    {
    }

    /// <summary>
    /// Test constructor: injects a custom <see cref="YtoClient"/> to simulate YTO API responses.
    /// </summary>
    internal YtoActivityPlugin(YtoClient client) => _client = client;

    /// <summary>
    /// Registers every activity of this plugin (called once at startup by PluginHost; never re-register during a run).
    /// The kind in workflow JSON is the activity name directly (for example "Yto.OrderCreate"); Category uses the built-in "logistics" (the palette shows the logistics center).
    /// </summary>
    public void Register()
    {
        ActivityRegistry.RegisterPlugin(new PluginDescriptor
        {
            Name = "Yto.OrderCreate",
            Title = "圓通下單（插件）",
            Category = "logistics",
            Hint = "下單：fromContact/fromPhone/fromAddress=寄件人 | toContact/toPhone/toAddress=收件人 | "
                 + "weight/length/width/height/quantity=包裹 | baseUrl=測試地址(控制台) | method/v=接口方法與版本(控制台) | "
                 + "secret=客戶密鑰(建議{secret:ytoSecret}) | extraParams=合約額外欄位JSON(可空) | resultVar=運單號變數",
            Defaults = new Dictionary<string, string>
            {
                ["secret"] = "{secret:ytoSecret}",
                ["format"] = "JSON",
                ["quantity"] = "1",
                ["weight"] = "1",
                ["resultVar"] = "ytoWaybill",
            },
            Handler = OrderCreate,
        });

        ActivityRegistry.RegisterPlugin(new PluginDescriptor
        {
            Name = "Yto.LabelPrint",
            Title = "圓通打印面單（插件）",
            Category = "logistics",
            Hint = "面單打印：waybillNo=運單號(可引用{ytoWaybill}) | output=保存路徑(如{env.TEMP}\\yto-label.html) | "
                 + "baseUrl/method/v=控制台提供 | secret=客戶密鑰(建議{secret:ytoSecret}) | resultVar=文件路徑變數",
            Defaults = new Dictionary<string, string>
            {
                ["secret"] = "{secret:ytoSecret}",
                ["format"] = "JSON",
                ["output"] = "{env.TEMP}\\yto-label.html",
                ["resultVar"] = "ytoLabelPath",
            },
            Handler = LabelPrint,
        });

        ActivityRegistry.RegisterPlugin(new PluginDescriptor
        {
            Name = "Yto.QueryOrder",
            Title = "圓通查詢物流軌跡（插件）",
            Category = "logistics",
            Hint = "查詢軌跡：waybillNo=運單號(必填，一次一單) | baseUrl/method/v=控制台提供 | "
                 + "secret=客戶密鑰(建議{secret:ytoSecret}) | resultVar=軌跡JSON | statusVar/lastTimeVar/lastRemarkVar=摘要變數",
            Defaults = new Dictionary<string, string>
            {
                ["secret"] = "{secret:ytoSecret}",
                ["format"] = "JSON",
                ["resultVar"] = "ytoRoutes",
                ["statusVar"] = "ytoStatus",
            },
            Handler = QueryOrder,
        });
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Activity 1: YTO order create (home-delivery service)
    // ═══════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// YTO order create: submits shipper, receiver, and parcel data, creates a waybill, and writes the waybill number to a variable.
    /// The waybill number is detected from common response fields (mailNo, waybillNo, number, and so on); the full response goes to detailVar (optional).
    /// </summary>
    private Task<IReadOnlyList<ActivityNode>?> OrderCreate(ActivityNode node, WorkflowContext ctx, CancellationToken ct)
    {
        // Assemble the business params (JsonObject only writes filled fields; blanks are not sent).
        var param = new JsonObject();
        AddStr(param, "customerOrderNo", ctx.GetStr(node, "orderId"));
        AddStr(param, "customerId", ctx.GetStr(node, "customerId"));
        AddStr(param, "senderName", Require(node, ctx, "fromContact", "寄件人聯絡人"));
        AddStr(param, "senderMobile", Require(node, ctx, "fromPhone", "寄件人電話"));
        AddStr(param, "senderAddress", Require(node, ctx, "fromAddress", "寄件人地址"));
        AddStr(param, "receiverName", Require(node, ctx, "toContact", "收件人聯絡人"));
        AddStr(param, "receiverMobile", Require(node, ctx, "toPhone", "收件人電話"));
        AddStr(param, "receiverAddress", Require(node, ctx, "toAddress", "收件人地址"));
        AddStr(param, "goodsName", ctx.GetStr(node, "goodsName"));
        AddStr(param, "weight", ctx.GetStr(node, "weight", "1"));                 // unit: kilograms
        AddStr(param, "length", ctx.GetStr(node, "length"));
        AddStr(param, "width", ctx.GetStr(node, "width"));
        AddStr(param, "height", ctx.GetStr(node, "height"));
        AddStr(param, "quantity", ctx.GetStr(node, "quantity", "1"));

        // Contract-specific escape hatch: extraParams is a JSON object string merged key-by-key into the params.
        MergeExtra(param, ctx.GetStr(node, "extraParams"));

        return CreateAsync(node, ctx, ct, param);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Activity 2: YTO label print
    // ═══════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// YTO label print: fetches the label and saves it to the output path.
    /// The label may be Base64 (matched by field names such as label, file, or imgData), a URL (printUrl, fileUrl, and so on),
    /// or HTML. All three are supported; the output file type follows the output path (.html for HTML, .png for images).
    /// </summary>
    private Task<IReadOnlyList<ActivityNode>?> LabelPrint(ActivityNode node, WorkflowContext ctx, CancellationToken ct)
    {
        var waybill = Require(node, ctx, "waybillNo", "圓通運單號");
        var output = ctx.GetStr(node, "output");
        if (output.Length == 0)
            throw new WorkflowException(node.Id, "圓通面單打印：output 保存路徑未填。");

        var param = new JsonObject { ["waybillNo"] = waybill };
        AddStr(param, "customerId", ctx.GetStr(node, "customerId"));
        AddStr(param, "printName", ctx.GetStr(node, "printName"));
        MergeExtra(param, ctx.GetStr(node, "extraParams"));

        return PrintAsync(node, ctx, ct, param, output);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Activity 3: YTO track query
    // ═══════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// YTO track query: fetches shipment info by waybill number and outputs a JSON array plus summary variables.
    /// Each milestone contains time, city, info (status code), and desc (description); the infoContent code is
    /// translated to a Chinese status (for example SIGNED becomes a delivered status).
    /// </summary>
    private Task<IReadOnlyList<ActivityNode>?> QueryOrder(ActivityNode node, WorkflowContext ctx, CancellationToken ct)
    {
        var waybill = Require(node, ctx, "waybillNo", "圓通運單號");

        // The official track query param: the waybill field is NUMBER (one waybill per call).
        var param = new JsonObject { ["NUMBER"] = waybill };

        return QueryAsync(node, ctx, ct, param);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Shared execution logic
    // ═══════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// Common order-create flow: build the request, call the API, detect the waybill, and write variables.
    /// </summary>
    private async Task<IReadOnlyList<ActivityNode>?> CreateAsync(
        ActivityNode node, WorkflowContext ctx, CancellationToken ct, JsonObject param)
    {
        var result = await CallAsync(node, ctx, ct, param);
        var waybill = PickWaybill(result);
        if (waybill is null)
            throw new WorkflowException(node.Id, $"圓通下單：回應未包含運單號。原始回覆：{Truncate(result.Raw)}");

        ctx.Set(ctx.GetStr(node, "resultVar"), waybill);
        var detailVar = ctx.GetStr(node, "detailVar");
        if (detailVar.Length > 0) ctx.Set(detailVar, result.Raw);

        ctx.LogLine?.Invoke($"[圓通] 下單成功：運單號 {waybill}");
        return null;
    }

    /// <summary>
    /// Common label-print flow: call the API, extract Base64, URL, or HTML from the response, and save the file.
    /// </summary>
    private async Task<IReadOnlyList<ActivityNode>?> PrintAsync(
        ActivityNode node, WorkflowContext ctx, CancellationToken ct, JsonObject param, string output)
    {
        var result = await CallAsync(node, ctx, ct, param);
        var obj = SearchValue(result.Raw);

        // 1) Base64 payload (most common)
        foreach (var key in new[] { "label", "file", "imgData", "imageBase64", "pdf", "printData", "data" })
        {
            var candidate = GetProp(obj, key);
            if (candidate is null || candidate.Length < 20) continue;
            if (TryDecodeBase64(candidate, out var bytes))
            {
                SaveBytes(output, bytes);
                FinishLabel(node, ctx, result, output);
                return null;
            }
        }

        // 2) URL download
        foreach (var key in new[] { "printUrl", "fileUrl", "labelUrl", "imgUrl", "url", "imageUrl" })
        {
            var candidate = GetProp(obj, key);
            if (string.IsNullOrEmpty(candidate)) continue;
            var bytes2 = _client.DownloadAsync(candidate, ct).GetAwaiter().GetResult();
            SaveBytes(output, bytes2);
            FinishLabel(node, ctx, result, output);
            return null;
        }

        // 3) HTML label (some electronic labels return HTML)
        foreach (var key in new[] { "html", "htmlContent", "labelHtml", "content" })
        {
            var candidate = GetProp(obj, key);
            if (string.IsNullOrEmpty(candidate) || !candidate.TrimStart().StartsWith("<", StringComparison.Ordinal)) continue;
            SaveText(output, candidate);
            FinishLabel(node, ctx, result, output);
            return null;
        }

        throw new WorkflowException(node.Id, $"圓通面單打印：回應無 Base64／網址／HTML 面單資料。原始回覆：{Truncate(result.Raw)}");
    }

    /// <summary>
    /// Variable writes and logging after the label is saved (avoids repeating code across the three save branches).
    /// </summary>
    private void FinishLabel(ActivityNode node, WorkflowContext ctx, YtoApiResult result, string output)
    {
        ctx.Set(ctx.GetStr(node, "resultVar"), output);
        var detailVar = ctx.GetStr(node, "detailVar");
        if (detailVar.Length > 0) ctx.Set(detailVar, result.Raw);
        ctx.LogLine?.Invoke($"[圓通] 面單已保存：{output}");
    }

    /// <summary>
    /// Common track-query flow: call the API, normalize the milestones, and write JSON and summary variables.
    /// </summary>
    private async Task<IReadOnlyList<ActivityNode>?> QueryAsync(
        ActivityNode node, WorkflowContext ctx, CancellationToken ct, JsonObject param)
    {
        var result = await CallAsync(node, ctx, ct, param);
        var routes = ParseRoutes(result.Raw);

        var json = Serialize(routes);
        var resultVar = ctx.GetStr(node, "resultVar");
        if (resultVar.Length > 0) ctx.Set(resultVar, json);

        if (routes.Count > 0)
        {
            var last = routes[^1];
            ctx.Set(ctx.GetStr(node, "statusVar", "ytoStatus"), StatusName(GetProp(last, "info")));
            var timeVar = ctx.GetStr(node, "lastTimeVar");
            if (timeVar.Length > 0) ctx.Set(timeVar, GetProp(last, "time") ?? "");
            var remarkVar = ctx.GetStr(node, "lastRemarkVar");
            if (remarkVar.Length > 0) ctx.Set(remarkVar, GetProp(last, "desc") ?? "");
        }
        else if (ctx.GetStr(node, "statusVar").Length > 0)
        {
            // Empty result: still set the summary so the flow does not break on an empty track.
            ctx.Set(ctx.GetStr(node, "statusVar"), "無軌跡結果");
        }

        var detailVar = ctx.GetStr(node, "detailVar");
        if (detailVar.Length > 0) ctx.Set(detailVar, result.Raw);

        ctx.LogLine?.Invoke($"[圓通] 軌跡查詢完成：共 {routes.Count} 站");
        return null;
    }

    /// <summary>
    /// Reads the flow arguments and calls the YTO API, unifying signing and HTTP-level errors.
    /// The four shared arguments baseUrl, secret, method, and v are resolved here; the business params go out as param.
    /// </summary>
    private async Task<YtoApiResult> CallAsync(
        ActivityNode node, WorkflowContext ctx, CancellationToken ct, JsonObject param)
    {
        var secret = Require(node, ctx, "secret", "圓通客戶密鑰 secret");
        var method = Require(node, ctx, "method", "圓通接口方法 method");
        var version = Require(node, ctx, "v", "圓通接口版本 v");
        var baseUrl = Require(node, ctx, "baseUrl", "圓通接口地址 baseUrl");
        var format = ctx.GetStr(node, "format", "JSON");

        // Signing and transport must use the identical param string, so param is serialized first.
        var paramStr = param.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        try
        {
            return await _client.PostAsync(baseUrl, secret, method, version, format, paramStr, ct);
        }
        catch (YtoApiException ex)
        {
            // YtoClient only throws HTTP-level errors; wrap them into WorkflowException so logs identify the node.
            throw new WorkflowException(node.Id, ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Response parsing
    // ═══════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// Detects the waybill number from the raw response: common field names are probed inside-out, bounded to two levels.
    /// </summary>
    private static string? PickWaybill(YtoApiResult result)
    {
        var obj = SearchValue(result.Raw);
        if (obj is null) return null;
        foreach (var key in new[] { "waybillNo", "waybill_No", "mailNo", "mail_no", "number", "waybill_number" })
        {
            var v = GetProp(obj, key);
            if (!string.IsNullOrEmpty(v)) return v;
        }
        return null;
    }

    /// <summary>
    /// Normalizes the track response into a {time, city, info, desc} array.
    /// Compatible with two shapes: the official track API returns a bare array (items with waybill_No, upload_Time,
    /// infoContent, and processInfo), or the track list nested under a common wrapper (data, result, traceList, or list as an array).
    /// An empty result (success is true with no data) returns an empty list.
    /// </summary>
    private static List<JsonObject> ParseRoutes(string raw)
    {
        var arr = FindArray(raw);
        var list = new List<JsonObject>();
        foreach (var node in arr.OfType<JsonObject>())
        {
            list.Add(new JsonObject
            {
                ["time"] = GetProp(node, "upload_Time") ?? GetProp(node, "uploadTime") ?? "",
                ["city"] = GetProp(node, "city") ?? "",
                ["info"] = GetProp(node, "infoContent") ?? "",
                ["desc"] = GetProp(node, "processInfo") ?? "",
            });
        }
        return list;
    }

    /// <summary>
    /// Finds the track array: the root array is used directly, otherwise common wrapper fields are probed.
    /// </summary>
    private static JsonArray FindArray(string raw)
    {
        try
        {
            var root = JsonNode.Parse(raw);
            if (root is JsonArray arr) return arr;
            if (root is JsonObject obj)
            {
                foreach (var key in new[] { "data", "result", "traceList", "trace", "list", "logistics", "info", "map" })
                {
                    if (obj.TryGetPropertyValue(key, out var el) && el is JsonArray nested) return nested;
                }
            }
        }
        catch (JsonException)
        {
        }
        return new JsonArray();
    }

    /// <summary>
    /// Converts the root JSON into a JsonObject, or null when the root is an array or unparseable.
    /// Common wrapper keys (data, result, info, content) are descended one level when they are objects,
    /// making the business payload easier to read.
    /// </summary>
    private static JsonObject? SearchValue(string raw)
    {
        try
        {
            if (JsonNode.Parse(raw) is not JsonObject obj) return null;
            foreach (var w in new[] { "data", "result", "info", "content" })
            {
                if (obj.TryGetPropertyValue(w, out var inner) && inner is JsonObject innerObj) obj = innerObj;
            }
            return obj;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a string field (returns only when the key exists and is non-empty).
    /// </summary>
    private static string? GetProp(JsonObject? obj, string key)
    {
        if (obj is null || !obj.TryGetPropertyValue(key, out var el)) return null;
        var s = el.Deserialize<string>() ?? el?.ToJsonString();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    /// <summary>
    /// Translates an infoContent code into a Chinese status; unknown codes are returned as-is.
    /// </summary>
    private static string StatusName(string? code)
    {
        if (string.IsNullOrEmpty(code)) return "已交寄";
        return code.ToUpperInvariant() switch
        {
            "GOT" => "已攬收",
            "ARRIVAL" => "已收入",
            "DEPARTURE" => "已發出",
            "SENT_SCAN" => "派件中",
            "INBOUND" => "自提櫃入櫃",
            "SIGNED" => "簽收成功",
            "FAILED" => "簽收失敗",
            "FORWARDING" => "轉寄中",
            "TMS_RETURN" => "已退回",
            "AIRSEND" => "航空發貨",
            "AIRPICK" => "航空提貨",
            _ => code,
        };
    }

    /// <summary>
    /// Serializes the milestones to JSON (keeps Chinese readable).
    /// </summary>
    private static string Serialize(List<JsonObject> routes)
    {
        var arr = new JsonArray();
        foreach (var r in routes) arr.Add(r);
        return arr.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// Required-argument check: throws a node-readable error when blank.
    /// </summary>
    private static string Require(ActivityNode node, WorkflowContext ctx, string arg, string label)
    {
        var v = ctx.GetStr(node, arg).Trim();
        if (v.Length == 0)
            throw new WorkflowException(node.Id, $"圓通活動：{label}（參數 {arg}）未填。");
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
    /// Merges extraParams (a JSON object string) key-by-key into the business params; malformed or non-object input is ignored.
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
    /// Ensures the directory exists, then writes the text (HTML label).
    /// </summary>
    private static void SaveText(string path, string text)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, text, Encoding.UTF8);
    }

    /// <summary>
    /// Truncates overly long response bodies so error messages do not flood the logs.
    /// </summary>
    private static string Truncate(string s) =>
        s.Length <= 500 ? s : s[..500] + "…（已截斷）";
}

// ═══════════════════════════════════════════════════════════════════════════════
// YTO API client
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// YTO HTTP-level error: thrown by <see cref="YtoClient"/> on network failures or non-success HTTP statuses.
/// </summary>
public sealed class YtoApiException : Exception
{
    /// <summary>
    /// Creates the exception with a message.
    /// </summary>
    public YtoApiException(string message) : base(message) { }
}

/// <summary>
/// YTO API response (raw text preserved for the activities to parse themselves).
/// </summary>
public sealed record YtoApiResult(string Raw)
{
    /// <summary>
    /// The raw response body.
    /// </summary>
    public string Raw { get; } = Raw;
}

/// <summary>
/// YTO API client: signing, JSON posting, and HTTP error handling; the HttpClient is injectable for tests.
/// Follows the same optional HttpClient injection pattern as the core <c>LlmClient</c>.
/// </summary>
public sealed class YtoClient
{
    private readonly HttpClient _http;

    /// <summary>
    /// Creates the client; accepts an injected HttpClient (fake handler for tests) and a timeout.
    /// </summary>
    public YtoClient(HttpClient? http = null, TimeSpan? timeout = null)
        => _http = http ?? new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(60) };

    /// <summary>
    /// Sends a YTO business request and returns the raw response text.
    /// Signing: sign = Base64( MD5( param + method + v + secret ) ), timestamp in Unix milliseconds;
    /// the JSON body { timestamp, param, sign, format, method, v } is posted over HTTPS.
    /// </summary>
    public async Task<YtoApiResult> PostAsync(
        string baseUrl, string secret, string method, string version, string format,
        string param, CancellationToken ct)
    {
        var timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var sign = Convert.ToBase64String(MD5.HashData(Encoding.UTF8.GetBytes(param + method + version + secret)));

        // The outer body's character encoding must match the param string used for signing (keep Chinese readable).
        var body = new
        {
            timestamp,
            param,
            sign,
            format,
            method,
            v = version,
        };
        var json = JsonSerializer.Serialize(body, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        string raw;
        try
        {
            using var resp = await _http.PostAsync(baseUrl, content, ct).ConfigureAwait(false);
            raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new YtoApiException($"圓通接口 HTTP {(int)resp.StatusCode}：{Truncate(raw)}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new YtoApiException($"圓通接口請求逾時：{Truncate(baseUrl)}");
        }
        catch (YtoApiException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new YtoApiException($"圓通接口網絡錯誤：{ex.Message}");
        }

        return new YtoApiResult(raw);
    }

    /// <summary>
    /// Downloads a label URL into bytes (when the API responds with an image or PDF URL).
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
            throw new YtoApiException($"圓通面單網址下載失敗：{ex.Message}");
        }
    }

    /// <summary>
    /// Truncates overly long response bodies so error messages do not flood the logs.
    /// </summary>
    private static string Truncate(string s) =>
        s.Length <= 500 ? s : s[..500] + "…（已截斷）";
}