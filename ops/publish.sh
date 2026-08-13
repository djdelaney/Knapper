#!/bin/sh
# Build the linux-x64 self-contained deployment artifacts for CT 106.
# Output: artifacts/knapper-<version>-linux-x64.tar.gz containing
#   mcp/   — Knapper.Mcp (self-contained)
#   cli/   — knapper (self-contained)
#   ops/   — systemd units, sync-heartbeat.sh, the logrotate drop-in, and the
#            HOST-side monitor kit
set -eu
cd "$(dirname "$0")/.."

# ops/version.sh is THE build identity, shared with the MSBuild stamping target
# and release.sh, and it validates <Version>'s shape (an absent or malformed one
# would otherwise ship "knapper--linux-x64.tar.gz" past the manifest gate below).
# The full form carries the git revision, so the artifact filename says which
# BUILD it is and not merely which release: knapper-0.2.0+g1f5ff1c-linux-x64.tar.gz,
# matching what the binaries inside it report at every runtime surface. A tarball
# built off uncommitted edits names itself ".dirty" and cannot be mistaken for
# the tagged one — before it reaches a machine, not after.
VERSION=$(./ops/version.sh --full)
STAGE=artifacts/stage
rm -rf "$STAGE"
mkdir -p "$STAGE/ops"

case "$VERSION" in
    *.dirty) echo "publish WARNING: building from a tree with uncommitted changes — this artifact is" >&2
             echo "  named '.dirty' and cannot be reproduced from any tag. Fine for a test install;" >&2
             echo "  cut releases with ops/release.sh from a clean tree." >&2 ;;
esac

# The version is PASSED IN rather than recomputed per project, so the tarball
# name and the string the binaries report come from one evaluation. Recomputing
# would let a file saved between these two publishes stamp the CLI ".dirty" and
# the server not, from a single run — two versions in one artifact, and the
# post-deploy check comparing them would be comparing the wrong things.
# The value already contains '+', which is what makes the stamping target in
# Directory.Build.props defer to it instead of appending a second revision.
STAMP="-p:InformationalVersion=$VERSION"
dotnet publish src/Knapper.Mcp -c Release -r linux-x64 --self-contained "$STAMP" -o "$STAGE/mcp"
dotnet publish src/Knapper.Cli -c Release -r linux-x64 --self-contained "$STAMP" -o "$STAGE/cli"
cp -R ops/systemd "$STAGE/ops/systemd"
cp ops/sync-heartbeat.sh "$STAGE/ops/"
chmod +x "$STAGE/ops/sync-heartbeat.sh"
# The Proxmox-host monitor installs FROM THIS ARCHIVE (runbook §8) — a
# tarball without it cannot perform the documented installation.
cp -R ops/monitor "$STAGE/ops/monitor"
chmod +x "$STAGE/ops/monitor/knapper-monitor.sh"
# Deliberately NOT chmod +x: a logrotate drop-in is config, and it lands in
# /etc/logrotate.d/ where 0644 is what the tool expects. cp -R preserves it.
cp -R ops/logrotate "$STAGE/ops/logrotate"

mkdir -p artifacts
TARBALL="artifacts/knapper-$VERSION-linux-x64.tar.gz"
tar -czf "$TARBALL" -C "$STAGE" .
rm -rf "$STAGE"

# Content gate: every path the runbook installs must exist in the archive.
# A missing path fails the PUBLISH, not the deployment. This list catches the
# other direction from the coverage gate below: a file DELETED from the repo
# while the runbook still tells an operator to install it.
MANIFEST=$(tar -tzf "$TARBALL")
for required in \
    ./mcp/Knapper.Mcp \
    ./cli/knapper \
    ./ops/sync-heartbeat.sh \
    ./ops/logrotate/knapper-sync-log \
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

# Coverage gate: nothing under ops/ leaves the repo silently.
#
# The gate above proves the paths the runbook NAMES arrived; it can say nothing
# about a file nobody remembered to name. That is how the logrotate drop-in was
# lost: committed for a deployment, never added to the stage copy, and the
# runbook's `cp /opt/knapper/ops/logrotate/...` on CT 106 was the first thing to
# notice. Explicit staging makes omission the DEFAULT — safe, because nothing
# unreviewed can slip into an artifact, but silent, because "not shipped" and
# "forgotten" look identical from here.
#
# So: every file under ops/ is either in the archive or on the list below. The
# list is the point. Writing the deliberate omissions down is what separates
# them from the accidental ones.
NOT_SHIPPED='
ops/ct106-runbook.md
ops/publish.sh
ops/release.sh
ops/runbook-lint.sh
ops/version.sh
'

# git ls-files rather than find: `-co --exclude-standard` is tracked PLUS
# untracked-but-not-ignored, so a newly added ops/ file trips this gate before
# it is even committed, while .DS_Store and editor droppings never do. Outside
# a checkout there is no index to read; the gate says so instead of passing
# quietly, since a gate that skips itself in silence is the failure it exists
# to prevent.
if command -v git >/dev/null 2>&1 && git rev-parse --is-inside-work-tree >/dev/null 2>&1; then
    OPS_FILES=$(git ls-files -co --exclude-standard -- ops)
else
    echo "publish WARNING: not a git checkout — the ops/ coverage gate did not run" >&2
    OPS_FILES=''
fi

for opsfile in $OPS_FILES; do
    if printf '%s\n' "$MANIFEST" | grep -qx "./$opsfile"; then continue; fi
    case "$NOT_SHIPPED" in
        *"
$opsfile
"*) continue ;;
    esac
    echo "publish FAILED: $opsfile is in the repo but not in $TARBALL." >&2
    echo "  Stage it (cp into \$STAGE/ops) if the deployment needs it, or add it" >&2
    echo "  to NOT_SHIPPED in this script if it is deliberately host-side only." >&2
    exit 1
done

echo "$TARBALL"
