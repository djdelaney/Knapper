#!/bin/bash
# Claude cloud environment setup for Knapper: .NET 10 SDK + pinned ripgrep 15.
#
# THIS FILE IS THE MASTER COPY. The claude.ai environment's "Setup script"
# field holds a PASTE of it, deliberately not a call to this path: nothing we
# have found documents whether the repo is on disk when that field runs.
# Change it here, then re-paste.
# docs/cloud-development.md is the guide; tests/shell/test_ripgrep_pin.sh
# fails CI if the ripgrep pin below drifts from ci.yml's or the runbook's.
#
# Output shows live AND persists to /var/log/knapper-setup.log: a failed setup
# leaves no session to read a log from, and a successful one scrolls away.
exec > >(tee -a /var/log/knapper-setup.log) 2>&1
set -euxo pipefail

# ── apt sources ──────────────────────────────────────────────────────────────
# The image ships two Launchpad PPAs (deadsnakes/Python, ondrej/PHP) that
# answer 403 / "no longer signed" from the sandbox (observed 2026-09-24).
# Knapper needs neither; dropping them lets `apt-get update` finish cleanly.
rm -f /etc/apt/sources.list.d/*deadsnakes* /etc/apt/sources.list.d/*ondrej*

# ── .NET 10 SDK ──────────────────────────────────────────────────────────────
# `apt-get update` exits 100 if ANY configured source is unreachable, even
# when the Ubuntu archive that carries dotnet-sdk-10.0 updated fine. Tolerate
# it, then judge the outcome by what actually got installed.
apt-get update || echo "WARNING: apt-get update reported errors (exit $?); continuing"
apt-get install -y dotnet-sdk-10.0
dotnet --list-sdks
dotnet --list-sdks | grep -q '^10\.' \
  || { echo ".NET 10 SDK not installed" >&2; exit 1; }

# ── ripgrep (pinned) ─────────────────────────────────────────────────────────
# rg 15+ is part of Knapper's query contract: rg 14 reports "searches": 0 on a
# no-match query, which empties scannedFiles, so "no match" stops proving the
# scope was searched. Same version and hash as .github/workflows/ci.yml and
# runbook §3; bump all three together (test_ripgrep_pin.sh enforces it).
RG_VERSION=15.2.0
RG_SHA256=33e15bcf1624b25cdd2a55813a47a2f95dbe126268203e76aa6a585d1e7b149c
RG_DIR="ripgrep-${RG_VERSION}-x86_64-unknown-linux-musl"

[ "$(uname -m)" = "x86_64" ] || { echo "unexpected arch $(uname -m); pin is x86_64" >&2; exit 1; }

# Remove Ubuntu's rg 14.x so the pinned one is the only ripgrep on the box:
# nothing can fall back to it through a PATH missing /usr/local/bin.
apt-get remove -y ripgrep || true

curl -sSLf -o /tmp/rg.tar.gz \
  "https://github.com/BurntSushi/ripgrep/releases/download/${RG_VERSION}/${RG_DIR}.tar.gz"
echo "${RG_SHA256}  /tmp/rg.tar.gz" | sha256sum -c -
tar xzf /tmp/rg.tar.gz -C /tmp
install -m 0755 "/tmp/${RG_DIR}/rg" /usr/local/bin/rg
rm -rf /tmp/rg.tar.gz "/tmp/${RG_DIR}"

# The rg on PATH must be the pinned one, and it must be the only one.
command -v rg
rg --version | head -1
rg --version | head -1 | grep -q "^ripgrep ${RG_VERSION}" \
  || { echo "PATH resolves an rg other than ${RG_VERSION}" >&2; exit 1; }
[ ! -e /usr/bin/rg ] \
  || { echo "/usr/bin/rg still present after removing Ubuntu's ripgrep" >&2; exit 1; }

echo "setup complete"
