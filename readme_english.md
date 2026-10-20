# QRPA-Plugins

Third-party activity plugins for QRPA.

QRPA - https://www.qrpaapp.com

Each subfolder is a standalone plugin class library. It implements
`QRpa.Core.IActivityPlugin` and registers activities through
`ActivityRegistry.RegisterPlugin(...)` inside `Register()`. After building, drop the DLL into the
`plugins` folder next to the host application (Robot / Studio) and it is loaded automatically.

## Plugin overview

| Folder | Plugin ID | Activities | Purpose |
|---|---|---|---|
| `ContosoWarehousePlugin` | `contoso.warehouse` | `Wh.BarcodeCheck`, `Wh.CalcFreight`, `Wh.PackingSlip` | Warehouse demo plugin, also a third-party development template |
| `DhlExpressPlugin` | `dhl.express` | `Dhl.CreateShipment`, `Dhl.Label`, `Dhl.QueryOrder` | DHL Express international shipping (MyDHL API) |
| `EcomOrdersPlugin` | `ecom.orders` | `Eco.PullOrders`, `Eco.OrderById` | E-commerce order source (Shopify Admin REST API) |
| `JntExpressPlugin` | `jnt.express` | `Jnt.OrderCreate`, `Jnt.QueryOrder`, `Jnt.LabelPrint` | J&T Express order create, tracking, label |
| `SfExpressPlugin` | `sf.express` | `Sf.OrderCreate`, `Sf.LabelPrint`, `Sf.QueryOrder` | SF Express (Fengqiao) order create, label, routing |
| `YtoExpressPlugin` | `yto.express` | `Yto.OrderCreate`, `Yto.LabelPrint`, `Yto.QueryOrder` | YTO Express order create, label, tracking |

All activities appear in Studio's "Action Menu" on the left (searchable and draggable into a flow)
under the existing "Logistics Center" category (`Category = "logistics"`).

## Installation

1. Build with `dotnet build <plugin>.csproj` (target framework `net10.0-windows10.0.19041`).
2. Copy `<plugin>.dll` into the `plugins` folder next to the QRPA host executable.
3. Robot also accepts `--pluginDir <folder>` to point at a plugin directory; Robot / Studio scan
   automatically on startup.

To uninstall, simply delete the DLL.

### Build prerequisites

`QRpa.Core.dll` is included in this repo at `lib\QRpa.Core.dll`, and the root
`Directory.Build.props` provides it as a reference to every plugin. After cloning, you can build
directly; no need to download the main project or install an SDK.

- The reference uses `Private="false"`: the DLL is for compile time only and is not copied into
  `plugins`. At run time the host (Robot / Studio) supplies the same assembly, avoiding double loading.
- Keep `lib\QRpa.Core.dll` in sync with the target host version. After upgrading the host, overwrite
  this file with the matching `QRpa.Core.dll` and rebuild the plugins.
- Build output goes to `plugins\` in the repo root (already covered by `.gitignore`; build artifacts
  are not committed).

## Credentials and security

Plugins never write or log plaintext secrets into flow files (`.flow.json`). Store secrets in the
QRpa CredentialStore and reference them in flow arguments as `{secret:name}`. Default reference names
per plugin:

| Plugin | Secret arguments | Default reference |
|---|---|---|
| DHL | `clientId` / `clientSecret` | `{secret:dhlClientId}` / `{secret:dhlClientSecret}` |
| J&T | `customerCode` / `secretKey` | `{secret:jntCustomerCode}` / `{secret:jntSecretKey}` |
| SF Express | `partnerID` / `checkword` | `{secret:sfPartner}` / `{secret:sfCheckword}` |
| YTO Express | `secret` | `{secret:ytoSecret}` |
| Shopify | `accessToken` | Fill in your own `{secret:name}` |

Common argument convention: `resultVar` names the output variable and also receives sub-fields
(for example `{shipment.waybill}`, `{orders.count}`).

## Plugin details

### ContosoWarehousePlugin

A beginner template for third-party plugins. Pure .NET, no external dependencies. It demonstrates
three common activity types:

| Activity | Description |
|---|---|
| `Wh.BarcodeCheck` | EAN-13 / UPC-A check-digit calculation; outputs the full barcode and the check digit |
| `Wh.CalcFreight` | Freight estimate: `baseFee + weight × ratePerKg`; cross-border is multiplied by 1.6 |
| `Wh.PackingSlip` | Writes a packing-slip CSV line (append, or rebuild with a header); supports the `{env.TEMP}` path template |

The source header documents the full plugin workflow, handler signature, error handling, and
container activities. It is a good starting point for custom plugins.

### DhlExpressPlugin

Connects to the DHL MyDHL REST API with OAuth2 `client_credentials` authentication. The access token
is cached statically per `baseUrl + clientId`; a 401 clears the cache and retries once.

| Activity | Description |
|---|---|
| `Dhl.CreateShipment` | Creates an international shipment (`POST /shipments`); returns the full JSON and `{resultVar}.waybill` |
| `Dhl.Label` | Fetches the label document and saves it; ZIP is saved as `.zip`, anything else as `.json` |
| `Dhl.QueryOrder` | Queries shipment status by waybill number; outputs `{resultVar}.status` |

Required: shipper / receiver details, `clientId` / `clientSecret`. Optional: account number,
product code, weight and dimensions, date.

### EcomOrdersPlugin

Uses the Shopify Admin REST API (`2024-01`) as an e-commerce order source. The token is sent as both
`X-Shopify-Access-Token` and `Authorization: Bearer`.

| Activity | Description |
|---|---|
| `Eco.PullOrders` | Pulls an order list; outputs a JSON array and `{resultVar}.count`, and registers it as a Table for `{resultVar.i.column}` access |
| `Eco.OrderById` | Gets one order by ID; also exposes the shipping address phone as `{resultVar}.phone` |

Required: `shop`, `accessToken`. Optional: `status`, `limit`, `extraParams` (a JSON object merged into
the query string).

### JntExpressPlugin

Connects to the J&T Express open platform. Requests are MD5-signed:
`sign = md5HexLower(paramsJson + secretKey)`. The signature field name is configurable via
`signerName` (default `sign`).

| Activity | Description |
|---|---|
| `Jnt.OrderCreate` | Creates a waybill; returns `{resultVar}.waybill` |
| `Jnt.QueryOrder` | Queries tracking; outputs the latest milestone as `{resultVar}.status` / `{resultVar}.desc` |
| `Jnt.LabelPrint` | Fetches the label and saves it; supports Base64, a download URL, or plain text |

Required: `customerCode`, `secretKey`, and shipper / receiver details. `extraParams` can carry
contract-specific fields.

### SfExpressPlugin

Connects to the SF Express Fengqiao open platform (`EXP_RECE_CREATE_ORDER`,
`COM_RECE_CLOUD_PRINT_WAYBILLS`, `EXP_RECE_SEARCH_ROUTES`). The signature is
`base64(md5(urlencode(msgData) + timestamp + checkword))`, posted as a form; the timestamp is Unix
milliseconds.

| Activity | Description |
|---|---|
| `Sf.OrderCreate` | Creates a waybill; writes the waybill number to `resultVar`; the full response can go to `detailVar` |
| `Sf.LabelPrint` | Cloud-print label; supports Base64 or a download URL; saved as PDF / image |
| `Sf.QueryOrder` | Queries routing; outputs a normalized JSON array plus `statusVar` / `lastTimeVar` / `lastRemarkVar` |

Required: `partnerID`, `checkword`, and shipper / receiver details. Payment method `payMethod`:
1 = shipper pays, 2 = receiver pays, 3 = third-party pays.

### YtoExpressPlugin

Connects to the YTO Express open platform using the unified envelope
`{ timestamp, param, sign, format, method, v }`. The signature is
`Base64(MD5(param + method + v + secret))`, sent as HTTPS POST JSON.

| Activity | Description |
|---|---|
| `Yto.OrderCreate` | Creates a waybill; writes the waybill number to `resultVar`; the full response can go to `detailVar` |
| `Yto.LabelPrint` | Fetches the label; supports Base64, a download URL, or HTML |
| `Yto.QueryOrder` | Queries tracking; outputs `{time, city, info, desc}` entries and translates status codes into a Chinese summary |

Required: `secret`, `method`, `v`, `baseUrl` (provided by the YTO console), and shipper / receiver
details.

## Testing

Every plugin activity accepts a `baseUrl` argument that can point to a local fake server or the
official sandbox, so a flow can run end to end without a live network. Plugins also allow injecting a
custom `HttpClient` (fake message handler) for unit tests. Fake credentials work too, because signing
and transmission use the same parameter string, so the server can recompute and verify the digest
with the same rule.

## License

The J&T, SF Express, and YTO Express plugin headers state the MIT License
(Copyright © 2026 Queeny App Ltd.). All plugins are independently developed by Queeny App and are
not provided, endorsed, or sponsored by the respective carriers; all trademarks belong to their
respective owners. The DHL, Shopify / Ecom, and Contoso demo plugins do not currently declare a
license in their headers.
