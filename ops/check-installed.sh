#!/bin/sh
# Does /etc still match what shipped? Runs INSIDE CT 106, from the unpacked
# artifact:
#
#   pct exec 106 -- sh /opt/knapper/ops/check-installed.sh
#
# The deploy-time twin of publish.sh's coverage gate. That gate closed one half
# of this failure class at BUILD time — a file in the repo that never reaches
# the archive. This closes the other half at DEPLOY time: a file in the archive
# that never reaches /etc, or one in /etc that no longer matches the release
# just installed. Both halves are the same bug — a hand-maintained list that
# must agree with the set of files that ship, with nothing enforcing it.
#
# It shipped as an enumerated list in the runbook and was wrong the first time
# it was run for real (2026-08-13): six units ship, four were named. The two
# omissions happened to be byte-identical in that release, which is luck, not
# process. So the set is DERIVED here — from the files present in the artifact,
# never from a list anyone has to remember to update.
#
# Exit codes, ordered by how much they should worry you:
#
#   0  every shipped file is installed and identical — nothing to do
#   1  some installed file DIFFERS. Read each diff. knapper.service legitimately
#      differs forever: /etc carries THIS deployment's edits (AllowedHosts, the
#      Access AUD, Sync__MaxAgeSeconds, Sync__MaxFileBytes) and copying the
#      shipped unit over them reverts them all at once, silently, into a service
#      that still starts. DIFFERS means "a human decides", not "wrong".
#   2  something shipped is NOT INSTALLED, or this script could not do its job.
#      This is the one that is never expected: a release that adds a unit
#      installs nothing by itself, and the missing unit is invisible until the
#      thing it was for does not happen.
#
# 2 outranks 1 when both occur.
#
# Deliberately NOT automatic: it reports and exits, it never copies. Reconciling
# a diff means merging this deployment's edits with the release's, and a script
# that guessed would be the exact accident the runbook's "reconcile BY HAND"
# warning exists to prevent.
set -u

ROOT=/opt/knapper
SYSTEMD_DIR=/etc/systemd/system
LOGROTATE_DIR=/etc/logrotate.d

while [ $# -gt 0 ]; do
    case "$1" in
        --root) ROOT="${2:-}"; shift 2 ;;
        --systemd-dir) SYSTEMD_DIR="${2:-}"; shift 2 ;;
        --logrotate-dir) LOGROTATE_DIR="${2:-}"; shift 2 ;;
        -h|--help)
            echo "usage: check-installed.sh [--root DIR] [--systemd-dir DIR] [--logrotate-dir DIR]"
            exit 0 ;;
        *) echo "check-installed: unknown argument '$1'" >&2; exit 2 ;;
    esac
done

# A source directory that is not there means the artifact is not unpacked where
# this thinks it is — and every file would then read as "shipped: none", which
# is a clean run reporting nothing. Refuse instead.
if [ ! -d "$ROOT/ops/systemd" ]; then
    echo "check-installed: $ROOT/ops/systemd does not exist — is the artifact unpacked at $ROOT?" >&2
    exit 2
fi

DIFFERS=0
MISSING=0

# Settings /etc is authoritative for: a deployment fills these in and the
# shipped unit carries a placeholder or a default. Used ONLY to classify the
# lines of a diff, never to suppress one.
#
# ⚠️ This list is allowed to be incomplete, and the direction of its
# incompleteness is the whole reason it is safe: a key missing from it lands in
# "OUTSIDE known site config — read this", which is MORE reading, never less.
# It must never grow a rule that moves a line the other way. Note this is not
# the hand-maintained-list failure this script exists to close — that one is a
# list of FILES that must agree with what ships, where an omission means a file
# is never looked at. Here an omission means a line is looked at harder.
SITE_KEYS='Mcp__AllowedHosts__|Mcp__Access__|Sync__MaxAgeSeconds|Sync__MaxFileBytes|Vault__RootPath|Vault__LockDirectory|Vault__AuditLogPath|Vault__MetricsPath|Vault__CommitStampPath'

# Compare one shipped file against its installed counterpart. Reports every
# file, including the identical ones: the value of this script is the claim
# "every shipped file was looked at", and a report that prints only problems
# cannot distinguish "checked, fine" from "never checked".
compare() {
    _shipped="$1"
    _installed="$2"
    _label=$(basename "$_shipped")

    if [ ! -e "$_installed" ]; then
        echo "=== $_label: NOT INSTALLED  ($_installed)"
        MISSING=$((MISSING + 1))
        return
    fi
    if diff -q "$_installed" "$_shipped" >/dev/null 2>&1; then
        echo "=== $_label: identical"
        return
    fi
    # ── Say what the diff MEANS before showing it ──────────────────────────
    #
    # The orientation is right (installed on the left, shipped on the right),
    # but `-`/`+` reads as removed/added to anyone not parsing the file
    # headers, and the expensive misreading is
    #
    #   +Environment=Mcp__AllowedHosts__0=mcp.example.com
    #     → "the new release wants this, I should apply it"
    #
    # which is how a deployment reverts its own hostname to the shipped
    # placeholder — the exact silent revert the DIFFERS state exists to
    # prevent, arriving through the report that was supposed to prevent it.
    # So the legend goes in plain language, above the diff, at the moment it is
    # most expensive to get wrong.
    echo "=== $_label: DIFFERS"
    _body=$(diff -u "$_installed" "$_shipped")
    # `tail -n +3` drops diff's own --- / +++ file headers by POSITION. Matching
    # them by prefix would also eat a removed content line beginning "-- ",
    # which renders as "--- " and is indistinguishable from a header.
    _changed=$(printf '%s\n' "$_body" | tail -n +3 | grep -E '^[-+]')
    _total=$(printf '%s\n' "$_changed" | grep -c '[^[:space:]]')
    _offsite=$(printf '%s\n' "$_changed" | grep -vE "$SITE_KEYS" | grep -c '[^[:space:]]')
    echo "    '-' = what is RUNNING here      ($_installed)"
    echo "    '+' = what this release SHIPS   ($_shipped)"
    echo "    A '+' line is NOT an instruction to apply it. /etc is authoritative for this"
    echo "    deployment's site config, and copying a shipped placeholder over a real value"
    echo "    reverts it silently into a service that still starts."
    if [ "$_offsite" -eq 0 ]; then
        echo "    → all $_total differing line(s) are known site config — expected; keep what /etc has."
    else
        echo "    → $_offsite of $_total differing line(s) are OUTSIDE known site config. THAT is what"
        echo "      this release changed: merge those into /etc and leave the rest alone."
    fi
    printf '%s\n' "$_body"
    DIFFERS=$((DIFFERS + 1))
}

# *.service and *.timer only — knapper-smoke.service.example is a template with
# placeholders in it, installed by hand under a different name at §8b and gone
# again by §9, so it has no installed counterpart to compare against and would
# report NOT INSTALLED forever.
for f in "$ROOT"/ops/systemd/*.service "$ROOT"/ops/systemd/*.timer; do
    [ -f "$f" ] || continue
    compare "$f" "$SYSTEMD_DIR/$(basename "$f")"
done

# The logrotate drop-in is in here for the same reason the timers are in the
# runbook's diff list: it looks inert and it is not. ob holds sync.log open, so
# a drop-in that loses `copytruncate` leaves ob writing to the rotated inode
# while sync.log stays empty — and the heartbeat starves, which blocks every
# mutation.
if [ -d "$ROOT/ops/logrotate" ]; then
    for f in "$ROOT"/ops/logrotate/*; do
        [ -f "$f" ] || continue
        compare "$f" "$LOGROTATE_DIR/$(basename "$f")"
    done
fi

echo
if [ "$MISSING" -ne 0 ]; then
    echo "check-installed: $MISSING shipped file(s) NOT INSTALLED, $DIFFERS differ(s)." >&2
    echo "  A shipped file with no installed counterpart is never expected: install it," >&2
    echo "  then systemctl daemon-reload." >&2
    exit 2
fi
if [ "$DIFFERS" -ne 0 ]; then
    echo "check-installed: $DIFFERS file(s) differ — reconcile BY HAND, then systemctl daemon-reload." >&2
    echo "  /etc carries this deployment's edits; copying the shipped file over them reverts" >&2
    echo "  every one of them silently into a service that still starts." >&2
    exit 1
fi
echo "check-installed: every shipped unit and drop-in is installed and identical"
