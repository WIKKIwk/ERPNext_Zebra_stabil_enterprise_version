# ZebraBridge v1 portable build

Bu papka **offline ishlaydigan portable paket** yasash uchun.

## Talablar

- bash
- dotnet SDK 8.x (repo ichidagi `.dotnet` avtomatik ishlatiladi)

## Build

```
./build.sh
```

ARM64 ham kerak bo‘lsa:

```
ZEBRA_PORTABLE_ARCHES=all ./build.sh
```

Natija:

- `dist/zebra-bridge/` — enterprise’ga yuborish uchun tayyor papka

Ixtiyoriy:

```
tar -czf dist/zebra-bridge-linux-x64.tar.gz -C dist zebra-bridge
```
