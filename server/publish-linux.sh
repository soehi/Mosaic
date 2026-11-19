#!/usr/bin/env sh
set -eu

ROOT="$(cd "$(dirname "$0")" && pwd)"
OUTPUT="${1:-$ROOT/artifacts/linux-x64}"
VERSION="${2:-}"

rm -rf "$OUTPUT"
dotnet publish "$ROOT/src/Mosaic.Server/Mosaic.Server.csproj" \
    -c Release -r linux-x64 --self-contained true -o "$OUTPUT" \
    ${VERSION:+-p:Version="$VERSION"}

echo "已发布到 $OUTPUT"
