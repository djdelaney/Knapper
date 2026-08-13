#!/bin/sh
# Touch the sync heartbeat iff continuous sync is demonstrably healthy.
# Run by knapper-heartbeat.timer every minute. Silent on success; every
# WITHHELD touch prints one line naming why.
#
# SCOPE: this gate measures TRANSPORT health, not content completeness.
# Measured on CT 106 2026-08-13: ob logs "Fully synced" IMMEDIATELY AFTER
# "File too large to sync (… max 5.00 MB)" — 67 such pairs in 117ms. The
# message means "this sync cycle completed", NOT "the vault is in sync": a
# permanently rejected file yields an endless green signal. Transport health
# is the right property for the mutation gate (Knapper writes small .md), but
# do NOT read a touched heartbeat as an all-clear on vault CONTENTS. Oversized
# files are a separate guard; this script cannot see them.
#
# WHY THE UNHEALTHY PATH LOGS: withholding the touch is the whole mechanism,
# and it used to leave no trace anywhere. Healthy and unhealthy runs were
# byte-identical in the journal (systemd's own Starting/Finished lines), and
# the heartbeat file keeps no history — only an mtime the next healthy tick
# overwrites. So "has sync ever been dead long enough to matter?" — the
# question that calibrates Sync__MaxAgeSeconds — could only be answered by
# parsing ob's sync.log: a third party's format, which interleaves vault
# filenames, rotates weekly under copytruncate, and where `Disconnected from
# server` is ALSO emitted on clean shutdown (measured on CT 106 2026-08-13: a
# naive grep -c over-reported 10 events against 5 real ones, and filtering on
# ob's shutdown line still left the LARGEST non-network entry in, because a
# service that was never started prints no shutdown line at all).
#
#   journalctl -u knapper-heartbeat --since -7d | grep withheld
#
# now answers it from our own journal, in the terms the budget is expressed in,
# and means the same thing across every upstream version. Consecutive withheld
# lines are ~one tick apart, so their run length is the outage duration.
#
# WITHHELD-ONLY, not per-run: 1440 runs/day of "ok" would bury the signal, and
# the healthy case already has a durable record — the heartbeat's mtime.
#
# Exit codes:
#   0  evaluated. Heartbeat touched iff healthy; UNHEALTHY is a normal 0 that
#      logs one "withheld:" line to stdout (journald), never stderr — it is an
#      expected condition, not a fault, and must not read as one.
#   3  CANNOT EVALUATE — misconfiguration or unreadable signal. Fails closed
#      like unhealthy, and one line on stderr names the cause.
#      ⚠️ knapper-monitor.sh has NO is-failed/--failed check, so exit 3 does not
#      itself alert. The operator-visible path is: untouched heartbeat → sync
#      gate → /up 503 → monitor check #1, which mails WITHOUT naming the cause.
#      The stderr line and the journal are where the cause lives. Adding a
#      failed-unit check to the monitor is a separate proposal.
set -eu

HEARTBEAT="${1:?usage: sync-heartbeat.sh <heartbeat-file> [vault-path]}"
VAULT_PATH="${2:-/vault}"

# Injectable for tests; env rather than positional, so tuning one does not
# require passing the others.
MAX_SYNC_AGE="${KNAPPER_MAX_SYNC_AGE:-90}"
SYSTEMCTL="${KNAPPER_SYSTEMCTL:-systemctl}"
OB="${KNAPPER_OB:-ob}"
STATE_ROOT="${KNAPPER_OB_STATE_DIR:-${HOME:-/home/knapper}/.config/obsidian-headless/sync}"
LOG_BUDGET_BYTES="${KNAPPER_LOG_BUDGET_BYTES:-1048576}"

cannot_evaluate() { echo "sync-heartbeat: cannot evaluate: $1" >&2; exit 3; }

# One line per withheld touch, on stdout. Every reason is OUR OWN words or our
# own arithmetic — never a line lifted from ob's log. Vault filenames appear in
# that log, so echoing it through would put vault CONTENT in the journal and
# re-import the anchoring hazard the matching below exists to close.
withheld() { echo "sync-heartbeat: withheld: $1"; exit 0; }

# (a) the sync unit must be running.
#     Also the reason "Disconnected from server" being overloaded is harmless:
#     ob emits it on CLEAN SHUTDOWN as well as network loss, and this check
#     short-circuits the shutdown case before the log is ever read.
"$SYSTEMCTL" is-active --quiet obsidian-headless.service \
  || withheld "obsidian-headless.service is not active"

# (b) configuration must exist, AND it names the vault whose log we read.
#     ⚠️ `ob sync-status` is a CONFIGURATION probe, not a health probe: it reads
#     a file off disk, makes no network call, and exits 0 with the unit stopped,
#     the network down, or not logged in (measured on CT 106 2026-08-12: exit 0
#     both running and stopped). Its only jobs here are catching an unconfigured
#     vault and binding the log path below to THIS vault.
STATUS=$("$OB" sync-status --path "$VAULT_PATH" --json 2>/dev/null) \
  || cannot_evaluate "ob sync-status failed for $VAULT_PATH"
VAULT_ID=$(printf '%s' "$STATUS" \
  | sed -n 's/.*"vaultId"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')
[ -n "$VAULT_ID" ] || cannot_evaluate "no vaultId in sync-status output for $VAULT_PATH"

LOG="$STATE_ROOT/$VAULT_ID/sync.log"
[ -r "$LOG" ] || cannot_evaluate "sync log not readable: $LOG"

# A zero-byte log is the logrotate window, not a broken signal: the drop-in
# uses copytruncate (ob holds the fd), so the file is briefly empty until the
# next ~30s tick. Silent unhealthy, NOT cannot-evaluate — otherwise every
# rotation fails the unit. "Has content but no connection-state line in budget"
# stays loud below, because that one means the budget is mis-sized.
LOG_BYTES=$(wc -c < "$LOG" | tr -d ' ')
[ "$LOG_BYTES" -gt 0 ] \
  || withheld "sync log is zero bytes (logrotate copytruncate window)"

# Read the tail under a BYTE budget, not a line count: bursts are dense
# (measured 67 lines in 117ms) and run ~3 lines per file, so any line constant
# is coupled to vault size.
#
# `tail -c` can cut mid-line, and the partial first line is dropped rather than
# parsed. It is NOT safe to assume a cut cannot produce a parseable line: vault
# FILENAMES appear in the log, so a note named "[draft] Fully synced.md" yields
# "[ts] Downloading Notes/[draft] Fully synced.md", and a cut landing on that
# bracket leaves a fragment matching the anchor exactly. Dropping the line makes
# that structural instead of improbable. Only drop it when a cut actually
# happened — on a log shorter than the budget the first line is a real one, and
# on a fresh deploy it may be the ONLY connection-state line.
read_tail() {
    if [ "$LOG_BYTES" -gt "$LOG_BUDGET_BYTES" ]; then
        tail -c "$LOG_BUDGET_BYTES" "$LOG" | tail -n +2
    else
        cat "$LOG"
    fi
}

# (c) the newest CONNECTION-STATE line must be healthy, and recent.
#
#     ⛔ ANCHORED at the message position, immediately after "] ". The log
#     interleaves vault filenames, so an unanchored match is steerable by vault
#     CONTENT: a note named "Fully synced.md" would forge a healthy verdict, and
#     one named "Disconnected from server.md" would block every mutation until
#     it was renamed. Same principle as --no-ignore in RipgrepRunner.BaselineArgs.
#
#     Recency alone is not enough either: measured 2026-08-13, "Fully synced"
#     survived a severed network for ~57s (two 30s ticks) before ob noticed, so
#     the disconnect lines are matched EXPLICITLY. That ~57s is ob's own
#     detection latency and bounds this whole approach — no check derived from
#     this log can beat it.
#
#     "Connection successful" means CONNECTED, not synced; the age check below
#     is what bounds how long that may stand alone.
LINE=$(read_tail \
  | grep -E '^\[[^]]*\] (Fully synced|Connection successful|Disconnected from server|Waiting to connect)' \
  | tail -1)
[ -n "$LINE" ] || cannot_evaluate "no connection-state line in last $LOG_BUDGET_BYTES bytes of $LOG"

# The two verdicts are reported separately — they are the same withheld touch,
# but "disconnected" and "still waiting to reconnect" read very differently in
# a run of journal lines. Note what is logged is the MATCHED CATEGORY, not
# $MSG: the anchor guarantees the line STARTS with one of these, not that the
# rest of it is free of vault filenames.
MSG=${LINE#*\] }
case "$MSG" in
  "Disconnected from server"*) withheld "ob reports disconnected from server" ;;
  "Waiting to connect"*)       withheld "ob reports waiting to connect to server" ;;
esac

TS=${LINE#\[}; TS=${TS%%\]*}
[ -n "$TS" ] || cannot_evaluate "unparseable timestamp in: $LINE"
EPOCH=$(date -u -d "$TS" +%s 2>/dev/null) || cannot_evaluate "date(1) could not parse: $TS"

# Backstop for a process wedged without logging. 90s = 3 missed 30s ticks.
# Total exposure ≈ 57s (ob detection floor) + ≤60s (timer period, which holds
# only because knapper-heartbeat.timer pins AccuracySec=1s — systemd's 1min
# default measured 116s gaps on CT 106) + 300s (Sync__MaxAgeSeconds). If that
# total matters, the lever is Sync__MaxAgeSeconds.
AGE=$(( $(date -u +%s) - EPOCH ))
[ "$AGE" -le "$MAX_SYNC_AGE" ] \
  || withheld "newest connection-state line is ${AGE}s old (max ${MAX_SYNC_AGE}s) — sync may be wedged without logging"

touch "$HEARTBEAT" || cannot_evaluate "could not touch $HEARTBEAT"
