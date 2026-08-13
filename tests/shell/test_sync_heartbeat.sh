#!/bin/sh
# ops/sync-heartbeat.sh — the gate every mutation depends on.
#
# History this pins (found at deployment, 2026-08-12/13, against v0.1.1):
#   * The original check was VACUOUS. `ob sync-status` is a CONFIGURATION probe
#     — it reads a file off disk, makes no network call, and exits 0 with the
#     unit stopped and the network down. The whole gate rested on
#     `systemctl is-active`, which cannot see running-but-not-syncing.
#   * "Fully synced" survived a SEVERED NETWORK for ~57s (two 30s ticks), so
#     recency alone is not proof — the disconnect lines are matched explicitly.
#   * The log interleaves vault FILENAMES, so an unanchored match is steerable
#     by vault content. Case "anchoring" is that one.
#
# ⚠️ Requires GNU date (-d). On macOS this SKIPS rather than fails — CI runs
# ubuntu-latest, which is where the real gate is. A skip prints loudly; do not
# read a green local run as coverage.
set -u

SCRIPT=$(cd "$(dirname "$0")/../.." && pwd)/ops/sync-heartbeat.sh
VAULT_ID=ea989b9e58b759ef7a491fcacfb7abbe
OTHER_ID=00000000000000000000000000000000

if ! date -u -d "2026-01-01T00:00:00.000Z" +%s >/dev/null 2>&1; then
    echo "   SKIP: no GNU date(1) -d on this host (macOS?) — these run in CI on linux"
    exit 0
fi

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

FAILURES=0
CASE=""
N=0

fail() { echo "   FAIL [$CASE] $1" >&2; FAILURES=$((FAILURES + 1)); }

ts_ago() { date -u -d "@$(( $(date -u +%s) - $1 ))" +%Y-%m-%dT%H:%M:%S.000Z; }

# Fresh tree per case: stub binaries, a state root, a heartbeat that does not
# exist yet. Nothing here touches the real vault or the real ob.
setup() {
    CASE="$1"
    N=$((N + 1))
    DIR="$WORK/$N"
    mkdir -p "$DIR/bin" "$DIR/state/$VAULT_ID"
    LOG="$DIR/state/$VAULT_ID/sync.log"
    HB="$DIR/heartbeat"
    stub_systemctl active
    stub_ob ok
    unset KNAPPER_MAX_SYNC_AGE KNAPPER_LOG_BUDGET_BYTES 2>/dev/null || true
}

stub_systemctl() {
    if [ "$1" = active ]; then _rc=0; else _rc=3; fi
    printf '#!/bin/sh\nexit %s\n' "$_rc" > "$DIR/bin/systemctl"
    chmod +x "$DIR/bin/systemctl"
}

# ok      → prints the real --json shape (vaultId is what binds the log path)
# fail    → nonzero, as `ob sync-status` does with no config (exit 3)
# novault → valid JSON with no vaultId
stub_ob() {
    case "$1" in
        ok)      printf '#!/bin/sh\nprintf %%s %s\n' \
                     "'{\"vaultId\":\"$VAULT_ID\",\"vaultPath\":\"/vault\"}'" > "$DIR/bin/ob" ;;
        fail)    printf '#!/bin/sh\nexit 3\n' > "$DIR/bin/ob" ;;
        novault) printf '#!/bin/sh\nprintf %%s %s\n' "'{\"vaultPath\":\"/vault\"}'" > "$DIR/bin/ob" ;;
    esac
    chmod +x "$DIR/bin/ob"
}

probe() {
    OUT=$(KNAPPER_SYSTEMCTL="$DIR/bin/systemctl" \
          KNAPPER_OB="$DIR/bin/ob" \
          KNAPPER_OB_STATE_DIR="$DIR/state" \
          sh "$SCRIPT" "$HB" /vault 2>&1)
    RC=$?
}

assert_rc()          { [ "$RC" = "$1" ] || fail "exit $RC, expected $1 (output: $OUT)"; }
assert_touched()     { [ -e "$HB" ] || fail "heartbeat NOT touched, expected touched (exit $RC: $OUT)"; }
assert_not_touched() { [ ! -e "$HB" ] || fail "heartbeat WAS touched, expected untouched"; }
assert_stderr()      { case "$OUT" in *"$1"*) ;; *) fail "stderr missing '$1' (got: $OUT)" ;; esac; }

# Every withheld touch must say so, and say why. Without this the healthy and
# unhealthy runs are byte-identical in the journal and the deployment keeps no
# record of how close it has come to its fail-closed limit — the calibration
# question then has to be answered by parsing ob's log instead of ours.
assert_withheld() {
    case "$OUT" in
        *withheld:*"$1"*) ;;
        *) fail "no withheld line mentioning '$1' (got: $OUT)" ;;
    esac
}
assert_silent() { [ -z "$OUT" ] || fail "expected no output, got: $OUT"; }

# ── 1. healthy ────────────────────────────────────────────────────────────
setup "healthy"
printf '[%s] Fully synced\n' "$(ts_ago 5)" > "$LOG"
probe; assert_rc 0; assert_touched; assert_silent

# ── 2. unit inactive → untouched, and says so (condition (a)) ─────────────
setup "unit inactive"
stub_systemctl inactive
printf '[%s] Fully synced\n' "$(ts_ago 5)" > "$LOG"
probe; assert_rc 0; assert_not_touched; assert_withheld "obsidian-headless.service is not active"

# ── 3. ⭐ THE REGRESSION TEST ─────────────────────────────────────────────
# Unit active AND `ob sync-status` exits 0 — the exact state the original
# script called healthy — while the newest connection-state line says the
# server is gone. Fixture is the real severed-network window from CT 106
# (00:00:26 nft block → 00:01:23 disconnect detected), timestamps rebased.
setup "severed network, unit still active"
{
    printf '[%s] Fully synced\n'             "$(ts_ago 62)"
    printf '[%s] Fully synced\n'             "$(ts_ago 32)"
    printf '[%s] Disconnected from server\n' "$(ts_ago 32)"
    printf '[%s] Waiting to connect to server\n' "$(ts_ago 31)"
} > "$LOG"
# The newest connection-state line here is the "Waiting to connect" one, so
# that is the verdict logged — the two are reported separately because a run of
# them reads very differently in the journal.
probe; assert_rc 0; assert_not_touched; assert_withheld "waiting to connect"

# ── 4. stale: healthy line, too old ───────────────────────────────────────
# The age is in the line: a run of these is how the deployment's real worst
# case gets measured without parsing anyone else's log.
setup "stale Fully synced"
printf '[%s] Fully synced\n' "$(ts_ago 600)" > "$LOG"
probe; assert_rc 0; assert_not_touched; assert_withheld "sync may be wedged"

# ── 5. window sizing: a bulk burst INSIDE the budget still evaluates ──────
# ~700 activity lines after the last connection-state line — the measured
# shape of the 234-note initial pull (~3 lines per file, one trailing
# "Fully synced"). Guards the byte budget, NOT the age check.
setup "bulk burst inside budget"
printf '[%s] Fully synced\n' "$(ts_ago 5)" > "$LOG"
i=0
while [ "$i" -lt 700 ]; do
    printf '[%s] Downloading Notes/note-%s.md\n' "$(ts_ago 4)" "$i" >> "$LOG"
    i=$((i + 1))
done
probe; assert_rc 0; assert_touched

# ── 6. burst EXCEEDS the budget → loud, not silent ────────────────────────
setup "bulk burst exceeds budget"
KNAPPER_LOG_BUDGET_BYTES=2000; export KNAPPER_LOG_BUDGET_BYTES
printf '[%s] Fully synced\n' "$(ts_ago 5)" > "$LOG"
i=0
while [ "$i" -lt 200 ]; do
    printf '[%s] Downloading Notes/note-%s.md\n' "$(ts_ago 4)" "$i" >> "$LOG"
    i=$((i + 1))
done
probe; assert_rc 3; assert_not_touched; assert_stderr "no connection-state line"
unset KNAPPER_LOG_BUDGET_BYTES

# ── 7. vaultId binding: the OTHER vault's log is newer and must be ignored ─
setup "two vaults, other one newer"
mkdir -p "$DIR/state/$OTHER_ID"
printf '[%s] Disconnected from server\n' "$(ts_ago 5)" > "$LOG"
printf '[%s] Fully synced\n' "$(ts_ago 1)" > "$DIR/state/$OTHER_ID/sync.log"
probe; assert_rc 0; assert_not_touched

# ── 8. ⭐ ANCHORING: vault content must not steer the gate ────────────────
# A note named "Fully synced.md" appears in a Downloading line as the NEWEST
# line, while the newest real connection-state line says Disconnected.
# Unanchored matching reads the filename as health and touches the heartbeat.
setup "filename cannot forge a healthy verdict"
{
    printf '[%s] Disconnected from server\n'            "$(ts_ago 40)"
    printf '[%s] Downloading Notes/Fully synced.md\n'   "$(ts_ago 2)"
} > "$LOG"
probe; assert_rc 0; assert_not_touched; assert_withheld "disconnected from server"
# ...and the withheld line must not carry the FILENAME through into our
# journal. The anchor guarantees the matched line starts with a known message,
# not that the rest of it is free of vault content — so the log line reports
# the matched category, never the raw text.
case "$OUT" in *"Fully synced.md"*) fail "vault filename leaked into the withheld line: $OUT" ;; esac

# ...and the inverse: a note named "Disconnected from server.md" must not
# block a healthy vault.
setup "filename cannot forge an unhealthy verdict"
{
    printf '[%s] Fully synced\n'                                "$(ts_ago 10)"
    printf '[%s] Downloading Notes/Disconnected from server.md\n' "$(ts_ago 2)"
} > "$LOG"
probe; assert_rc 0; assert_touched

# ── 9. the partial line a byte cut leaves behind ──────────────────────────
# Budget is sized so `tail -c` cuts exactly at the "[" of a note named
# "[<iso8601>] Fully synced.md". The fragment is a syntactically perfect,
# RECENT, healthy-looking line. Dropping the first line after a cut is what
# makes this structural rather than improbable; without it this case touches
# the heartbeat off a filename.
setup "partial first line after a byte cut"
SUFFIX="[$(ts_ago 2)] Fully synced.md
"
PREFIX="[$(ts_ago 90)] Disconnected from server
[$(ts_ago 3)] Downloading Notes/"
printf '%s%s' "$PREFIX" "$SUFFIX" > "$LOG"
KNAPPER_LOG_BUDGET_BYTES=$(printf '%s' "$SUFFIX" | wc -c | tr -d ' ')
export KNAPPER_LOG_BUDGET_BYTES
probe; assert_rc 3; assert_not_touched
unset KNAPPER_LOG_BUDGET_BYTES

# ── 10. a short log keeps its first line (fresh deploy) ───────────────────
# The drop must be conditional on a cut having happened: right after §4 the
# only connection-state line in the file IS the first one.
setup "single-line log on a fresh deploy"
printf '[%s] Fully synced\n' "$(ts_ago 3)" > "$LOG"
probe; assert_rc 0; assert_touched

# ── 11. zero-byte log = the logrotate window, silent not loud ─────────────
# copytruncate leaves the file empty until the next ~30s tick. Failing the
# unit on every rotation would be noise; a mis-sized budget (case 6) stays loud.
setup "zero-byte log during rotation"
: > "$LOG"
probe; assert_rc 0; assert_not_touched; assert_withheld "logrotate copytruncate window"

# ── 12. cannot-evaluate paths are loud and named ──────────────────────────
setup "ob sync-status fails"
stub_ob fail
probe; assert_rc 3; assert_not_touched; assert_stderr "ob sync-status failed"

setup "no vaultId in sync-status output"
stub_ob novault
probe; assert_rc 3; assert_not_touched; assert_stderr "no vaultId"

setup "log missing"
rm -rf "$DIR/state/$VAULT_ID"
probe; assert_rc 3; assert_not_touched; assert_stderr "sync log not readable"

setup "unparseable timestamp"
printf '[not-a-date] Fully synced\n' > "$LOG"
probe; assert_rc 3; assert_not_touched; assert_stderr "could not parse"

setup "heartbeat unwritable"
printf '[%s] Fully synced\n' "$(ts_ago 5)" > "$LOG"
mkdir -p "$DIR/ro" && HB="$DIR/ro/sub/heartbeat"
probe; assert_rc 3; assert_stderr "could not touch"

[ "$FAILURES" -eq 0 ] || exit 1
echo "   $N cases passed"
