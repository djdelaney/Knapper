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
# It walks BOTH directions, and the second direction is the younger half. Going
# artifact → /etc answers "did everything this release ships get installed?".
# It cannot answer "is anything in /etc that this release does NOT ship?" —
# a unit a later release stops shipping keeps running, with no counterpart to
# compare it against and so no line in the report. Same for a `.d` drop-in
# directory, which overrides the very unit the report just called identical.
# Both were invisible here until 0.5.1, and both reported as clean, which is
# the failure mode this script exists to close, arriving from the other side.
#
# Exit codes, ordered by how much they should worry you:
#
#   0  every shipped file is installed and identical, and /etc holds nothing
#      this release does not account for — nothing to do
#   1  some installed file DIFFERS, or /etc holds something ORPHANED or an
#      override drop-in. Read each one. knapper.service legitimately differs
#      forever: /etc carries THIS deployment's edits (AllowedHosts, the Access
#      AUD, Sync__MaxAgeSeconds, Sync__MaxFileBytes) and copying the shipped
#      unit over them reverts them all at once, silently, into a service that
#      still starts. Every state at this level means "a human decides", not
#      "wrong" — and nothing here is ever removed for you: /etc is
#      authoritative, and an orphan may be deliberate.
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
ORPHANED=0

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
    # Templates pass a label naming BOTH ends: the shipped file and the
    # installed file have different names there, and "knapper-smoke.service
    # .example: identical" leaves the reader to guess which unit on the box
    # that sentence is about.
    _label="${3:-$(basename "$_shipped")}"

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
        # Deliberately not phrased as "merge those". On 0.5.0 this line read
        # "13 of 24" and all thirteen were COMMENTS — the correct action was
        # to merge nothing, while the naive reading (replace the block the
        # lines sit in) would have commented out the whole Access config and
        # collapsed the deployment to a single application. The count says
        # where to look; only reading them says what to do.
        echo "    → $_offsite of $_total differing line(s) are OUTSIDE known site config — that is what"
        echo "      this release changed. Review them before merging anything: they may be comments"
        echo "      only, and the rest is yours to keep."
    fi
    printf '%s\n' "$_body"
    DIFFERS=$((DIFFERS + 1))
}

for f in "$ROOT"/ops/systemd/*.service "$ROOT"/ops/systemd/*.timer; do
    [ -f "$f" ] || continue
    compare "$f" "$SYSTEMD_DIR/$(basename "$f")"
done

# Templates (*.example) are installed BY HAND under the name minus the suffix —
# knapper-smoke.service.example → knapper-smoke.service — at §8b, and removed
# again at §9. They were excluded from this report entirely until 0.5.1, on the
# reasoning that a template has no installed counterpart and would read NOT
# INSTALLED forever. True for most of the deployment's life, and wrong exactly
# when it matters: while §8b is live, the smoke instance is the ONLY place a
# real MCP client is attached to Knapper before cutover, so its unit is the one
# gathering the evidence — and a release that changed the template said nothing.
#
# So: compare when installed, and when not installed say so as a plain fact
# with no counter behind it. NOT the MISSING state — nothing is wrong with a
# template that is not deployed, and a permanent red line is how a check stops
# being read.
for f in "$ROOT"/ops/systemd/*.example; do
    [ -f "$f" ] || continue
    _installs_as=$(basename "${f%.example}")
    if [ -e "$SYSTEMD_DIR/$_installs_as" ]; then
        compare "$f" "$SYSTEMD_DIR/$_installs_as" "$(basename "$f") → $_installs_as"
    else
        echo "=== $(basename "$f"): template, not installed as $_installs_as — nothing to compare"
    fi
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

# ── The other direction: what is in /etc that this release does not ship? ──
#
# Everything above walks artifact → /etc, and by construction it can only ever
# report on files the release knows about. A unit a LATER release stops
# shipping keeps running under systemd with nothing left to compare it to, so
# it drops out of the report entirely — and the report still ends "every
# shipped unit is installed and identical", which is true and reads as clean.
#
# Scoped to our own names: /etc/systemd/system belongs to the host, and a
# report that listed every unrelated unit as an orphan would be noise nobody
# finishes reading. Names are NOT prescriptive about removal — /etc is
# authoritative, an orphan may be a deliberate local unit, and the decision is
# the operator's.
orphan_scan() {
    _dir="$1"
    _shipped_names="$2"
    shift 2
    for _prefix in "$@"; do
        for _found in "$_dir/$_prefix"*; do
            [ -e "$_found" ] || continue
            _name=$(basename "$_found")
            printf '%s\n' "$_shipped_names" | grep -Fxq "$_name" && continue
            # A `foo.service.d/` directory is not an orphan — it is an
            # OVERRIDE of a unit this report may have just called identical,
            # and systemd applies it on top. Worth its own words: "identical
            # unit + a drop-in nobody mentioned" is a config nobody has read
            # whole.
            if [ -d "$_found" ]; then
                case "$_name" in
                    *.d)
                        echo "=== $_name: OVERRIDE DIR — drop-ins here modify ${_name%.d}, and are shipped by"
                        echo "    no file in this release. systemd applies them on top of the unit above."
                        echo "    Contents: $(ls -A "$_found" 2>/dev/null | tr '\n' ' ')"
                        ORPHANED=$((ORPHANED + 1))
                        continue ;;
                esac
            fi
            echo "=== $_name: ORPHANED — installed at $_found, shipped by no file in this release."
            echo "    A unit a later release stopped shipping keeps running until someone stops it."
            echo "    This is a report, not an instruction: /etc is authoritative and it may be"
            echo "    deliberate. If it is not, systemctl disable --now it before removing the file."
            ORPHANED=$((ORPHANED + 1))
        done
    done
}

# What the release accounts for: every shipped unit, plus each template under
# the name it installs as (so §8b's live smoke unit is not called an orphan by
# the same release that ships its template).
SHIPPED_NAMES=$(
    for f in "$ROOT"/ops/systemd/*.service "$ROOT"/ops/systemd/*.timer "$ROOT"/ops/systemd/*.example; do
        [ -f "$f" ] || continue
        _b=$(basename "$f")
        printf '%s\n' "${_b%.example}"
    done
)
orphan_scan "$SYSTEMD_DIR" "$SHIPPED_NAMES" knapper obsidian-headless

if [ -d "$ROOT/ops/logrotate" ]; then
    LOGROTATE_NAMES=$(
        for f in "$ROOT"/ops/logrotate/*; do
            [ -f "$f" ] || continue
            basename "$f"
        done
    )
    orphan_scan "$LOGROTATE_DIR" "$LOGROTATE_NAMES" knapper obsidian
fi

echo
if [ "$MISSING" -ne 0 ]; then
    echo "check-installed: $MISSING shipped file(s) NOT INSTALLED, $DIFFERS differ(s)." >&2
    echo "  A shipped file with no installed counterpart is never expected: install it," >&2
    echo "  then systemctl daemon-reload." >&2
    exit 2
fi
if [ "$DIFFERS" -ne 0 ] || [ "$ORPHANED" -ne 0 ]; then
    if [ "$DIFFERS" -ne 0 ]; then
        echo "check-installed: $DIFFERS file(s) differ — reconcile BY HAND, then systemctl daemon-reload." >&2
        echo "  /etc carries this deployment's edits; copying the shipped file over them reverts" >&2
        echo "  every one of them silently into a service that still starts." >&2
    fi
    if [ "$ORPHANED" -ne 0 ]; then
        echo "check-installed: $ORPHANED file(s) in /etc are shipped by nothing in this release." >&2
        echo "  Reported, never removed: /etc is authoritative and an orphan may be deliberate." >&2
    fi
    exit 1
fi
echo "check-installed: every shipped unit and drop-in is installed and identical,"
echo "                 and /etc holds nothing this release does not account for"
