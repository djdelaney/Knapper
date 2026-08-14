#!/bin/sh
# ops/monitor/knapper-monitor.sh — the only thing watching CT 106 from outside.
#
# History this pins (found at deployment, 2026-08-14, against v0.3.2, by
# runbook §8 drill 4):
#   * Check 1b read `jq -r '.oversized.ok // "absent"'`. jq's `//` substitutes
#     its right side for no value, `null`, AND `false` — so the boolean `false`,
#     the ONLY value that check exists to catch, arrived as the string "absent"
#     and compared unequal to "false". The alert branch was unreachable from
#     the day it shipped. Every adjacent check passed the whole time; the
#     server, /up's payload and the runbook prose were all correct.
#   * The first diagnosis of it was wrong (it blamed /health's shape, which
#     legitimately has no `ok` key). Case 1 below is the one that settles it:
#     a canned /up body with `{"ok":false}` must produce a mail.
#
# The server-side contract — that /up really answers `oversized.ok:false` for
# a real oversized file — is pinned separately by
# Knapper.Mcp.Tests.OversizedBackstopTests. It was passing throughout. This
# file covers the half nothing reached: the shell script's own reading of it.
#
# Everything the script touches outside itself is stubbed onto PATH: no curl,
# no pct, no CT, and above all no mailer — a test that could mail is a test
# that pages the operator. `sendmail` here appends to a file.
set -u

SCRIPT=$(cd "$(dirname "$0")/../.." && pwd)/ops/monitor/knapper-monitor.sh

if ! command -v jq >/dev/null 2>&1; then
    echo "   SKIP: no jq(1) on this host — check 1b is jq-gated; CI has it"
    exit 0
fi

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

FAILURES=0
CASE=""
N=0

fail() { echo "   FAIL [$CASE] $1" >&2; FAILURES=$((FAILURES + 1)); }

# Fresh tree per case. STAMP/METRICS stubs are healthy by default so that a
# failing run can only be check 1b's doing — every case here is about 1b.
setup() {
    CASE="$1"
    N=$((N + 1))
    DIR="$WORK/$N"
    mkdir -p "$DIR/bin" "$DIR/state"
    MAILBOX="$DIR/mailbox"
    : > "$MAILBOX"

    # sendmail(8): capture instead of deliver. Picked over `mail` because the
    # script prefers it, so this exercises the branch production uses.
    printf '#!/bin/sh\ncat >> "%s"\n' "$MAILBOX" > "$DIR/bin/sendmail"

    # pct exec <ct> -- stat -c %%Y <stamp>   → a fresh stamp (now)
    # pct exec <ct> -- cat <metrics>         → a snapshot with no counters moving
    printf '#!/bin/sh\ncase "$*" in\n  *stat*) date +%%s ;;\n  *cat*) printf %%s %s ;;\nesac\n' \
        "'{\"StartedAt\":\"2026-08-14T00:00:00Z\"}'" > "$DIR/bin/pct"

    for b in sendmail pct; do chmod +x "$DIR/bin/$b"; done

    cat > "$DIR/conf" <<EOF
UP_URL=https://example.invalid/up
CF_CLIENT_ID=id
CF_CLIENT_SECRET=secret
CT_ID=106
MAILTO=nobody@example.invalid
STATE_DIR=$DIR/state
EOF
}

# curl stub: honours -o <file> the way the script calls it, writes the canned
# body there and prints the canned status code on stdout, as -w '%{http_code}'
# does. $1 = body, $2 = status.
stub_curl() {
    _body=$1
    _code=$2
    printf '%s' "$_body" > "$DIR/body"
    cat > "$DIR/bin/curl" <<EOF
#!/bin/sh
_out=""
while [ \$# -gt 0 ]; do
    [ "\$1" = "-o" ] && { _out=\$2; shift; }
    shift
done
[ -n "\$_out" ] && cp "$DIR/body" "\$_out"
printf %s '$_code'
EOF
    chmod +x "$DIR/bin/curl"
}

run() {
    OUT=$(PATH="$DIR/bin:$PATH" sh "$SCRIPT" "$DIR/conf" 2>&1)
    RC=$?
    MAIL=$(cat "$MAILBOX")
}

assert_rc()      { [ "$RC" = "$1" ] || fail "exit $RC, expected $1 (output: $OUT)"; }
assert_mailed()  { case "$MAIL" in *"$1"*) ;; *) fail "no mail matching '$1' (mailbox: $MAIL)" ;; esac; }
assert_no_mail() { [ -z "$MAIL" ] || fail "expected NO mail, got: $MAIL"; }

# ── 1. ⭐ THE REGRESSION TEST ─────────────────────────────────────────────
# `{"ok":false}` is the live shape /up returns with an oversized file present
# (OversizedBackstopTests pins that end). Under `// "absent"` this case exited
# 0 and mailed nothing, which is how it survived a year of green runs.
setup "oversized present → mail"
stub_curl '{"status":"ok","oversized":{"ok":false}}' 200
run
assert_rc 1
assert_mailed "Obsidian Sync will NOT carry"

# ...and the mail must not claim the OTHER fault. A scan that could not
# complete degrades to 503 and is check 1's alert; naming it here would send
# the operator to look for a file that does not exist.
case "$MAIL" in *"could not be read"*) fail "reported an unreadable field for a legitimate false" ;; esac

# ── 2. the healthy case stays silent ──────────────────────────────────────
# The half that always worked — `true` is the one value `//` passed through
# intact, so this case looked like proof the check was live.
setup "no oversized files → silent"
stub_curl '{"status":"ok","oversized":{"ok":true}}' 200
run
assert_rc 0
assert_no_mail

# ── 3. field absent → loud, and DISTINGUISHABLE from case 1 ───────────────
# The server shape changing must not silently retire the check. Under the old
# expression this was byte-identical to case 1's input as far as the script
# could tell — which is why "just add an absent branch" would not have been a
# fix on its own.
setup "oversized.ok missing → named as blind"
stub_curl '{"status":"ok","oversized":{"scanned":true,"count":0}}' 200
run
assert_rc 1
assert_mailed "could not be read (absent)"
assert_mailed "BLIND"
case "$MAIL" in *"Obsidian Sync will NOT carry"*) fail "claimed oversized files for a missing field" ;; esac

# ── 4. an HTML body is not a healthy vault ────────────────────────────────
# The Access login page arriving as a 200 (the -L bug `knapper verify` shipped
# on 2026-08-14). jq cannot parse it; the old `//` swallowed that into
# "absent" and read it as healthy. Now it is named.
setup "unparseable body → loud"
stub_curl '<!DOCTYPE html><title>Sign in</title>' 200
run
assert_rc 1
assert_mailed "could not be read (unreadable)"

# ── 5. a non-200 does not also fire 1b ────────────────────────────────────
# 503 is check 1's alert. Two mails for one fault is how a fault gets
# misattributed; the 200 guard is what keeps 1b quiet here.
setup "503 → check 1 only"
stub_curl '{"status":"degraded","oversized":{"ok":false}}' 503
run
assert_rc 1
assert_mailed "expected 200"
case "$MAIL" in *"Obsidian Sync will NOT carry"*) fail "1b fired on a non-200; the guard is not holding" ;; esac

# ── 6. the mail never carries a value lifted from the body ────────────────
# /up is designed never to name a vault file (Up_never_names_an_oversized_file),
# and this script must not undo that by interpolating whatever it parsed into
# an alert. The three tokens are fixed strings; nothing from the body reaches
# the mailer.
setup "no body content reaches the mail"
stub_curl '{"status":"ok","oversized":{"ok":"Secret Project/roadmap.md"}}' 200
run
assert_rc 1
assert_mailed "could not be read (absent)"
case "$MAIL" in *"Secret Project"*) fail "body content leaked into the alert mail" ;; esac
case "$MAIL" in *roadmap*) fail "body content leaked into the alert mail" ;; esac

[ "$FAILURES" -eq 0 ] || exit 1
echo "   $N cases passed"
