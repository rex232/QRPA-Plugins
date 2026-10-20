# QRPA-Plugins

QApp.QRpa 的第三方活動插件集合（Free RPA tool）。

每個子目錄是一個獨立的外掛類別庫，實作 `QRpa.Core.IActivityPlugin`，於 `Register()` 內以
`ActivityRegistry.RegisterPlugin(...)` 註冊活動。編譯後的 DLL 放入主程式（Robot / Studio）旁的
`plugins` 資料夾即會自動載入。

## 插件一覽

| 目錄 | 插件 ID | 活動 | 用途 |
|---|---|---|---|
| `ContosoWarehousePlugin` | `contoso.warehouse` | `Wh.BarcodeCheck`、`Wh.CalcFreight`、`Wh.PackingSlip` | 倉庫示範插件，同時是第三方開發範本 |
| `DhlExpressPlugin` | `dhl.express` | `Dhl.CreateShipment`、`Dhl.Label`、`Dhl.QueryOrder` | DHL Express 國際寄件（MyDHL API） |
| `EcomOrdersPlugin` | `ecom.orders` | `Eco.PullOrders`、`Eco.OrderById` | 電商訂單來源（Shopify Admin REST API） |
| `JntExpressPlugin` | `jnt.express` | `Jnt.OrderCreate`、`Jnt.QueryOrder`、`Jnt.LabelPrint` | J&T 極兔物流下單、軌跡、面單 |
| `SfExpressPlugin` | `sf.express` | `Sf.OrderCreate`、`Sf.LabelPrint`、`Sf.QueryOrder` | 順豐丰橋開放平台下單、面單、路由 |
| `YtoExpressPlugin` | `yto.express` | `Yto.OrderCreate`、`Yto.LabelPrint`、`Yto.QueryOrder` | 圓通開放平台下單、面單、軌跡 |

所有活動在 Studio 左側的「動作選單」（可搜尋及拖曳活動入流程）中，
統一歸入既有的「物流中心」分類（`Category = "logistics"`）。

## 安裝

1. 以 `dotnet build <插件>.csproj` 編譯（目標框架 `net10.0-windows10.0.19041`）。
2. 把 `<插件>.dll` 複製到QRPA主程式執行檔旁的 `plugins` 資料夾。
3. Robot 亦可用 `--pluginDir <資料夾>` 指定插件目錄；Robot / Studio 啟動時會自動掃描。

移除插件只需刪除對應 DLL。

### 編譯前置

`QRpa.Core.dll` 已隨 repo 放在 `lib\QRpa.Core.dll`，並由根目錄的 `Directory.Build.props`
統一向所有插件提供引用。Clone 後即可直接編譯，無需下載主程式專案或另行安裝 SDK。

- 引用設定了 `Private="false"`：此 DLL 僅供編譯期使用，不會複製到 `plugins`；
  執行時使用主程式（Robot / Studio）自帶的同一組件，避免重複載入。
- 請保持 `lib\QRpa.Core.dll` 與目標主程式版本一致。升級主程式後，以相同版本的
  `QRpa.Core.dll` 覆蓋此檔，再重新編譯插件。
- 輸出目錄為 repo 根目錄的 `plugins\`（已加入 `.gitignore`，不會提交建置產物）。

## 憑證與安全

插件不會把明文密鑰寫入或記錄到流程檔（`.flow.json`）。請把密鑰存入 QRpa CredentialStore，
在流程參數以 `{secret:名稱}` 引用。各插件預設引用名稱：

| 插件 | 密鑰參數 | 預設引用 |
|---|---|---|
| DHL | `clientId` / `clientSecret` | `{secret:dhlClientId}` / `{secret:dhlClientSecret}` |
| J&T | `customerCode` / `secretKey` | `{secret:jntCustomerCode}` / `{secret:jntSecretKey}` |
| 順豐 | `partnerID` / `checkword` | `{secret:sfPartner}` / `{secret:sfCheckword}` |
| 圓通 | `secret` | `{secret:ytoSecret}` |
| Shopify | `accessToken` | 自行以 `{secret:名稱}` 填入 |

通用參數慣例：`resultVar` 指定輸出變數，並會附加子欄位（例如 `{shipment.waybill}`、`{orders.count}`）。

## 各插件說明

### ContosoWarehousePlugin

第三方插件入門範本，純 .NET、無外部依賴，示範三種常見活動型態：

| 活動 | 說明 |
|---|---|
| `Wh.BarcodeCheck` | EAN-13 / UPC-A 校驗碼計算，輸出完整條碼與校驗碼 |
| `Wh.CalcFreight` | 運費估算：`baseFee + weight × ratePerKg`，跨境乘 1.6 |
| `Wh.PackingSlip` | 出貨單寫入 CSV（追加或重建含表頭），支援 `{env.TEMP}` 路徑模板 |

原始碼檔頭附有完整的插件開發流程、Handler 簽名、錯誤處理與容器活動說明，適合作為自訂插件的起點。

### DhlExpressPlugin

對接 DHL MyDHL REST API，OAuth2 `client_credentials` 驗證；Access Token 按 `baseUrl + clientId`
靜態快取，遇到 401 會清除快取並重試一次。

| 活動 | 說明 |
|---|---|
| `Dhl.CreateShipment` | 建立國際貨件（`POST /shipments`），回傳完整 JSON 及 `{resultVar}.waybill` |
| `Dhl.Label` | 取得面單文件並存檔；ZIP 存為 `.zip`，其他存為 `.json` |
| `Dhl.QueryOrder` | 依運單號查詢貨件狀態，輸出 `{resultVar}.status` |

必填：寄件人／收件人資料、`clientId` / `clientSecret`。可選：帳號、產品代碼、重量尺寸、日期。

### EcomOrdersPlugin

以 Shopify Admin REST API（`2024-01`）作為電商訂單來源；Token 以
`X-Shopify-Access-Token` 及 `Authorization: Bearer` 傳送。

| 活動 | 說明 |
|---|---|
| `Eco.PullOrders` | 拉取訂單列表，輸出 JSON 陣列、`{resultVar}.count`，並註冊為 Table 供 `{resultVar.i.欄位}` 讀取 |
| `Eco.OrderById` | 依訂單 ID 取得單一訂單，附運送地址電話 `{resultVar}.phone` |

必填：`shop`、`accessToken`；可選：`status`、`limit`、`extraParams`（合併至查詢字串的 JSON）。

### JntExpressPlugin

對接 J&T 極兔開放平台，請求以 MD5 簽名：`sign = md5HexLower(paramsJson + secretKey)`，
簽名欄位名稱可用 `signerName` 自訂（預設 `sign`）。

| 活動 | 說明 |
|---|---|
| `Jnt.OrderCreate` | 建立運單，回傳 `{resultVar}.waybill` |
| `Jnt.QueryOrder` | 查詢軌跡，輸出最新節點 `{resultVar}.status` / `{resultVar}.desc` |
| `Jnt.LabelPrint` | 取得面單並存檔，支援 Base64、下載網址或純文字 |

必填：`customerCode`、`secretKey`，以及寄件人／收件人資料；`extraParams` 可附加合約專屬欄位。

### SfExpressPlugin

對接順豐丰橋開放平台（`EXP_RECE_CREATE_ORDER`、`COM_RECE_CLOUD_PRINT_WAYBILLS`、
`EXP_RECE_SEARCH_ROUTES`）。簽名為
`base64(md5(urlencode(msgData) + timestamp + checkword))`，表單方式提交，時間戳為 Unix 毫秒。

| 活動 | 說明 |
|---|---|
| `Sf.OrderCreate` | 建立運單，輸出運單號至 `resultVar`，完整回應可寫入 `detailVar` |
| `Sf.LabelPrint` | 雲打印面單，支援 Base64 或下載網址，存為 PDF / 圖片 |
| `Sf.QueryOrder` | 查詢路由，輸出正規化 JSON 陣列及 `statusVar` / `lastTimeVar` / `lastRemarkVar` |

必填：`partnerID`、`checkword`、寄件人／收件人資料；付款方式 `payMethod`：1 寄付、2 到付、3 第三方付。

### YtoExpressPlugin

對接圓通開放平台，統一信封 `{ timestamp, param, sign, format, method, v }`，簽名為
`Base64(MD5(param + method + v + secret))`，HTTPS POST JSON。

| 活動 | 說明 |
|---|---|
| `Yto.OrderCreate` | 建立運單，輸出運單號至 `resultVar`，完整回應可寫入 `detailVar` |
| `Yto.LabelPrint` | 取得面單，支援 Base64、下載網址或 HTML |
| `Yto.QueryOrder` | 查詢軌跡，輸出 `{time, city, info, desc}` 陣列，並把狀態代碼轉為中文摘要 |

必填：`secret`、`method`、`v`、`baseUrl`（由圓通控制台提供）、寄件人／收件人資料。

## 測試

所有插件活動的 `baseUrl` 均可指向本機假伺服器或官方沙箱，無需真實網絡即可端到端執行流程。
插件亦允許注入自訂 `HttpClient`（假 MessageHandler）作單元測試；測試用假密鑰同樣可用，
因為簽名與傳輸使用同一份參數字串，伺服器可依同一規則重算驗證。

## 授權

J&T、順豐、圓通插件檔頭標示 MIT License（Copyright © 2026 Queeny App Ltd.）。
各插件均為 Queeny App 獨立開發，並非由對應物流商提供、認可或贊助；所有商標屬其各自所有者。
DHL、Shopify／Ecom、Contoso 示範插件目前檔頭未標示授權條款。
