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
#   3. Metrics deltas (brief §8 rate signals) from the CT's metrics
#      snapshot: audit-append failures (any > 0 alerts), query timeouts,
#      tool errors, truncated and generation-changed responses over the
#      window since the previous monitor run. The snapshot carries
#      cumulative counters plus the process start stamp, so a server
#      restart resets the baseline instead of false-alarming.
#      Requires `jq` on the host.
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
# A monitor whose mailer is missing is the exact "alert path silently dead"
# condition this script exists to prevent. Checked up front, loudly: the
# systemd unit fails and the journal names the cause.
command -v mail >/dev/null 2>&1 || {
    echo "knapper-monitor: 'mail' is not installed — alerts CANNOT be delivered; install a mailer (msmtp + mailutils)" >&2
    exit 2
}
# shellcheck disable=SC1090
. "$CONFIG"

STAMP_PATH="${STAMP_PATH:-/var/lib/knapper/commit-stamp}"
MAX_STAMP_AGE="${MAX_STAMP_AGE:-3900}"
MAILTO="${MAILTO:-alerts@example.com}"
CURL_TIMEOUT="${CURL_TIMEOUT:-20}"
METRICS_PATH="${METRICS_PATH:-/var/lib/knapper/metrics.json}"
STATE_DIR="${STATE_DIR:-/var/lib/knapper-monitor}"
# Per-window (one monitor interval) delta thresholds. Audit failures alert
# on ANY occurrence; the others are agent-behavior signals with headroom —
# tune after observing real traffic, per the brief's "exercise before trust".
MAX_AUDIT_FAILURES="${MAX_AUDIT_FAILURES:-0}"
MAX_QUERY_TIMEOUTS="${MAX_QUERY_TIMEOUTS:-5}"
MAX_TOOL_ERRORS="${MAX_TOOL_ERRORS:-50}"
MAX_STALE_REJECTIONS="${MAX_STALE_REJECTIONS:-25}"
MAX_TRUNCATED="${MAX_TRUNCATED:-100}"
MAX_GENERATION_CHANGED="${MAX_GENERATION_CHANGED:-100}"

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

# ---- 3. metrics deltas since the previous monitor run -------------------
CURRENT=$(pct exec "$CT_ID" -- cat "$METRICS_PATH" 2>/dev/null)
if [ -z "${CURRENT:-}" ]; then
    fail "metrics snapshot ${METRICS_PATH} missing/unreadable in CT ${CT_ID} — Vault__MetricsPath unset, knapper never started, or CT stopped"
elif ! command -v jq >/dev/null 2>&1; then
    fail "jq is not installed on the monitor host — metrics deltas cannot be evaluated"
else
    mkdir -p "$STATE_DIR"
    PREV_FILE="$STATE_DIR/last-metrics.json"
    PREV=""
    [ -r "$PREV_FILE" ] && PREV=$(cat "$PREV_FILE")

    delta() { # $1 jq field name
        CUR_V=$(printf '%s' "$CURRENT" | jq -r ".$1 // 0")
        PREV_V=$(printf '%s' "$PREV" | jq -r ".$1 // 0" 2>/dev/null || echo 0)
        echo $((CUR_V - PREV_V))
    }

    CUR_START=$(printf '%s' "$CURRENT" | jq -r '.StartedAt // ""')
    PREV_START=$(printf '%s' "$PREV" | jq -r '.StartedAt // ""' 2>/dev/null || echo "")
    if [ -z "$PREV" ] || [ "$CUR_START" != "$PREV_START" ]; then
        # First run, or the server restarted: counters reset legitimately.
        # Baseline this snapshot; deltas resume next run. A restart LOOP
        # still surfaces through /up and the commit stamp.
        :
    else
        check_delta() { # $1 field  $2 max  $3 description
            D=$(delta "$1")
            [ "$D" -gt "$2" ] && fail "$3: ${D} since the last monitor run (max $2)"
        }
        check_delta AuditAppendFailures "$MAX_AUDIT_FAILURES" "AUDIT APPEND FAILURES — landed changes may lack audit records"
        check_delta QueryTimeouts       "$MAX_QUERY_TIMEOUTS" "query timeouts"
        check_delta ToolErrors          "$MAX_TOOL_ERRORS" "tool errors"
        check_delta StaleRejections     "$MAX_STALE_REJECTIONS" "stale-write rejections (agents racing or retrying stale bases)"
        check_delta TruncatedResponses  "$MAX_TRUNCATED" "truncated responses"
        check_delta GenerationChangedResponses "$MAX_GENERATION_CHANGED" "generation-changed responses"
    fi
    printf '%s' "$CURRENT" > "$PREV_FILE"
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
