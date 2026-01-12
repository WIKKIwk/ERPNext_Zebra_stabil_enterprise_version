# ZebraBridge v1

ZebraBridge v1 is a .NET 8 service that writes EPC values to Zebra RFID labels over a local printer connection.
It focuses on a stable encode pipeline with a lightweight local API.

## What Works Now

- Single encode: `POST /api/v1/encode`
- Batch encode: `POST /api/v1/encode-batch` (manual items or auto-generate EPCs)
- EPC generator with a persistent counter (file-backed)
- Scale reader (serial): `GET /api/v1/scale` + `GET /api/v1/scale/ports`
- ERP agent (poll/register/reply) for ERP-driven commands
- Raw ZPL transceive endpoint (supported only if the transport can read)
- Linux device file output (`/dev/usb/lpX`) and Windows raw printer output

## Current Limits

- ZPL transceive requires `ZEBRA_TRANSPORT=usb` and libusb support
- ERP agent needs proper URL/auth configuration before it starts
- No web UI is shipped (API-only service)

## Configuration

Edit `src/ZebraBridge.Web/appsettings.json` or set environment overrides:

- Printer: `ZEBRA_DEVICE_PATH`, `ZEBRA_PRINTER_NAME`, `ZEBRA_FEED_AFTER_ENCODE`, `ZEBRA_RFID_ZPL_TEMPLATE`, `ZEBRA_RFID_ZPL_TEMPLATE_PATH`, `ZEBRA_TRANSPORT` (`device` or `usb`)
- EPC generator: `ZEBRA_EPC_PREFIX_HEX`, `ZEBRA_EPC_STATE_PATH`, `ZEBRA_STATE_DIR`
- ZPL line ending: `ZEBRA_ZPL_EOL` (default `\n`)
- ERP agent: `ZEBRA_ERP_URL`, `ZEBRA_ERP_AUTH`, `ZEBRA_ERP_AGENT_ID`, `ZEBRA_ERP_DEVICE`
- Multi-target ERP: `ZEBRA_ERP_TARGETS_JSON` or `ZEBRA_ERP_URL_LOCAL`/`ZEBRA_ERP_AUTH_LOCAL`, `ZEBRA_ERP_URL_SERVER`/`ZEBRA_ERP_AUTH_SERVER`
- ERP config file: `ZEBRA_ERP_CONFIG_PATH` (defaults to `~/.local/state/zebra-bridge/erp-config.json`)
- API auth (recommended in production): `ZEBRA_API_TOKEN` or `ZEBRA_API_AUTH`
- HTTP timeouts: `ZEBRA_HTTP_TIMEOUT_MS`, `ZEBRA_HTTP_CONNECT_TIMEOUT_MS`
- TUI tuning: `ZEBRA_TUI_HTTP_TIMEOUT_MS`, `ZEBRA_TUI_CONNECT_TIMEOUT_MS`, `ZEBRA_TUI_SCALE_MS`, `ZEBRA_TUI_REFRESH_MS`

The EPC generator stores state in:

- `ZEBRA_EPC_STATE_PATH` if set
- otherwise `${XDG_STATE_HOME}/zebra-bridge/epc-generator.json`
- otherwise `~/.local/state/zebra-bridge/epc-generator.json`

## Run

On Linux, run:

```
./run.sh
```

The script downloads .NET 8 locally into `.dotnet/` if `dotnet` is not already installed
(requires `curl` or `wget`).

API base URL: `http://127.0.0.1:18000`.

CLI (terminal):

```
./cli.sh encode --epc 3034257BF7194E4000000001 --copies 2
./cli.sh encode-batch --items 3034AA:1,3034BB:2
./cli.sh transceive --zpl "^XA^HH^XZ"
./cli.sh printer resume
./cli.sh setup --online --erp-url http://127.0.0.1:8000 --erp-token api_key:api_secret --device ZEBRA-01
```

Terminal TUI (clean screen, no log spam):

```
./run.sh --tui
```

This starts the web service in the background and opens a clean terminal dashboard.
On startup it shows an ONLINE/OFFLINE selector. If you choose ONLINE it will ask for:

- ERP URL (where to send data)
- ERP token (api_key:api_secret or token ...)
- Local device name (agent identity)

The answers are saved to `~/.local/state/zebra-bridge/erp-config.json` (or `ZEBRA_ERP_CONFIG_PATH`)
and the ERP UI (`rfidenter`) can control the device after that.

## Example Calls

Encode a single EPC:

```
curl -X POST http://127.0.0.1:18000/api/v1/encode \
  -H "Content-Type: application/json" \
  -d '{"epc":"3034257BF7194E4000000001","copies":1}'
```

Batch encode (manual):

```
curl -X POST http://127.0.0.1:18000/api/v1/encode-batch \
  -H "Content-Type: application/json" \
  -d '{"mode":"manual","items":[{"epc":"3034257BF7194E4000000001","copies":2}]}'
```

Batch encode (auto):

```
curl -X POST http://127.0.0.1:18000/api/v1/encode-batch \
  -H "Content-Type: application/json" \
  -d '{"mode":"auto","autoCount":5}'
```

Transceive raw ZPL (only if transport supports reads):

```
curl -X POST http://127.0.0.1:18000/api/v1/transceive \
  -H "Content-Type: application/json" \
  -d '{"zpl":"^XA^HH^XZ","readTimeoutMs":2000,"maxBytes":32768}'
```

To enable USB transceive, set `ZEBRA_TRANSPORT=usb` and ensure libusb is available.

Resume/clear printer:

```
curl -X POST http://127.0.0.1:18000/api/v1/printer/resume
curl -X POST http://127.0.0.1:18000/api/v1/printer/reset
```

## Next Phase

- Add a bidirectional USB transport for `transceive`
- Implement scale reader + ERP agent pipelines
- Add automated tests around EPC generation and batch workflows
