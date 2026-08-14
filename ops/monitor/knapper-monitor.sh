#!/bin/sh
# Knapper external monitor — runs on the PROXMOX HOST, outside CT 106
# (brief §8): a monitor inside the CT dies with the CT. Follows the
# pbs-backup-freshness.sh precedent: silent on success, mail on failure.
#
# Checks:
#   1. /up through the PUBLIC tunnel with the monitoring service token —
#      must answer 200. 503 or unreachable covers: vault unreachable, sync
#      unhealthy, rg missing, audit unwritable, conflict files present,
#      knapper down, tunnel down, Access misconfigured. Its BODY additionally
#      carries an oversized-file warning, which is a 200 by design (nothing is
#      blocked) and so has to be read rather than inferred from the code.
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
#   knapper-monitor.sh [config]          config default: /etc/knapper-monitor.conf
#   knapper-monitor.sh --test [config]   force a mail to prove delivery
#
# Alert cadence: one mail when the failure SET changes, a reminder at most
# once per RENOTIFY_SECONDS while it is unchanged, one mail on recovery. A
# conflict file blocks mutations until a HUMAN reconciles it — mailing that
# every five minutes for three days is how a monitor gets filtered into a
# folder nobody reads. The exit code always reflects the live state.
#
# Exercise EVERY failure path before trusting this monitor (brief §8):
# stop knapper, stop cloudflared, stop the commit timer, revoke the service
# token — each must produce exactly one mail. Point MAILTO at TEST_MAILTO
# while doing it, so drills cannot be mistaken for real alerts later.
set -u

CONFIG="/etc/knapper-monitor.conf"
TEST_ALERT=0
if [ "${1:-}" = "--test" ]; then
    TEST_ALERT=1
    shift
fi
[ -n "${1:-}" ] && CONFIG="$1"

# ---- configuration (all required unless a default is shown) -------------
#   UP_URL           e.g. https://mcp.example.com/up
#   CF_CLIENT_ID     Access service token id (the monitoring app's token)
#   CF_CLIENT_SECRET Access service token secret
#   CT_ID            container id, e.g. 106
#   STAMP_PATH       default /var/lib/knapper/commit-stamp (inside the CT)
#   MAX_STAMP_AGE    seconds; default 3900 (30-min timer + one missed run)
#   MAILTO           default alerts@example.com
#   TEST_MAILTO      where --test goes; default $MAILTO. Point failure-injection
#                    drills here so they cannot be mistaken for real alerts.
#   RENOTIFY_SECONDS default 86400 — see "alert cadence" below
#   CURL_TIMEOUT     seconds; default 20
[ -r "$CONFIG" ] || { echo "knapper-monitor: config $CONFIG not readable" >&2; exit 2; }
# A monitor whose mailer is missing is the exact "alert path silently dead"
# condition this script exists to prevent. Checked up front, loudly: the
# systemd unit fails and the journal names the cause.
#
# sendmail(8) first, `mail` second. On a Proxmox host the sendmail interface
# is usually the established one, and it is what msmtp-mta provides. NEVER
# install `mailutils` to satisfy the `mail` branch: it brings its own MTA and
# takes over /usr/sbin/sendmail, breaking every OTHER monitor on the host that
# went through sendmail. bsd-mailx is the safe provider of `mail`.
if command -v sendmail >/dev/null 2>&1; then
    MAILER=sendmail
elif command -v mail >/dev/null 2>&1; then
    MAILER=mail
else
    echo "knapper-monitor: no mailer — alerts CANNOT be delivered. Install msmtp + msmtp-mta (sendmail)," \
        "or msmtp + bsd-mailx. NOT mailutils: it hijacks /usr/sbin/sendmail for the whole host." >&2
    exit 2
fi
# shellcheck disable=SC1090
. "$CONFIG"

STAMP_PATH="${STAMP_PATH:-/var/lib/knapper/commit-stamp}"
MAX_STAMP_AGE="${MAX_STAMP_AGE:-3900}"
MAILTO="${MAILTO:-alerts@example.com}"
TEST_MAILTO="${TEST_MAILTO:-$MAILTO}"
RENOTIFY_SECONDS="${RENOTIFY_SECONDS:-86400}"
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

# One mail, either interface. sendmail(8) needs the headers in the body.
send_mail() {
    _to="$1"
    _subject="$2"
    if [ "$MAILER" = sendmail ]; then
        {
            printf 'To: %s\nSubject: %s\n\n' "$_to" "$_subject"
            cat
        } | sendmail -t
    else
        mail -s "$_subject" "$_to"
    fi
}

# ---- 1. /up through the tunnel ------------------------------------------
# NEVER add -L here, and keep the test `!= 200` rather than a not-an-error
# range. Access refuses a request whose token it does not accept by sending
# it to log in: 302 → the Cloudflare login page → 200 text/html. Followed,
# that arrives as a 200 whose body is HTML, and this monitor reports a healthy
# vault while it is being refused at the edge and reading nothing at all.
# `knapper verify` shipped exactly that bug on its own probes (2026-08-14).
# Check 1b is the second line of defence — it refuses to read a body it cannot
# parse — but it is a backstop, not a reason to relax this test.
UP_BODY=$(mktemp)
HTTP_CODE=$(curl -s -o "$UP_BODY" -w '%{http_code}' \
    --max-time "$CURL_TIMEOUT" \
    -H "CF-Access-Client-Id: ${CF_CLIENT_ID}" \
    -H "CF-Access-Client-Secret: ${CF_CLIENT_SECRET}" \
    "$UP_URL" 2>/dev/null) || HTTP_CODE=000
if [ "$HTTP_CODE" != "200" ]; then
    fail "/up returned HTTP ${HTTP_CODE} (expected 200) at ${UP_URL} — knapper degraded/down, tunnel down, or Access rejecting the monitor token"
fi

# ---- 1b. oversized files: a WARNING carried inside a 200 -----------------
# Obsidian Sync silently refuses any file over its per-file ceiling — it logs
# the rejection and prints "Fully synced" in the same millisecond, so a
# stranded file leaves every other signal green. Knapper refuses its OWN
# oversized writes; this catches one that got here another way — a human shell
# on the CT, or a file predating the guard — which the mutation guard cannot
# see. It does NOT catch an oversized note made on a Mac: the ceiling is
# symmetric, so that one never downloads to CT 106 and is absent rather than
# oversized here. Nothing detects that; see docs/extending.md.
#
# Deliberately NOT a 503 on the server side: nothing is blocked, the rest of
# the vault syncs, and no human has to reconcile anything the way a conflict
# file demands. It rides here instead, where the existing cadence rules turn
# it into one mail on transition plus an occasional reminder. jq is checked
# again because check 3 owns the "jq missing" alert; a second one would be noise.
#
# The 200 guard above is load-bearing, not just an optimisation. `oversized.ok`
# is also false when the scan could not COMPLETE (unreadable directory, walk
# budget expired) — and that case degrades to 503, so it is check 1's alert,
# not this one's. Reporting "vault contains file(s) Sync will not carry" for a
# scan that found nothing because it never finished would name the wrong fault.
# `knapper doctor` in the CT distinguishes them: it warns "could not scan …".
#
# ⚠️ NEVER write this as `jq -r '.oversized.ok // "absent"'`. jq's `//` is
# FALSY-triggered, not absence-triggered: its right side is substituted for no
# value, for `null`, AND for `false`. So the one input this check exists to
# catch — `ok` being the boolean `false` — came out as the string "absent" and
# compared unequal to "false". The branch was unreachable; `true` was the only
# value that ever survived `//` intact, so the check could report "healthy" and
# nothing else. Shipped that way, found on CT 106 2026-08-14 by runbook §8
# drill 4, which is exactly the sentence in the runbook: "a mail that never
# arrives here means the body check is dead while every status-code check still
# looks green." Pinned by tests/shell/test_knapper_monitor.sh.
#
# The jq expression below therefore emits one of exactly three fixed tokens and
# never interpolates a value from the response: a mail is not a place to render
# whatever a body happened to contain. An unparseable body (the Access login
# page arriving as HTML) fails jq itself and is caught by the same catch-all —
# "could not tell" must never leave here dressed as "checked, all clear".
if [ "$HTTP_CODE" = "200" ] && command -v jq >/dev/null 2>&1; then
    OVERSIZED_OK=$(jq -r '.oversized.ok
        | if . == true then "true" elif . == false then "false" else "absent" end' \
        < "$UP_BODY" 2>/dev/null) || OVERSIZED_OK="unreadable"
    case "$OVERSIZED_OK" in
        true) ;;
        false)
            fail "vault contains file(s) Obsidian Sync will NOT carry — they exist on the CT, commit to git, and reach no device. Run \`knapper doctor\` in the CT to name them" ;;
        *)
            fail "/up answered 200 but .oversized.ok could not be read (${OVERSIZED_OK}) — the response shape changed, or the body is not the vault surface answering. The oversized-file check is BLIND until this is resolved" ;;
    esac
fi
rm -f "$UP_BODY"

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
# --test bypasses the cadence entirely: it exists to prove delivery, and a
# suppressed test mail would prove the opposite of what the operator wanted.
if [ "$TEST_ALERT" = 1 ]; then
    printf 'Knapper monitor TEST alert from %s at %s — delivery path works if you are reading this.\n' \
        "$(hostname)" "$(date -Is)" \
        | send_mail "$TEST_MAILTO" "[knapper] monitor TEST: $(hostname)"
    exit 0
fi

# Alert cadence. /up answers 503 for as long as an unreconciled Sync conflict
# file exists — a condition that legitimately persists for days until a human
# reconciles it. Mailing that every interval trains the operator to filter the
# monitor out, which is the same outcome as having no monitor. So: mail when
# the failure SET changes (fingerprint), remind at most once per
# RENOTIFY_SECONDS while it is unchanged, and send exactly one recovery mail
# when it clears. The exit CODE still reflects the live state on every run —
# systemd and `systemctl list-timers` never see the suppression.
mkdir -p "$STATE_DIR"
ALERT_STATE="$STATE_DIR/last-alert"
NOW=$(date +%s)

if [ -z "$FAILURES" ]; then
    if [ -f "$ALERT_STATE" ]; then
        printf 'Knapper monitor RECOVERED on %s at %s — all checks pass again.\n' \
            "$(hostname)" "$(date -Is)" \
            | send_mail "$MAILTO" "[knapper] monitor recovered: $(hostname)"
        rm -f "$ALERT_STATE"
    fi
    exit 0
fi

# cksum, not a hash tool: every coreutils has it, and this only needs to tell
# "same failure set" from "different failure set".
FINGERPRINT=$(printf '%s' "$FAILURES" | cksum | tr -d ' ')
PREV_FINGERPRINT=""
PREV_NOTIFIED=0
if [ -r "$ALERT_STATE" ]; then
    PREV_FINGERPRINT=$(sed -n 1p "$ALERT_STATE")
    PREV_NOTIFIED=$(sed -n 2p "$ALERT_STATE")
    [ -n "$PREV_NOTIFIED" ] || PREV_NOTIFIED=0
fi

NOTIFY=0
NOTE=""
if [ "$FINGERPRINT" != "$PREV_FINGERPRINT" ]; then
    NOTIFY=1
elif [ "$((NOW - PREV_NOTIFIED))" -ge "$RENOTIFY_SECONDS" ]; then
    NOTIFY=1
    NOTE="(still failing — reminder after $(( (NOW - PREV_NOTIFIED) / 3600 ))h; the set has not changed)
"
fi

if [ "$NOTIFY" = 1 ]; then
    printf 'Knapper monitor failures on %s at %s:\n%s\n%s' \
        "$(hostname)" "$(date -Is)" "$NOTE" "$FAILURES" \
        | send_mail "$MAILTO" "[knapper] monitor alert: $(hostname)"
    printf '%s\n%s\n' "$FINGERPRINT" "$NOW" > "$ALERT_STATE"
else
    # Keep the notification timestamp; only the fingerprint is re-asserted.
    printf '%s\n%s\n' "$FINGERPRINT" "$PREV_NOTIFIED" > "$ALERT_STATE"
fi
exit 1
