#!/bin/sh
# Tests for ops/check-installed.sh — the deploy-time reconciliation report.
#
# What is worth testing here is not the diffing (that is diff's job) but the
# two claims the script makes that a human would otherwise have to trust:
# that the set of files checked is DERIVED from what shipped rather than
# enumerated, and that the three states are distinguishable by exit code. The
# enumerated list this replaces was wrong the first time it was run for real,
# and it was wrong silently — the omitted units happened to match.
set -u

SCRIPT="$(cd "$(dirname "$0")/../.." && pwd)/ops/check-installed.sh"
FAILURES=0
TMPROOT=$(mktemp -d)
trap 'rm -rf "$TMPROOT"' EXIT

fail() {
    echo "   FAIL: $1" >&2
    FAILURES=$((FAILURES + 1))
}

# A fake unpacked artifact + a fake /etc. Sets $SHIPPED, $ETC, $LOGROTATE.
new_case() {
    CASE="$TMPROOT/$1"
    SHIPPED="$CASE/opt/ops/systemd"
    LOGROTATE_SRC="$CASE/opt/ops/logrotate"
    ETC="$CASE/etc/systemd/system"
    LOGROTATE="$CASE/etc/logrotate.d"
    mkdir -p "$SHIPPED" "$LOGROTATE_SRC" "$ETC" "$LOGROTATE"
    ROOT="$CASE/opt"
}

run_check() {
    "$SCRIPT" --root "$ROOT" --systemd-dir "$ETC" --logrotate-dir "$LOGROTATE" >"$CASE/out" 2>&1
    STATUS=$?
    OUT=$(cat "$CASE/out")
}

# ---- 1. everything installed and identical → exit 0 ----------------------
new_case identical
printf 'a\n' > "$SHIPPED/knapper.service"
printf 'b\n' > "$SHIPPED/knapper-commit.timer"
printf 'c\n' > "$LOGROTATE_SRC/knapper-sync-log"
cp "$SHIPPED/knapper.service" "$ETC/knapper.service"
cp "$SHIPPED/knapper-commit.timer" "$ETC/knapper-commit.timer"
cp "$LOGROTATE_SRC/knapper-sync-log" "$LOGROTATE/knapper-sync-log"
run_check
[ "$STATUS" -eq 0 ] || fail "identical tree exited $STATUS, expected 0"
printf '%s' "$OUT" | grep -q 'knapper.service: identical' || fail "identical unit not reported as identical"
# The logrotate drop-in is checked too. It looks inert next to the units and is
# not: losing copytruncate starves the heartbeat, which blocks every mutation.
printf '%s' "$OUT" | grep -q 'knapper-sync-log: identical' || fail "logrotate drop-in was not checked"

# ---- 2. an installed file that differs → exit 1, and the diff is printed --
new_case differs
printf 'Environment=Mcp__AllowedHosts__0=mcp.example.com\n' > "$SHIPPED/knapper.service"
printf 'Environment=Mcp__AllowedHosts__0=real.example.com\n' > "$ETC/knapper.service"
run_check
[ "$STATUS" -eq 1 ] || fail "a differing unit exited $STATUS, expected 1"
printf '%s' "$OUT" | grep -q 'knapper.service: DIFFERS' || fail "a differing unit was not reported"
# The diff itself, not just the verdict: reconciliation is by hand, and a
# verdict with no diff sends the operator to run diff themselves.
printf '%s' "$OUT" | grep -q 'real.example.com' || fail "DIFFERS did not print the diff"

# ---- 2b. the diff says what it MEANS, above the diff ---------------------
# The orientation is right, but -/+ reads as removed/added to anyone not
# parsing the file headers, and the expensive misreading is the mirror of the
# one the DIFFERS state exists to prevent: reading
# `+Environment=Mcp__AllowedHosts__0=mcp.example.com` as "the release wants
# this, apply it" reverts the deployment's own hostname to the placeholder.
printf '%s' "$OUT" | grep -q "'-' = what is RUNNING here" \
    || fail "no plain-language orientation line above the diff"
printf '%s' "$OUT" | grep -q "NOT an instruction to apply it" \
    || fail "the report does not say a '+' line is not an instruction"
# The case above is PURE site config, and saying so is the point: the operator
# needs "expected, keep yours" before reading a single -/+ line.
printf '%s' "$OUT" | grep -q 'known site config — expected' \
    || fail "an all-site-config diff was not classified as expected"

# ---- 2b-ii. the SUMMARY distinguishes the expected state -----------------
# Exit 1 is permanent for a configured deployment — knapper.service differs
# forever, and making it identical would mean the hostname and Access AUD
# living in the repo. So the exit code cannot separate "the expected two
# files" from "something changed", and the summary line is the only thing
# that can.
printf '%s' "$OUT" | grep -q 'Nothing UNCLASSIFIED' \
    || fail "an all-site-config run did not say so in the summary"
# ⚠️ UNCLASSIFIED, never "nothing to do". The stronger phrasing would be a
# safety conclusion resting on SITE_KEYS being complete — the exact trust the
# exit code deliberately withholds from it. This says what the classifier
# recognised, not that the recognised lines are harmless.
printf '%s' "$OUT" | grep -qiE 'nothing (to do|needs a decision)' \
    && fail "the summary certified the diff as safe rather than as classified"

# ---- 2c. a real release change is called out as the thing to merge -------
# The other half, and the half that must never be swallowed: a key the site
# list does not know lands in "OUTSIDE known site config", which is the
# instruction to merge. Note the classification only ever moves lines TOWARD
# more reading — an incomplete SITE_KEYS costs attention, never safety.
new_case differs_offsite
printf 'Environment=Mcp__AllowedHosts__0=mcp.example.com\nEnvironment=Knapper__NewKnob=true\n' \
    > "$SHIPPED/knapper.service"
printf 'Environment=Mcp__AllowedHosts__0=real.example.com\n' > "$ETC/knapper.service"
run_check
[ "$STATUS" -eq 1 ] || fail "a mixed diff exited $STATUS, expected 1"
printf '%s' "$OUT" | grep -q 'OUTSIDE known site config' \
    || fail "a release change outside site config was not called out"
# Counted, not merely mentioned — "1 of 3" is what tells the operator how much
# of the diff below is theirs to keep.
printf '%s' "$OUT" | grep -qE '1 of [0-9]+ differing line' \
    || fail "the off-site line count was not reported"
# And the summary says the opposite of the calm one — same exit code, and the
# only place the two runs read differently.
printf '%s' "$OUT" | grep -q 'OUTSIDE known' || fail "the summary did not flag the unclassified line"
printf '%s' "$OUT" | grep -q 'Nothing UNCLASSIFIED' \
    && fail "a run with an unclassified line claimed nothing was unclassified"

# ---- 3. a shipped file that was never installed → exit 2 -----------------
# The failure this script exists for. A release that adds a unit installs
# nothing by itself, and the omission is invisible until the thing the unit was
# for does not happen.
new_case missing
printf 'a\n' > "$SHIPPED/knapper.service"
cp "$SHIPPED/knapper.service" "$ETC/knapper.service"
printf 'new\n' > "$SHIPPED/knapper-newthing.timer"
run_check
[ "$STATUS" -eq 2 ] || fail "an uninstalled shipped unit exited $STATUS, expected 2"
printf '%s' "$OUT" | grep -q 'knapper-newthing.timer: NOT INSTALLED' || fail "uninstalled unit not reported"

# ---- 4. NOT INSTALLED outranks DIFFERS ----------------------------------
new_case missing_and_differs
printf 'a\n' > "$SHIPPED/knapper.service"
printf 'z\n' > "$ETC/knapper.service"
printf 'new\n' > "$SHIPPED/knapper-newthing.timer"
run_check
[ "$STATUS" -eq 2 ] || fail "missing+differing exited $STATUS, expected 2 (missing outranks)"

# ---- 5. an UNINSTALLED .example template is a fact, not a failure --------
# knapper-smoke.service.example installs by hand as knapper-smoke.service at
# §8b and is gone by §9, so "not installed" is its normal state for most of the
# deployment's life. It must be SAID (silence is what hid blindspot 5b below)
# without counting as MISSING — a permanent red line is how a check stops being
# read.
new_case example_not_installed
printf 'a\n' > "$SHIPPED/knapper.service"
cp "$SHIPPED/knapper.service" "$ETC/knapper.service"
printf 'template\n' > "$SHIPPED/knapper-smoke.service.example"
run_check
[ "$STATUS" -eq 0 ] || fail "an uninstalled template exited $STATUS, expected 0"
printf '%s' "$OUT" | grep -q 'knapper-smoke.service.example: template, not installed' \
    || fail "an uninstalled template was not reported as such"
printf '%s' "$OUT" | grep -q 'NOT INSTALLED' && fail "an uninstalled template was counted as MISSING"

# ---- 5b. an INSTALLED .example template is compared ----------------------
# The blindspot this replaces: the template was excluded from the report
# outright, so while §8b was live — the only pre-cutover window in which a real
# MCP client is attached to Knapper — a release that changed the smoke unit was
# reported by nothing, and the §8b evidence was being gathered against a
# configuration no check had looked at.
new_case example_installed
printf 'a\n' > "$SHIPPED/knapper.service"
cp "$SHIPPED/knapper.service" "$ETC/knapper.service"
printf 'Environment=Mcp__Access__Audience=PLACEHOLDER\nExecStart=/opt/knapper/mcp/Knapper.Mcp\n' \
    > "$SHIPPED/knapper-smoke.service.example"
printf 'Environment=Mcp__Access__Audience=real-aud\nExecStart=/opt/knapper/mcp/Knapper.Mcp\n' \
    > "$ETC/knapper-smoke.service"
run_check
[ "$STATUS" -eq 1 ] || fail "an installed template that differs exited $STATUS, expected 1"
# The label names BOTH ends: the shipped file and the installed file have
# different names here, and one name alone leaves the reader guessing which
# unit on the box the verdict is about.
printf '%s' "$OUT" | grep -q 'knapper-smoke.service.example → knapper-smoke.service: DIFFERS' \
    || fail "an installed template was not compared, or the label names only one end"
# It is a unit like any other once installed: the site-config protection that
# keeps a deployment from reverting its own Access AUD applies here too.
printf '%s' "$OUT" | grep -q 'known site config — expected' \
    || fail "the installed template lost the site-config classification"

# ---- 5c. an installed template is NOT then reported as an orphan ---------
# The two fixes have to agree: the smoke unit has no same-named file in the
# artifact, so the /etc-side walk would call it orphaned while the template
# walk was busy comparing it. Two lines about one file, one of them wrong.
printf '%s' "$OUT" | grep -q 'knapper-smoke.service: ORPHANED' \
    && fail "the file the template installs as was also reported as an orphan"

# ---- 6. the file set is DERIVED, not enumerated --------------------------
# The whole point. A unit nobody wrote into any list must still be checked —
# that is what makes this different from the runbook prose it replaces.
new_case derived
printf 'a\n' > "$SHIPPED/knapper.service"
cp "$SHIPPED/knapper.service" "$ETC/knapper.service"
printf 'invented\n' > "$SHIPPED/some-unit-nobody-listed.service"
cp "$SHIPPED/some-unit-nobody-listed.service" "$ETC/some-unit-nobody-listed.service"
run_check
[ "$STATUS" -eq 0 ] || fail "derived case exited $STATUS, expected 0"
printf '%s' "$OUT" | grep -q 'some-unit-nobody-listed.service: identical' \
    || fail "a unit no list names was not checked — the set is not derived"

# ---- 6b. a unit in /etc that the release does not ship is ORPHANED -------
# The other direction, and the half that was missing until 0.5.1: walking
# artifact → /etc can only ever report files the release knows about, so a unit
# a later release stopped shipping keeps running with no line in the report —
# under a summary that still reads "every shipped unit is installed and
# identical", which is true and reads as clean.
new_case orphaned
printf 'a\n' > "$SHIPPED/knapper.service"
cp "$SHIPPED/knapper.service" "$ETC/knapper.service"
printf 'retired\n' > "$ETC/knapper-oldthing.timer"
run_check
[ "$STATUS" -eq 1 ] || fail "an orphaned unit exited $STATUS, expected 1"
printf '%s' "$OUT" | grep -q 'knapper-oldthing.timer: ORPHANED' || fail "an orphaned unit was not reported"
# Reported, never prescribed: /etc is authoritative and the orphan may be
# deliberate. The script must not tell an operator to delete it.
printf '%s' "$OUT" | grep -q 'not an instruction' || fail "the orphan line reads as an instruction to remove"

# ---- 6b-ii. an outstanding orphan withholds the reassuring summary -------
# "The expected state of a configured deployment" is a sentence about the whole
# box, and it is not true while something in /etc is shipped by nothing — even
# when every differing LINE is classified. The two findings are independent and
# the summary must not let one speak for the other.
new_case orphan_with_site_config_diff
printf 'Environment=Mcp__AllowedHosts__0=mcp.example.com\n' > "$SHIPPED/knapper.service"
printf 'Environment=Mcp__AllowedHosts__0=real.example.com\n' > "$ETC/knapper.service"
printf 'retired\n' > "$ETC/knapper-oldthing.timer"
run_check
[ "$STATUS" -eq 1 ] || fail "site-config diff + orphan exited $STATUS, expected 1"
printf '%s' "$OUT" | grep -q 'Nothing UNCLASSIFIED' \
    && fail "an outstanding orphan still got the all-clear summary"
printf '%s' "$OUT" | grep -q 'none with line(s) outside known site config' \
    || fail "the classification result was lost when an orphan was present"
printf '%s' "$OUT" | grep -q 'those are the open items' \
    || fail "the summary did not point at the orphan as the open item"

# ---- 6c. the orphan walk stays inside our own names ----------------------
# /etc/systemd/system belongs to the host. A report that listed every unrelated
# unit would be noise nobody finishes reading — and a check nobody finishes is
# the same as no check.
new_case orphan_scope
printf 'a\n' > "$SHIPPED/knapper.service"
cp "$SHIPPED/knapper.service" "$ETC/knapper.service"
printf 'someone elses\n' > "$ETC/postgresql.service"
run_check
[ "$STATUS" -eq 0 ] || fail "an unrelated host unit exited $STATUS, expected 0"
printf '%s' "$OUT" | grep -q 'postgresql' && fail "an unrelated host unit was reported as an orphan"

# ---- 6d. a drop-in directory overriding a shipped unit is reported -------
# The subtlest of the set: knapper.service reports identical, and systemd is
# running it with an override the report never mentioned. "Identical unit + a
# drop-in nobody mentioned" is a configuration nobody has read whole.
new_case dropin
printf 'a\n' > "$SHIPPED/knapper.service"
cp "$SHIPPED/knapper.service" "$ETC/knapper.service"
mkdir -p "$ETC/knapper.service.d"
printf '[Service]\nEnvironment=Sync__MaxAgeSeconds=99999\n' > "$ETC/knapper.service.d/override.conf"
run_check
[ "$STATUS" -eq 1 ] || fail "an override drop-in exited $STATUS, expected 1"
printf '%s' "$OUT" | grep -q 'knapper.service.d: OVERRIDE DIR' || fail "an override drop-in was not reported"
printf '%s' "$OUT" | grep -q 'override.conf' || fail "the drop-in's contents were not named"
# And the unit it overrides is still reported on its own terms.
printf '%s' "$OUT" | grep -q 'knapper.service: identical' || fail "the overridden unit lost its own line"

# ---- 6e. an orphaned logrotate drop-in is reported too -------------------
new_case orphan_logrotate
printf 'a\n' > "$SHIPPED/knapper.service"
cp "$SHIPPED/knapper.service" "$ETC/knapper.service"
printf 'c\n' > "$LOGROTATE_SRC/knapper-sync-log"
cp "$LOGROTATE_SRC/knapper-sync-log" "$LOGROTATE/knapper-sync-log"
printf 'retired\n' > "$LOGROTATE/knapper-oldlog"
run_check
[ "$STATUS" -eq 1 ] || fail "an orphaned logrotate drop-in exited $STATUS, expected 1"
printf '%s' "$OUT" | grep -q 'knapper-oldlog: ORPHANED' || fail "an orphaned logrotate drop-in was not reported"

# ---- 7. a missing source tree refuses rather than reporting a clean run --
# Every file would otherwise read as "shipped: none", and a report over an
# empty set is a green run that looked at nothing.
new_case no_artifact
rm -rf "$ROOT/ops/systemd"
run_check
[ "$STATUS" -eq 2 ] || fail "a missing artifact exited $STATUS, expected 2"

if [ "$FAILURES" -ne 0 ]; then
    echo "check-installed: $FAILURES assertion(s) failed" >&2
    exit 1
fi
exit 0
