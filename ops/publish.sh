#!/bin/sh
# Build the linux-x64 self-contained deployment artifacts for CT 106.
# Output: artifacts/knapper-<version>-linux-x64.tar.gz containing
#   mcp/   — Knapper.Mcp (self-contained)
#   cli/   — knapper (self-contained)
#   ops/   — systemd units, sync-heartbeat.sh, and the HOST-side monitor kit
set -eu
cd "$(dirname "$0")/.."

VERSION=$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' Directory.Build.props)
# An absent or malformed <Version> would ship "knapper--linux-x64.tar.gz"
# and the manifest gate below would still pass — validate the shape here.
case "$VERSION" in
    [0-9]*.[0-9]*) ;;
    *) echo "publish FAILED: could not extract a valid <Version> from Directory.Build.props (got '$VERSION')" >&2
       exit 1 ;;
esac
[ "$(printf '%s' "$VERSION" | wc -l)" -eq 0 ] || {
    echo "publish FAILED: <Version> extraction produced multiple lines" >&2
    exit 1
}
STAGE=artifacts/stage
rm -rf "$STAGE"
mkdir -p "$STAGE/ops"

dotnet publish src/Knapper.Mcp -c Release -r linux-x64 --self-contained -o "$STAGE/mcp"
dotnet publish src/Knapper.Cli -c Release -r linux-x64 --self-contained -o "$STAGE/cli"
cp -R ops/systemd "$STAGE/ops/systemd"
cp ops/sync-heartbeat.sh "$STAGE/ops/"
chmod +x "$STAGE/ops/sync-heartbeat.sh"
# The Proxmox-host monitor installs FROM THIS ARCHIVE (runbook §8) — a
# tarball without it cannot perform the documented installation.
cp -R ops/monitor "$STAGE/ops/monitor"
chmod +x "$STAGE/ops/monitor/knapper-monitor.sh"

mkdir -p artifacts
TARBALL="artifacts/knapper-$VERSION-linux-x64.tar.gz"
tar -czf "$TARBALL" -C "$STAGE" .
rm -rf "$STAGE"

# Content gate: every path the runbook installs must exist in the archive.
# A missing path fails the PUBLISH, not the deployment.
MANIFEST=$(tar -tzf "$TARBALL")
for required in \
    ./mcp/Knapper.Mcp \
    ./cli/knapper \
    ./ops/sync-heartbeat.sh \
    ./ops/systemd/knapper.service \
    ./ops/systemd/knapper-commit.service \
    ./ops/systemd/knapper-commit.timer \
    ./ops/systemd/knapper-heartbeat.service \
    ./ops/systemd/knapper-heartbeat.timer \
    ./ops/systemd/obsidian-headless.service \
    ./ops/systemd/knapper-smoke.service.example \
    ./ops/monitor/knapper-monitor.sh \
    ./ops/monitor/knapper-monitor.conf.example \
    ./ops/monitor/knapper-monitor.service \
    ./ops/monitor/knapper-monitor.timer
do
    printf '%s\n' "$MANIFEST" | grep -qx "$required" || {
        echo "publish FAILED: $required missing from $TARBALL" >&2
        exit 1
    }
done

echo "$TARBALL"
