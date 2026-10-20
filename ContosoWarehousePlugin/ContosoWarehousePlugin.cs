using System.Globalization;
using QRpa.Core;

namespace ContosoWarehousePlugin;

// ═══════════════════════════════════════════════════════════════════════════════
//  第三方插件入門指南（Third-party Plugin Beginner's Guide）
//  ═══════════════════════════════════════════════════════════════════════════════
//  想為 QRpa 加自定活動？跟住呢個流程：
//  Want to add a custom activity to QRpa? Follow this flow:
//
//   1. 新增一個類別庫（.NET）專案，並引用 QRpa.Core。
//      Create a class-library (.NET) project and reference QRpa.Core.
//        <ProjectReference Include="..\..\src\QRpa.Core\QRpa.Core.csproj" />
//
//   2. 實作 IActivityPlugin 介面（本檔案 WarehouseActivityPlugin 示範）。
//      Implement the IActivityPlugin interface (WarehouseActivityPlugin below demonstrates it).
//
//   3. 在 Register() 內呼叫 ActivityRegistry.RegisterPlugin(...)，逐個註冊活動。
//      Inside Register(), call ActivityRegistry.RegisterPlugin(...) once per activity.
//
//   4. 編譯後，把 DLL 放進主程式執行檔旁的 plugins 資料夾（或指定 plugins 目錄）。
//      After compiling, drop the DLL into the plugins folder next to the host executable (or point at a plugins directory).
//      ── Robot：  --pluginDir <資料夾>；唔指定就載入執行檔旁邊的 plugins 資料夾
//      ── Robot：  --pluginDir <folder>; defaults to the plugins folder beside the executable
//      ── Studio：兩個 Studio（WinForms / WPF）啟動時自動掃描執行檔旁的 plugins 資料夾
//      ── Studio: both Studios (WinForms / WPF) auto-scan the plugins folder beside the executable on startup
//
//   慣例同規則（Conventions and rules）：
//   a) DLL 檔名必須包含 "Plugin"（例如 ContosoWarehousePlugin.dll），PluginHost 先會掃描佢。
//      The DLL filename MUST contain "Plugin" (e.g. ContosoWarehousePlugin.dll) for PluginHost to scan it.
//   b) 活動名稱（PluginDescriptor.Name）要全局唯一（大小寫不敏感），撞名註冊會拋錯。
//      The activity name (PluginDescriptor.Name) must be globally unique (case-insensitive); a collision throws.
//   c) 單一插件註冊失敗只會寫入日誌，唔會令 Robot/Studio 崩潰（PluginHost 已內置隔離）。
//      A single plugin failing to register is only logged and never crashes Robot/Studio (PluginHost isolates failures).
//   d) 插件 Handler 與內建活動完全同一個簽名：可以 ctx.Expand 模板、ctx.Set 寫變數、回傳子分支畀引擎執行。
//      The plugin Handler uses exactly the same signature as built-in activities: ctx.Expand templates, ctx.Set variables,
//      and it may return child branches for the engine to execute.
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Contoso 倉庫示範插件：展示第三方如何實作 <see cref="IActivityPlugin"/> 並向 QRpa 註冊活動。
/// Contoso warehouse demo plugin: shows how a third party implements <see cref="IActivityPlugin"/> and registers activities with QRpa.
///
/// 接入三步曲（Three steps to plug in）：
///   1. 本類別實作 <see cref="IActivityPlugin"/>；此介面由 QRpa.Core 提供，唔需要任何外掛框架或 NuGet 依賴。
///      This class implements <see cref="IActivityPlugin"/>; the interface comes from QRpa.Core — no plugin framework or NuGet dependency is required.
///   2. Register() 內逐個用 <see cref="ActivityRegistry.RegisterPlugin(PluginDescriptor)"/> 註冊活動
///      （名稱即流程 JSON 的 kind；Handler 簽名與內建活動完全一致，支援 {var} 模板、ctx.Set 變數、回傳子分支）。
///      Register() registers each activity with <see cref="ActivityRegistry.RegisterPlugin(PluginDescriptor)"/>
///      (the name is the kind in the workflow JSON; the Handler signature matches built-in activities exactly, supporting {var} templates, ctx.Set variables, and returned child branches).
///   3. build 後把 ContosoWarehousePlugin.dll 放入主程式旁的 plugins 資料夾，Robot 與 Studio 啟動時自動掃描載入。
///      After building, drop ContosoWarehousePlugin.dll into the plugins folder next to the host; Robot and Studio auto-scan and load it on startup.
///
/// 插件介面三成員（The three interface members）：
///   - <see cref="IActivityPlugin.Id"/>          ：插件唯一識別碼（命名慣例：廠商.領域，如 "contoso.warehouse"）。
///       A globally unique plugin identifier (naming convention: vendor.domain, e.g. "contoso.warehouse").
///   - <see cref="IActivityPlugin.Description"/> ：插件簡介（顯示用）。
///       A short plugin description (for display).
///   - <see cref="IActivityPlugin.Register"/>    ：註冊入口。主機啟動時會 new 出本類別並呼叫呢個方法一次；
///       你要喺入面為每個活動呼叫 ActivityRegistry.RegisterPlugin(...)。
///       The registration entry point. The host instantiates this class and calls this method once at startup;
///       you call ActivityRegistry.RegisterPlugin(...) inside for every activity.
///
/// 進階：容器活動（Advanced: container activities）。
/// 如果活動想收子節點（好似內建 Sequence / If / ForEach 咁），可以：
/// If an activity should hold child nodes (like the built-in Sequence / If / ForEach), you can:
///   - PluginDescriptor.IsContainer = true                                     → Studio 容許喺流程樹加子節點
///   - 返回子分支（return node.Children on success）                            → 引擎會自動接住執行
///   - the Studio allows adding child nodes in the flow tree
///   - returning the branch (return children) when the handler succeeds       → the engine executes them
/// </summary>
public sealed class WarehouseActivityPlugin : IActivityPlugin
{
    /// <summary>
    /// 插件唯一識別碼。用喺日誌同錯誤訊息，方便追蹤插件來源。
    /// Globally unique plugin identifier, used in logs and error messages to trace the plugin's origin.
    /// </summary>
    public string Id => "contoso.warehouse";

    /// <summary>
    /// 插件簡介，未來可喺 Studio 或管理工具度顯示。
    /// Short plugin description, shown in Studio or management tools in the future.
    /// </summary>
    public string Description => "物流中心示範活動：條碼校驗碼、出貨單寫入、運費估算（純 .NET，無外部依賴）。";

    /// <summary>
    /// 註冊本插件嘅全部活動（啟動時由 PluginHost 或主機自行呼叫一次，唔好喺流程執行期間重複註冊）。
    /// Registers all activities of this plugin (called once at startup by PluginHost or the host; do not re-register during a workflow run).
    ///
    /// PluginDescriptor 各欄位嘅作用（What each PluginDescriptor field means）：
    ///   Name     ：唯一活動名，同時係「流程 JSON 的 kind 值」。寫流程時 kind 直接填呢個字串（例如 "Wh.BarcodeCheck"），
    ///               引擎執行時會搵返呢度註冊嘅 Handler 嚟跑。
    ///              The unique activity name and the "kind value" in the workflow JSON. In a flow, set kind to this string
    ///              (e.g. "Wh.BarcodeCheck"); the engine resolves the Handler registered here at run time.
    ///   Title    ：動作選單（Studio 左邊活動面板）顯示嘅名字；插件自帶文字，唔需要 resx。
    ///              The display name in the action menu (Studio's activity panel on the left); the plugin provides its own text, no resx needed.
    ///   Category ：動作選單分類 key。用內建 resx key（例如 "logistics"）就可以放進已有分類；
    ///              用自訂 key（例如 "uplink"）會自動開新分類，冇 resx 時直接顯示 key 文字。
    ///              The action menu category key. Use a built-in resx key (e.g. "logistics") to join an existing category;
    ///              a custom key (e.g. "uplink") creates a new category and displays the key text when no resx exists.
    ///   Hint     ：參數提示（tooltip / 搜尋說明），寫清楚每個參數名同用途對使用者最有幫助。
    ///              Parameter hint (tooltip / search help); documenting every argument name and purpose is most helpful to users.
    ///   Defaults ：使用者由動作選單拖入流程設計器時，預先填好嘅參數（args）。好嘢：路徑可以用 {env.TEMP} 等模板。
    ///              Default arguments pre-filled when the user drags the activity into the flow designer. Templates like {env.TEMP} work here too.
    ///   Handler  ：實際執行邏輯，簽名為 (node, ctx, ct) → 詳見每個 Handler 嘅註解。
    ///              The actual execution logic with signature (node, ctx, ct) → see the comments on each Handler.
    /// </summary>
    public void Register()
    {
        // ── 活動 1：純計算型插件（計算完寫變數）──────────────────────────────
        // ── Activity 1: a pure-computation plugin (computes and writes variables) ──
        ActivityRegistry.RegisterPlugin(new PluginDescriptor
        {
            Name = "Wh.BarcodeCheck",
            Title = "條碼校驗碼（插件）",
            Category = "logistics",                       // 對應 resx Cat.logistics → 動作選單顯示「物流中心」
                                                          // Maps to the resx key Cat.logistics → the action menu shows 「物流中心」
            Hint = "code=前12位數字 | resultVar=完整條碼 | checkVar=校驗碼(可空)",
            Defaults = new Dictionary<string, string>
            {
                ["code"] = "590123412345",
                ["resultVar"] = "barcode",
            },
            Handler = BarcodeCheck,
        });

        // ── 活動 2：計算型插件（讀數字參數，輸出格式化結果）──────────────────
        // ── Activity 2: a computation plugin (reads numeric arguments, outputs a formatted result) ──
        ActivityRegistry.RegisterPlugin(new PluginDescriptor
        {
            Name = "Wh.CalcFreight",
            Title = "運費估算（插件）",
            Category = "logistics",
            Hint = "weight=公斤 | zone=local|cross | baseFee=起步價 | ratePerKg=每公斤單價 | resultVar=總運費",
            Defaults = new Dictionary<string, string>
            {
                ["weight"] = "10",
                ["zone"] = "local",
                ["baseFee"] = "20",
                ["ratePerKg"] = "2.5",
                ["resultVar"] = "freight",
            },
            Handler = CalcFreight,
        });

        // ── 活動 3：檔案型插件（寫盤 + 路徑模板）──────────────────────────────
        // ── Activity 3: a file-based plugin (writes to disk + path templates) ──
        ActivityRegistry.RegisterPlugin(new PluginDescriptor
        {
            Name = "Wh.PackingSlip",
            Title = "出貨單寫入（插件）",
            Category = "logistics",
            Hint = "output=csv路徑 | orderId/sku/qty | append=true|false | resultVar=寫入嘅行",
            Defaults = new Dictionary<string, string>
            {
                ["output"] = "{env.TEMP}\\packing-slip.csv",
                ["orderId"] = "SO-1001",
                ["sku"] = "WH-001",
                ["qty"] = "1",
                ["append"] = "true",
                ["resultVar"] = "slipLine",
            },
            Handler = PackingSlip,
        });
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    //  Handler 簽名（The Handler signature）
    //  ═══════════════════════════════════════════════════════════════════════════════
    //  ActivityHandler : Task<IReadOnlyList<ActivityNode>?> Handler(ActivityNode node, WorkflowContext ctx, CancellationToken ct)
    //
    //  node：    當前節點。所有參數都以「字串」存放喺 node.Args（流程 JSON 的 args），
    //            dict 大小寫不敏感；用 ctx.GetStr(node, "參數名", 預設值) 讀取。
    //  node:     the current node. All arguments are stored as strings in node.Args (the args object of the workflow JSON);
    //            the dictionary is case-insensitive; read them with ctx.GetStr(node, "argName", defaultValue).
    //  ctx：     執行期上下文。ctx.Expand() 會替換 {var} / {env.VAR} / {now} / {secret:名稱} / {table.i.col} 模板；
    //            ctx.Set(name, value) 寫變數——後續 Log、SetVariable 或插件都可以讀返。
    //  ctx:      the runtime context. ctx.Expand() substitutes {var} / {env.VAR} / {now} / {secret:name} / {table.i.col} templates;
    //            ctx.Set(name, value) writes a variable — later Log, SetVariable or other plugins can read it back.
    //  ct：      CancellationToken。長任務（等待檔案、迴圈）必須監聽佢，使用者按「停止」時立即中止。
    //  ct:       a CancellationToken. Long-running work (waiting for files, loops) must honor it so an immediate stop works.
    //
    //  傳回值（Return value）：
    //    返回 IReadOnlyList&lt;ActivityNode&gt;？＝回傳子分支畀引擎接住執行（容器活動用）；
    //    Returning a non-null list means "run these child branches next" (used by container activities);
    //    返回 null ＝本節點完成，流程繼續下一個節點。非 async Handler 一定要 Task.FromResult(...)，唔可以直接 return null。
    //    Returning null means "this node is done, move on". Non-async handlers MUST return Task.FromResult(...); never return null directly.
    //
    //  錯誤處理（Error handling）：
    //    參數違規（例如條碼格式錯）要用 throw new WorkflowException(node.Id, "用戶可讀訊息")，
    //    咁樣失敗時日誌會清清楚楚顯示係邊個節點、錯咩；唔好靜靜地 return 或者死機。
    //    Argument violations (e.g. a malformed barcode) must throw new WorkflowException(node.Id, "user-readable message"),
    //    so failure logs clearly identify the node and the reason; never swallow the error or crash.
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// EAN-13／UPC-A 校驗碼計算（入庫貼標前檢查條碼）。
    /// EAN-13 / UPC-A check-digit computation (verify a barcode before shelf labeling).
    ///
    /// 示範重點（What this handler demonstrates）：
    ///   1. ctx.GetStr(node, "code") 讀參數（會先 Expand 模板，所以參數可以係 {order.code} 呢啲變數）；
    ///      ctx.GetStr reads an argument (templates are expanded first, so the value may be something like {order.code}).
    ///   2. 參數唔啱規格就 throw WorkflowException，畀使用者清晰錯誤。
    ///      Throws WorkflowException when the argument violates the spec, giving the user a clear error.
    ///   3. ctx.Set(...) 輸出變數：resultVar 係「寫去邊個變數」嘅參數名——第三方活動慣例。
    ///      ctx.Set(...) writes the output: resultVar names the target variable — the standard convention for third-party activities.
    ///
    /// 演算法：由左至右奇數位 ×3、偶數位 ×1，校驗位補足到 10 的倍數。
    /// Algorithm: odd positions ×3, even ×1 left to right; the check digit tops the sum up to a multiple of 10.
    /// </summary>
    private static Task<IReadOnlyList<ActivityNode>?> BarcodeCheck(ActivityNode node, WorkflowContext ctx, CancellationToken ct)
    {
        // 讀參數：user 填 "590123412345" 或 "{gen.barcode}"，GetStr 自動 Expand。
        // Read the argument: the user may fill in "590123412345" or "{gen.barcode}"; GetStr expands it automatically.
        var code = ctx.GetStr(node, "code").Trim();

        // 輸入驗證：11 位（UPC-A）或 12 位（EAN-13）全數字。唔啱就拋出可讀錯誤。
        // Input validation: 11 digits (UPC-A) or 12 digits (EAN-13), all numeric. Throw a readable error otherwise.
        if (code.Length is not (11 or 12) || !code.All(char.IsDigit))
            throw new WorkflowException(node.Id, $"條碼校驗碼：需要 11（UPC-A）或 12（EAN-13）位數字，收到「{code}」。");

        // 核心計算（check digit）
        var sum = 0;
        for (var i = 0; i < code.Length; i++)
            sum += (code[i] - '0') * (i % 2 == 0 ? 3 : 1);
        var check = (10 - sum % 10) % 10;
        var full = code + check;                       // 完整 EAN-13 = 12 位 + 1 位校驗碼 / full EAN-13 = 12 digits + 1 check digit

        // 輸出：resultVar 指定嘅變數寫完整條碼；checkVar（可選）寫校驗碼。
        // Output: write the full barcode to the variable named by resultVar; optionally write the check digit to checkVar.
        ctx.Set(ctx.GetStr(node, "resultVar"), full);
        var checkVar = ctx.GetStr(node, "checkVar");
        if (checkVar.Length > 0) ctx.Set(checkVar, check.ToString());

        return Task.FromResult<IReadOnlyList<ActivityNode>?>(null);
    }

    /// <summary>
    /// 運費估算：baseFee + weight × ratePerKg，跨境（cross）再乘 1.6。
    /// Freight estimate: baseFee + weight × ratePerKg; cross-border (cross) is multiplied by 1.6.
    ///
    /// 示範重點（What this handler demonstrates）：
    ///   1. 數字參數解析：GetStr 攞返嘅係字串（因為 JSON args 一律字串），用 TryNum 轉 double；
    ///      Numeric arguments come back as strings (JSON args are all strings), so TryNum converts them.
    ///   2. "0.##" 格式化輸出，避免 45.00000000000001 呢啲浮點尾巴令使用者睇到眼花。
    ///      "0.##" formats the output, avoiding float tails like 45.00000000000001 that confuse users.
    /// </summary>
    private static Task<IReadOnlyList<ActivityNode>?> CalcFreight(ActivityNode node, WorkflowContext ctx, CancellationToken ct)
    {
        var weight = TryNum(ctx.GetStr(node, "weight"));
        var baseFee = TryNum(ctx.GetStr(node, "baseFee"));
        var rate = TryNum(ctx.GetStr(node, "ratePerKg"));
        var cross = ctx.GetStr(node, "zone", "local").Equals("cross", StringComparison.OrdinalIgnoreCase);
        var fee = baseFee + weight * rate * (cross ? 1.6 : 1.0);

        ctx.Set(ctx.GetStr(node, "resultVar"), fee.ToString("0.##"));

        return Task.FromResult<IReadOnlyList<ActivityNode>?>(null);
    }

    /// <summary>
    /// 出貨單寫入（CSV 一行，追加或重建帶表頭）；展示檔案型插件活動同 {env.TEMP} 路徑模板。
    /// Packing-slip append (one CSV line, append or rebuild with a header); demonstrates file-based plugin activities and the {env.TEMP} path template.
    ///
    /// 示範重點（What this handler demonstrates）：
    ///   1. 路徑參數支援模板：Defaults 度寫咗 "{env.TEMP}\\packing-slip.csv"，真正執行時會換成實際 TEMP 目錄。
    ///      Path arguments support templates: the Defaults use "{env.TEMP}\\packing-slip.csv", which expands to the real TEMP directory at run time.
    ///   2. File.Exists / Directory.CreateDirectory 保證寫入安全（目錄未建就建）。
    ///      File.Exists / Directory.CreateDirectory make the write robust (create the directory if missing).
    ///   3. append 參數控制「追加」定「重建」，重建時寫埋表頭，令 CSV 一開檔就知每一欄係咩。
    ///      The append flag controls append-vs-recreate; recreation writes a header so columns are obvious on first glance.
    /// </summary>
    private static Task<IReadOnlyList<ActivityNode>?> PackingSlip(ActivityNode node, WorkflowContext ctx, CancellationToken ct)
    {
        var path = ctx.GetStr(node, "output");
        if (path.Length == 0)
            throw new WorkflowException(node.Id, "出貨單：output 路徑未填。");

        // 組裝一行：timestamp,orderId,sku,qty
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss},{ctx.GetStr(node, "orderId")},{ctx.GetStr(node, "sku")},{ctx.GetStr(node, "qty")}";
        var append = ctx.GetStr(node, "append", "true").Equals("true", StringComparison.OrdinalIgnoreCase);

        var dir = Path.GetDirectoryName(path);
        if (dir.Length > 0) Directory.CreateDirectory(dir);
        if (append && File.Exists(path))
            File.AppendAllText(path, line + Environment.NewLine);
        else
            File.WriteAllText(path, "timestamp,orderId,sku,qty" + Environment.NewLine + line + Environment.NewLine);

        // 順便話畀之後嘅活動知寫咗咩（resultVar 慣例同上）
        // Also tell later activities what was written (the resultVar convention as above)
        var resultVar = ctx.GetStr(node, "resultVar");
        if (resultVar.Length > 0) ctx.Set(resultVar, line);

        return Task.FromResult<IReadOnlyList<ActivityNode>?>(null);
    }

    /// <summary>
    /// 把字串安全咁轉成 double：JSON args 全部係字串，呢個工具函式畀要用數值嘅 Handler 用。
    /// Safely converts a string to double: JSON args are all strings, and this helper serves handlers that need numeric values.
    /// 用 InvariantCulture 解析，避免地區用小數點/逗號嘅差異（例如 "2.5" 喺歐洲會讀唔到）。
    /// Parsing uses InvariantCulture to avoid locale differences in decimal separators (e.g. "2.5" fails in European locales).
    /// </summary>
    private static double TryNum(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
}