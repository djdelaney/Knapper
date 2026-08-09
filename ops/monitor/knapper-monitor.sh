#!/bin/sh
# Knapper external monitor — runs on the PROXMOX HOST, outside CT 106
# (brief §8): a monitor inside the CT dies with the CT. Follows the
# pbs-backup-freshness.sh precedent: silent on success, mail on failure.
#
# Checks:
#   1. /up through the PUBLIC tunnel with the monitoring service token —
#      must answer 200. 503 or unreachable covers: vault unreachable, sync
#      unhealthy, rg missing, audit unwritable, conflict files present,
#      knapper down, tunnel down, Access misconfigured.
#   2. Git snapshot freshness via the commit STAMP inside the CT — the
#      stamp is touched on every successful `knapper commit` run including
#      "nothing to commit". Deliberate deviation from the brief's
#      last-commit-age monitoring: the commit job creates no commit when
#      the vault is quiet, so HEAD age cannot distinguish a quiet vault
#      from a dead timer. The stamp can. (See ct106-runbook.md §8.)
#
# Usage:
#   knapper-monitor.sh [config]     config default: /etc/knapper-monitor.conf
#   knapper-monitor.sh --test       force an alert to prove mail delivery
#
# Exercise EVERY failure path before trusting this monitor (brief §8):
# stop knapper, stop cloudflared, stop the commit timer, revoke the service
# token — each must produce exactly one mail.
set -u

CONFIG="/etc/knapper-monitor.conf"
TEST_ALERT=0
case "${1:-}" in
    --test) TEST_ALERT=1 ;;
    "") ;;
    *) CONFIG="$1" ;;
esac

# ---- configuration (all required unless a default is shown) -------------
#   UP_URL           e.g. https://mcp.example.com/up
#   CF_CLIENT_ID     Access service token id (the monitoring app's token)
#   CF_CLIENT_SECRET Access service token secret
#   CT_ID            container id, e.g. 106
#   STAMP_PATH       default /var/lib/knapper/commit-stamp (inside the CT)
#   MAX_STAMP_AGE    seconds; default 3900 (30-min timer + one missed run)
#   MAILTO           default alerts@example.com
#   CURL_TIMEOUT     seconds; default 20
[ -r "$CONFIG" ] || { echo "knapper-monitor: config $CONFIG not readable" >&2; exit 2; }
# shellcheck disable=SC1090
. "$CONFIG"

STAMP_PATH="${STAMP_PATH:-/var/lib/knapper/commit-stamp}"
MAX_STAMP_AGE="${MAX_STAMP_AGE:-3900}"
MAILTO="${MAILTO:-alerts@example.com}"
CURL_TIMEOUT="${CURL_TIMEOUT:-20}"

FAILURES=""
fail() {
    FAILURES="${FAILURES}- $1
"
}

# ---- 1. /up through the tunnel ------------------------------------------
HTTP_CODE=$(curl -s -o /dev/null -w '%{http_code}' \
    --max-time "$CURL_TIMEOUT" \
    -H "CF-Access-Client-Id: ${CF_CLIENT_ID}" \
    -H "CF-Access-Client-Secret: ${CF_CLIENT_SECRET}" \
    "$UP_URL" 2>/dev/null) || HTTP_CODE=000
if [ "$HTTP_CODE" != "200" ]; then
    fail "/up returned HTTP ${HTTP_CODE} (expected 200) at ${UP_URL} — knapper degraded/down, tunnel down, or Access rejecting the monitor token"
fi

# ---- 2. commit-stamp freshness inside the CT ----------------------------
STAMP_MTIME=$(pct exec "$CT_ID" -- stat -c %Y "$STAMP_PATH" 2>/dev/null)
if [ -z "${STAMP_MTIME:-}" ]; then
    fail "commit stamp ${STAMP_PATH} missing/unreadable in CT ${CT_ID} — commit timer never succeeded (or CT stopped)"
else
    NOW=$(date +%s)
    AGE=$((NOW - STAMP_MTIME))
    if [ "$AGE" -gt "$MAX_STAMP_AGE" ]; then
        fail "commit stamp is ${AGE}s old (max ${MAX_STAMP_AGE}s) — the git snapshot timer is dead or every run is failing (secret scan? lock?)"
    fi
fi

# ---- alert / exit -------------------------------------------------------
if [ "$TEST_ALERT" = 1 ]; then
    fail "TEST alert requested with --test: delivery path works if you are reading this"
fi

[ -z "$FAILURES" ] && exit 0

printf 'Knapper monitor failures on %s at %s:\n\n%s' \
    "$(hostname)" "$(date -Is)" "$FAILURES" \
    | mail -s "[knapper] monitor alert: $(hostname)" "$MAILTO"
exit 1
