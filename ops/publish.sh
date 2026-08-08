#!/bin/sh
# Build the linux-x64 self-contained deployment artifacts for CT 106.
# Output: artifacts/knapper-<version>-linux-x64.tar.gz containing
#   mcp/   — Knapper.Mcp (self-contained)
#   cli/   — knapper (self-contained)
#   ops/   — systemd units + sync-heartbeat.sh
set -eu
cd "$(dirname "$0")/.."

VERSION=$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' Directory.Build.props)
STAGE=artifacts/stage
rm -rf "$STAGE"
mkdir -p "$STAGE/ops"

dotnet publish src/Knapper.Mcp -c Release -r linux-x64 --self-contained -o "$STAGE/mcp"
dotnet publish src/Knapper.Cli -c Release -r linux-x64 --self-contained -o "$STAGE/cli"
cp -R ops/systemd "$STAGE/ops/systemd"
cp ops/sync-heartbeat.sh "$STAGE/ops/"
chmod +x "$STAGE/ops/sync-heartbeat.sh"

mkdir -p artifacts
tar -czf "artifacts/knapper-$VERSION-linux-x64.tar.gz" -C "$STAGE" .
rm -rf "$STAGE"
echo "artifacts/knapper-$VERSION-linux-x64.tar.gz"
