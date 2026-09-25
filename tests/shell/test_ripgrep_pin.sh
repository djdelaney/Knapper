#!/bin/sh
# The ripgrep pin (version + the release asset's SHA-256) lives in three
# places, because each is run where the other two cannot be:
#
#   .github/workflows/ci.yml   — the version the suite is tested against
#   ops/ct106-runbook.md §3    — the version production runs
#   ops/claude-cloud-setup.sh  — the version cloud dev sessions run
#
# "Bump all three together" is a comment, and comments do not fail builds.
# A drifted copy is silent: CI green on 15.x while production installs
# whatever the runbook still says, or a cloud session testing a different
# binary from the one CI blessed. This makes the drift loud.
#
# Each file must carry EXACTLY ONE version line and one hash line: a pattern
# that stopped matching would otherwise extract nothing from every file, and
# three empty strings agree with each other perfectly.
set -u

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
FAILURES=0

fail() {
    echo "   FAIL: $1" >&2
    FAILURES=$((FAILURES + 1))
}

# extract <file> <variable-name> → prints the one value assigned, or nothing
# (with a reason on stderr). Runs in a $(…) subshell, so it cannot count a
# failure itself: an empty result is caught by the shape checks below.
extract() {
    values=$(grep -E "^[[:space:]]*$2=" "$ROOT/$1" | sed -E "s/^[[:space:]]*$2=//; s/[[:space:]].*//")
    count=$(printf '%s' "$values" | grep -c . || true)
    if [ "$count" -ne 1 ]; then
        echo "   $1: expected exactly one $2= line, found $count" >&2
        return
    fi
    printf '%s' "$values"
}

# shape <label> <value> <ERE> — an empty or malformed extraction fails here.
shape() {
    printf '%s' "$2" | grep -Eq "$3" || fail "$1 is missing or malformed: '$2'"
}

CI_VER=$(extract .github/workflows/ci.yml RG_VERSION)
CI_SHA=$(extract .github/workflows/ci.yml RG_SHA256)
RB_VER=$(extract ops/ct106-runbook.md RG)
RB_SHA=$(extract ops/ct106-runbook.md RG_SHA256)
CL_VER=$(extract ops/claude-cloud-setup.sh RG_VERSION)
CL_SHA=$(extract ops/claude-cloud-setup.sh RG_SHA256)

VER='^[0-9]+\.[0-9]+\.[0-9]+$'
SHA='^[0-9a-f]{64}$'
shape "ci.yml RG_VERSION" "$CI_VER" "$VER"
shape "ci.yml RG_SHA256" "$CI_SHA" "$SHA"
shape "runbook RG" "$RB_VER" "$VER"
shape "runbook RG_SHA256" "$RB_SHA" "$SHA"
shape "claude-cloud-setup.sh RG_VERSION" "$CL_VER" "$VER"
shape "claude-cloud-setup.sh RG_SHA256" "$CL_SHA" "$SHA"

[ "$RB_VER" = "$CI_VER" ] || fail "runbook pins ripgrep '$RB_VER', ci.yml pins '$CI_VER'"
[ "$CL_VER" = "$CI_VER" ] || fail "claude-cloud-setup.sh pins ripgrep '$CL_VER', ci.yml pins '$CI_VER'"
[ "$RB_SHA" = "$CI_SHA" ] || fail "runbook RG_SHA256 differs from ci.yml's"
[ "$CL_SHA" = "$CI_SHA" ] || fail "claude-cloud-setup.sh RG_SHA256 differs from ci.yml's"

# The setup script is pasted into claude.ai rather than run by anything in
# this repo, so a syntax error would surface only as a failed environment.
bash -n "$ROOT/ops/claude-cloud-setup.sh" 2>/dev/null || fail "ops/claude-cloud-setup.sh does not parse (bash -n)"

[ "$FAILURES" -eq 0 ] || exit 1
echo "   ripgrep pin ${CI_VER} agrees across ci.yml, runbook, cloud setup"
