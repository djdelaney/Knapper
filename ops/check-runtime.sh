#!/bin/sh
# Refuse a self-contained build whose bundled .NET runtime is not the latest
# patch of its channel. Called by ops/publish.sh on the staged output, before
# the tarball exists.
#
#   ops/check-runtime.sh <runtimeconfig.json>...
#
# Why this is a gate and not a habit: a self-contained artifact carries its own
# copy of the runtime, picked by whichever SDK happened to build it — the SDK
# bundles the newest runtime patch it knows of, and nothing else. Nothing in
# the build, the tests, doctor or verify can see that copy go stale, and
# rebooting the host restarts the same old copy. v0.12.1 shipped to CT 106
# carrying 10.0.8 while 10.0.13 was current (five monthly security releases
# missed), found by a deployment agent rather than by anything here.
#
# The latest patch is read from Microsoft's release metadata for the channel
# the build ITSELF bundles (10.0.x → .../10.0/releases.json), so a future
# TargetFramework bump needs no edit here.
#
# Exit: 0 current · 1 stale (or the inputs disagree) · 2 could not tell.
# "Could not tell" is a refusal, not a pass: an offline publish is exactly the
# one that cannot know it is current. KNAPPER_ALLOW_STALE_RUNTIME=1 downgrades
# both refusals to a warning, for an operator who has decided to ship anyway
# (an urgent fix ahead of an SDK install, or no network) — say so in the
# deployment record.
#
# KNAPPER_DOTNET_RELEASES_URL overrides the metadata base URL (tests point it
# at file:// fixtures).
set -u

BASE="${KNAPPER_DOTNET_RELEASES_URL:-https://builds.dotnet.microsoft.com/dotnet/release-metadata}"

refuse() {
    code=$1; shift
    if [ "${KNAPPER_ALLOW_STALE_RUNTIME:-}" = 1 ]; then
        echo "publish WARNING: $* — shipping anyway (KNAPPER_ALLOW_STALE_RUNTIME=1)" >&2
        exit 0
    fi
    echo "publish FAILED: $*" >&2
    echo "  Set KNAPPER_ALLOW_STALE_RUNTIME=1 to ship regardless, and record why." >&2
    exit "$code"
}

[ "$#" -gt 0 ] || { echo "usage: $0 <runtimeconfig.json>..." >&2; exit 2; }

# Every framework every input bundles must be ONE version. A self-contained
# app's runtimeconfig lists them under includedFrameworks; a framework-
# dependent one has none, and then there is no bundled copy to judge — which
# is not this artifact's shape, so it is refused rather than passed.
BUNDLED=''
for cfg in "$@"; do
    [ -f "$cfg" ] || refuse 1 "$cfg does not exist"
    versions=$(tr -d ' \t\r\n' < "$cfg" \
        | grep -oE '"includedFrameworks":\[[^]]*\]' \
        | grep -oE '"version":"[^"]*"' | cut -d'"' -f4)
    [ -n "$versions" ] || refuse 1 "$cfg lists no includedFrameworks — not a self-contained build, nothing bundled to check"
    for v in $versions; do
        if [ -z "$BUNDLED" ]; then
            BUNDLED=$v
        elif [ "$v" != "$BUNDLED" ]; then
            refuse 1 "bundled runtimes disagree ($BUNDLED vs $v in $cfg) — one artifact, two runtimes"
        fi
    done
done

printf '%s' "$BUNDLED" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+$' \
    || refuse 1 "bundled runtime '$BUNDLED' is not a release version (a preview build?)"
CHANNEL=${BUNDLED%.*}

URL="$BASE/$CHANNEL/releases.json"
META=$(curl -fsSL --max-time 20 "$URL" 2>/dev/null) \
    || refuse 2 "could not fetch $URL to learn the latest $CHANNEL patch — cannot confirm runtime $BUNDLED is current"

field() {
    printf '%s' "$META" | grep -oE "\"$1\"[[:space:]]*:[[:space:]]*\"[^\"]*\"" | head -n 1 | cut -d'"' -f4
}
LATEST=$(field latest-release)
LATEST_SDK=$(field latest-sdk)
printf '%s' "$LATEST" | grep -Eq "^${CHANNEL%.*}\.${CHANNEL#*.}\.[0-9]+$" \
    || refuse 2 "$URL gave no usable latest-release ('$LATEST') — cannot confirm runtime $BUNDLED is current"

if [ "${BUNDLED##*.}" -lt "${LATEST##*.}" ]; then
    refuse 1 "bundled .NET runtime $BUNDLED is behind the latest $CHANNEL patch, $LATEST.
  The SDK that built this bundles the newest runtime IT knows of; install a
  newer one (latest: SDK ${LATEST_SDK:-?}), check \`dotnet --list-sdks\`, and publish again."
fi

echo "bundled .NET runtime $BUNDLED is the latest $CHANNEL patch"
