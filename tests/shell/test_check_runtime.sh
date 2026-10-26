#!/bin/sh
# Tests for ops/check-runtime.sh — publish's stale-runtime gate.
#
# The claims worth pinning are the refusals: a stale runtime fails, and so does
# "could not tell" (unreachable or unusable metadata) — a gate that passes when
# it cannot see is the silent version of the failure it exists to catch. The
# override must turn every refusal into a warning and nothing else.
set -u

SCRIPT="$(cd "$(dirname "$0")/../.." && pwd)/ops/check-runtime.sh"
FAILURES=0
TMPROOT=$(mktemp -d)
trap 'rm -rf "$TMPROOT"' EXIT

fail() {
    echo "   FAIL: $1" >&2
    FAILURES=$((FAILURES + 1))
}

# metadata <channel> <latest-release> → a fixture releases.json for that channel
metadata() {
    mkdir -p "$TMPROOT/meta/$1"
    printf '{\n  "channel-version": "%s",\n  "latest-release": "%s",\n  "latest-sdk": "%s.403",\n  "releases": []\n}\n' \
        "$1" "$2" "$1" > "$TMPROOT/meta/$1/releases.json"
}

# config <name> <version>... → a self-contained runtimeconfig bundling those frameworks
config() {
    name=$1; shift
    frameworks=''
    for v in "$@"; do
        frameworks="$frameworks${frameworks:+,}{\"name\": \"Microsoft.NETCore.App\", \"version\": \"$v\"}"
    done
    printf '{\n  "runtimeOptions": {\n    "tfm": "net10.0",\n    "includedFrameworks": [%s]\n  }\n}\n' \
        "$frameworks" > "$TMPROOT/$name.json"
}

# run <expected-status> <label> <args...>
run() {
    want=$1; label=$2; shift 2
    OUT=$(KNAPPER_DOTNET_RELEASES_URL="file://$TMPROOT/meta" "$SCRIPT" "$@" 2>&1)
    STATUS=$?
    [ "$STATUS" -eq "$want" ] || fail "$label: exit $STATUS, wanted $want — $OUT"
}

metadata 10.0 10.0.14

# ---- current ---------------------------------------------------------------
config mcp 10.0.14 10.0.14
config cli 10.0.14
run 0 "current runtime" "$TMPROOT/mcp.json" "$TMPROOT/cli.json"

# A newer bundle than the metadata names (the metadata lags a release) is not stale.
config ahead 10.0.15
run 0 "runtime ahead of metadata" "$TMPROOT/ahead.json"

# ---- stale -----------------------------------------------------------------
config stale 10.0.8 10.0.8
run 1 "stale runtime" "$TMPROOT/stale.json"
case "$OUT" in *10.0.8*10.0.14*) : ;; *) fail "stale message names neither version pair: $OUT" ;; esac
case "$OUT" in *10.0.403*) : ;; *) fail "stale message does not name the SDK to install: $OUT" ;; esac

# Numeric, not lexical: 10.0.9 is behind 10.0.14 even though "9" > "1".
config nine 10.0.9
run 1 "10.0.9 vs 10.0.14 compared numerically" "$TMPROOT/nine.json"

# ---- inputs that cannot be judged ------------------------------------------
config mixed 10.0.14 10.0.12
run 1 "frameworks disagree within one file" "$TMPROOT/mixed.json"
run 1 "frameworks disagree across files" "$TMPROOT/cli.json" "$TMPROOT/stale.json"

printf '{"runtimeOptions": {"tfm": "net10.0", "framework": {"name": "Microsoft.NETCore.App", "version": "10.0.0"}}}\n' \
    > "$TMPROOT/fdd.json"
run 1 "framework-dependent config (nothing bundled)" "$TMPROOT/fdd.json"
run 1 "missing runtimeconfig" "$TMPROOT/nope.json"

# ---- could not tell: refused, not passed -----------------------------------
config other 11.0.2
run 2 "metadata unreachable for the channel" "$TMPROOT/other.json"

mkdir -p "$TMPROOT/meta/12.0"
printf '<html>not json</html>\n' > "$TMPROOT/meta/12.0/releases.json"
config garbled 12.0.1
run 2 "metadata without a latest-release" "$TMPROOT/garbled.json"

# A latest-release from the wrong channel is unusable, not a comparison.
metadata 13.0 14.0.1
config wrongchan 13.0.1
run 2 "latest-release from another channel" "$TMPROOT/wrongchan.json"

# ---- override: every refusal becomes a warning -----------------------------
OUT=$(KNAPPER_ALLOW_STALE_RUNTIME=1 KNAPPER_DOTNET_RELEASES_URL="file://$TMPROOT/meta" \
    "$SCRIPT" "$TMPROOT/stale.json" 2>&1)
[ $? -eq 0 ] || fail "override did not pass a stale runtime: $OUT"
case "$OUT" in *WARNING*) : ;; *) fail "override passed without a warning: $OUT" ;; esac

OUT=$(KNAPPER_ALLOW_STALE_RUNTIME=1 KNAPPER_DOTNET_RELEASES_URL="file://$TMPROOT/meta" \
    "$SCRIPT" "$TMPROOT/other.json" 2>&1)
[ $? -eq 0 ] || fail "override did not pass an unknown runtime: $OUT"

# Only the literal 1 is the override.
OUT=$(KNAPPER_ALLOW_STALE_RUNTIME=yes KNAPPER_DOTNET_RELEASES_URL="file://$TMPROOT/meta" \
    "$SCRIPT" "$TMPROOT/stale.json" 2>&1)
[ $? -eq 1 ] || fail "KNAPPER_ALLOW_STALE_RUNTIME=yes was taken as the override"

# ---- publish.sh actually calls it, before the tarball ----------------------
PUBLISH="$(dirname "$SCRIPT")/publish.sh"
gate=$(grep -n 'ops/check-runtime.sh "\$STAGE' "$PUBLISH" | head -n 1 | cut -d: -f1)
tar=$(grep -n '^tar ' "$PUBLISH" | head -n 1 | cut -d: -f1)
if [ -z "$gate" ] || [ -z "$tar" ]; then
    fail "publish.sh does not call check-runtime.sh on the staged output"
elif [ "$gate" -ge "$tar" ]; then
    fail "publish.sh runs check-runtime.sh after creating the tarball (line $gate ≥ $tar)"
fi

[ "$FAILURES" -eq 0 ]
