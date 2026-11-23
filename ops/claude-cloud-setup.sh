#!/bin/bash
# Claude cloud environment setup for Knapper (also fits Mailvec and other repos):
# .NET 11 SDK, pinned ripgrep 15, sqlite3 CLI, Mailvec's sqlite-vec.
# MASTER COPY: Knapper's ops/claude-cloud-setup.sh; the settings field is a paste.
# Runs as root AFTER the session's repos are cloned, from the directory holding
# the checkouts; a session may carry several repos. Output also goes
# to /var/log/dotnet-env-setup.log, since a failed setup leaves no session.
exec > >(tee -a /var/log/dotnet-env-setup.log) 2>&1
set -euxo pipefail

# Fail closed: a setup that "succeeds" half-done is snapshotted as the
# environment cache and reused by every session for about a week.
[ "$(uname -m)" = "x86_64" ] || { echo "unexpected arch $(uname -m)" >&2; exit 1; }

# -- apt ----------------------------------------------------------------------
# The image ships two Launchpad PPAs (deadsnakes, ondrej) that answer 403 /
# "no longer signed" here; nothing needs them. `apt-get update` exits 100 if
# ANY source fails, so tolerate it and judge by what actually got installed.
rm -f /etc/apt/sources.list.d/*deadsnakes* /etc/apt/sources.list.d/*ondrej*
apt-get update || echo "WARNING: apt-get update reported errors (exit $?); continuing"

# sqlite3: for inspecting a Mailvec dev database by hand. Not a dependency, and
# it cannot read vec0 tables unless the extension is loaded.
apt-get install -y sqlite3
command -v sqlite3

# -- .NET 11 SDK --------------------------------------------------------------
# Ubuntu 24.04's archive stops at .NET 10, packages.microsoft.com has no .NET
# SDKs for 24.04, and the dotnet/backports PPA is blocked. So: Microsoft's
# tarball from builds.dotnet.microsoft.com, tracking the latest 11.0 patch (as
# apt did, and as CI's `11.x` does), checked against Microsoft's published
# SHA-512. Not dotnet-install.sh: it verifies nothing it downloads.
DOTNET_CHANNEL=11.0
DOTNET_ROOT=/usr/share/dotnet
META="https://builds.dotnet.microsoft.com/dotnet/release-metadata/${DOTNET_CHANNEL}/releases.json"

# Only one dotnet on the box: purge any Ubuntu-packaged .NET (/usr/bin/dotnet).
apt-get purge -y 'dotnet-*' 'aspnetcore-*' 'netstandard-*' || true

curl -fsSL --retry 3 -o /tmp/dotnet-releases.json "$META"
read -r DOTNET_SDK DOTNET_URL DOTNET_SHA512 < <(python3 -I -c '
import json, sys
m = json.load(open(sys.argv[1])); v = m["latest-sdk"]
s = next(s for r in m["releases"] for s in r.get("sdks", []) if s["version"] == v)
f = next(f for f in s["files"] if f["rid"] == "linux-x64" and f["name"].endswith(".tar.gz"))
print(v, f["url"], f["hash"])' /tmp/dotnet-releases.json)
case "$DOTNET_URL" in
  https://builds.dotnet.microsoft.com/*) ;;
  *) echo "unexpected SDK URL: $DOTNET_URL" >&2; exit 1 ;;
esac

curl -fsSL --retry 3 -o /tmp/dotnet-sdk.tar.gz "$DOTNET_URL"
echo "${DOTNET_SHA512}  /tmp/dotnet-sdk.tar.gz" | sha512sum -c -
rm -rf "$DOTNET_ROOT" && mkdir -p "$DOTNET_ROOT"
tar xzf /tmp/dotnet-sdk.tar.gz -C "$DOTNET_ROOT"
rm -f /tmp/dotnet-sdk.tar.gz /tmp/dotnet-releases.json
ln -sf "$DOTNET_ROOT/dotnet" /usr/local/bin/dotnet
# Lets apphosts (dotnet tools, built executables) find the runtime.
mkdir -p /etc/dotnet && echo "$DOTNET_ROOT" > /etc/dotnet/install_location

command -v dotnet
dotnet --list-sdks
dotnet --list-sdks | grep -q "^${DOTNET_SDK} " || { echo "SDK ${DOTNET_SDK} missing" >&2; exit 1; }
[ ! -e /usr/bin/dotnet ] || { echo "/usr/bin/dotnet still present" >&2; exit 1; }

# -- ripgrep (pinned) ---------------------------------------------------------
# rg 15+ is part of Knapper's query contract: rg 14 reports "searches": 0 on a
# no-match query, which empties scannedFiles, so "no match" stops proving the
# scope was searched. Same version and hash as Knapper's .github/workflows/ci.yml
# and runbook §3; bump all three together.
RG_VERSION=15.2.0
RG_SHA256=33e15bcf1624b25cdd2a55813a47a2f95dbe126268203e76aa6a585d1e7b149c
RG_DIR="ripgrep-${RG_VERSION}-x86_64-unknown-linux-musl"

# Remove Ubuntu's rg 14.x so the pinned one is the only ripgrep on the box.
apt-get remove -y ripgrep || true
curl -fsSL --retry 3 -o /tmp/rg.tar.gz \
  "https://github.com/BurntSushi/ripgrep/releases/download/${RG_VERSION}/${RG_DIR}.tar.gz"
echo "${RG_SHA256}  /tmp/rg.tar.gz" | sha256sum -c -
tar xzf /tmp/rg.tar.gz -C /tmp
install -m 0755 "/tmp/${RG_DIR}/rg" /usr/local/bin/rg
rm -rf /tmp/rg.tar.gz "/tmp/${RG_DIR}"

command -v rg
rg --version | head -1
rg --version | head -1 | grep -q "^ripgrep ${RG_VERSION}" \
  || { echo "PATH resolves an rg other than ${RG_VERSION}" >&2; exit 1; }
[ ! -e /usr/bin/rg ] || { echo "/usr/bin/rg still present" >&2; exit 1; }

# -- Mailvec: sqlite-vec --------------------------------------------------------
# Mailvec's SessionStart hook installs this, but a multi-repo session never
# loads Mailvec's .claude/settings.json, so ~650 tests fail without it. Found by
# file, not folder name; reuses that script and its SHA-256 pin.
for fetch in */ops/fetch-sqlite-vec.sh; do
  [ -f "$fetch" ] || continue
  bash "$fetch" linux-x64
  test -f "${fetch%/ops/*}/runtimes/linux-x64/native/vec0.so"
done

echo "setup complete"
