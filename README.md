# ZebraBridge v1

ZebraBridge v1 is a .NET 8 service that writes EPC values to Zebra RFID labels over a local printer connection.
It focuses on a stable encode pipeline first (no UI yet).

## What Works Now

- Single encode: `POST /api/v1/encode`
- Batch encode: `POST /api/v1/encode-batch` (manual items or auto-generate EPCs)
- EPC generator with a persistent counter (file-backed)
- Raw ZPL transceive endpoint (supported only if the transport can read)
- Linux device file output (`/dev/usb/lpX`) and Windows raw printer output

## Current Limits

- ZPL transceive requires a bidirectional transport (not implemented yet)
- Scale reader and ERP agent are placeholders
- No HTML UI in this project

## Configuration

Edit `src/ZebraBridge.Web/appsettings.json` or set environment overrides:

- Printer: `ZEBRA_DEVICE_PATH`, `ZEBRA_PRINTER_NAME`, `ZEBRA_FEED_AFTER_ENCODE`
- EPC generator: `ZEBRA_EPC_PREFIX_HEX`, `ZEBRA_EPC_STATE_PATH`, `ZEBRA_STATE_DIR`
- ZPL line ending: `ZEBRA_ZPL_EOL` (default `\n`)

The EPC generator stores state in:

- `ZEBRA_EPC_STATE_PATH` if set
- otherwise `${XDG_STATE_HOME}/zebra-bridge/epc-generator.json`
- otherwise `~/.local/state/zebra-bridge/epc-generator.json`

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

## Next Phase

- Add a bidirectional USB transport for `transceive`
- Implement scale reader + ERP agent pipelines
- Add automated tests around EPC generation and batch workflows
