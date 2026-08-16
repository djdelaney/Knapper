#!/bin/sh
# Mechanical checks on ops/ct106-runbook.md. Six review rounds found defects a
# careful human read had missed, and the last two rounds' worst findings were
# mechanical in nature: a fact corrected in one of the two places it lived, a
# section reference to a section that did not exist yet, an identity check with
# no expected value. Prose review does not catch those reliably. This does.
#
# What it CANNOT check is whether the procedure is right — that stays a human
# job. It checks that the document is internally consistent with itself.
#
#   ops/runbook-lint.sh          # exit 0 = clean; every failure prints a line
set -u

cd "$(dirname "$0")/.."
DOC=ops/ct106-runbook.md
FAILURES=0

fail() {
    echo "runbook-lint: $1" >&2
    FAILURES=$((FAILURES + 1))
}

# ---- 1. every shell block parses ----------------------------------------
# Placeholders (<lan-resolver>, 9<nn>, …) are not shell, so substitute them
# first — the target is syntax, not runnability.
BLOCKS=$(mktemp)
awk '/^```sh/{f=1;next} /^```/{f=0} f' "$DOC" | sed -E 's/<[^ >]*>/PLACEHOLDER/g' > "$BLOCKS"
sh -n "$BLOCKS" 2>/dev/null || fail "a fenced sh block does not parse (run: sh -n on the extracted blocks)"
rm -f "$BLOCKS"

# ---- 2. every §N cross-reference resolves -------------------------------
# "brief §N" points at the requirements document and is out of scope; a bare
# §N must match a heading in THIS file. Round four found a drill referring to
# a section whose state did not exist yet; this catches the cheaper cousin,
# a reference to a section that does not exist at all.
SECTIONS=$(grep -oE '^## [0-9]+b?\.' "$DOC" | sed 's/^## //; s/\.$//')
REFS=$(sed -E "s/brief('s)? §[0-9][0-9.]*[a-z]*//g; s/§[0-9]+(\.[0-9]+)? traps//g" "$DOC" \
    | grep -oE '§[0-9]+(\.[0-9]+)?b?' | sed 's/§//' | sort -u)
for ref in $REFS; do
    base=$(printf '%s' "$ref" | sed 's/\..*//')
    printf '%s\n' "$SECTIONS" | grep -qx "$base" \
        || fail "§$ref is referenced but there is no '## $base.' heading"
done

# ---- 3. identities that must never be conflated -------------------------
# Round five and six both found the /up monitoring token and the root app's
# token treated as one thing — the second time in the closing checklist,
# ninety lines from the corrected prose. The rule: no single line may tell the
# operator to write a token into the monitor's config AND into a client.
# Table rows are the identity map itself, where naming both IS the point; the
# failure this guards against was an instruction, in a checklist item.
grep -nE 'knapper-monitor\.conf' "$DOC" | grep -v '|' | grep -iE 'claude code|connector' \
    | grep -viE 'unaffected|holds the root|is not|never' \
    && fail "a line ties /etc/knapper-monitor.conf to Claude Code — those are different Access apps (§6.2 creates both: the root app vs the path-scoped /up app)"

# ---- 4. placeholders are declared ---------------------------------------
# Every <thing> in the document should be substitutable and known. A new
# placeholder is fine; adding it here is the cost of admission, and the point
# is that a deployment can key on the list.
KNOWN='lan-resolver|backup-storage|parent-dataset|rootfs-dataset|archive|fresh-archive|storage|nn|PINNED|v|token|secret|monitor-token|monitor-secret|smoke-hostname|smoke-access-app-aud|ct-address|term|ref|vmid|scratch-vmid|vaultId|pid|https://YOURTEAM.cloudflareaccess.com|EDITOR'
for ph in $(grep -oE '<[a-zA-Z][^ >]*>' "$DOC" | tr -d '<>' | sort -u); do
    printf '%s' "$ph" | grep -qE "^($KNOWN)$" \
        || fail "undeclared placeholder <$ph> — add it to KNOWN in this script, or use an existing one"
done

# ---- 5. `pct exec` lines with a glob are wrapped in a shell -------------
# `pct exec` runs the command with NO shell in the container. A glob is
# therefore expanded by the CALLING shell, against the Proxmox HOST's
# filesystem, and a literal `*` is passed through — so
#   pct exec 106 -- ls /opt/knapper/*.tar.gz
# reports "No such file or directory" IDENTICALLY whether the file exists or
# not. §10.1's retained-artifact check shipped this way and read as "no
# artifact, go make one" at the exact moment the artifact was there (found by
# running §10 for the first time, 2026-08-13). Pipes and redirects are NOT
# flagged: those legitimately belong to the host side of the command.
# Line-initial only: this is about COMMANDS, and prose mentioning `pct exec`
# next to markdown bold would otherwise trip it on the asterisks.
GLOB_LINES=$(grep -nE '^[[:space:]]*pct exec' "$DOC" | grep -F '*' | grep -v 'sh -c')
if [ -n "$GLOB_LINES" ]; then
    printf '%s\n' "$GLOB_LINES" >&2
    fail "a 'pct exec' line contains a glob but no 'sh -c' — pct exec has no shell in the container, so the host expands it and a literal '*' passes through (see §10.1)"
fi

# ---- 6. the smoke hostname does not outlive its teardown ----------------
# The identity table's dominant late-document failure: a reference resolving to
# the wrong member of a pair. §8b's smoke instance, its route and its Access app
# are all torn down in §9's checklist, so any §10 command aimed at
# <smoke-hostname> targets a name that no longer resolves — and `verify` failing
# at connect reads like the upgrade having broken the service. §10.3 and §10.4
# both shipped this way.
LATE_SMOKE=$(awk '/^## (9|10)\./{s=1} s && /<smoke-hostname>/{print FILENAME":"NR": "$0}' "$DOC")
if [ -n "$LATE_SMOKE" ]; then
    printf '%s\n' "$LATE_SMOKE" >&2
    fail "<smoke-hostname> is referenced at or after §9, where §9's checklist has already torn the smoke instance down — §10 targets the PRODUCTION hostname"
fi

# ---- 7. the smoke instance never points at the live vault ---------------
# The one invariant of §8b that would be catastrophic rather than annoying.
if grep -q 'Vault__RootPath=/vault' ops/systemd/knapper-smoke.service.example; then
    fail "knapper-smoke.service.example points at /vault — its vault MUST live outside Helios"
fi
grep -q 'Sync__Mode=open' ops/systemd/knapper-smoke.service.example \
    || fail "knapper-smoke.service.example lost Sync__Mode=open — its mutation tests would MutationBlock"
grep -q 'Sync__Mode=heartbeat' ops/systemd/knapper.service \
    || fail "knapper.service is not Sync__Mode=heartbeat — the fail-closed gate is off in PRODUCTION"

# ---- 8. the unit ships a placeholder for EVERY Access setting §6 needs --
# It shipped three (Enabled, TeamDomain, Audience) where §6 needs four, and the
# missing one was MonitoringAudience — so filling in the block the unit offers
# lands you in the single-app collapse by accident: /up falls back to the owner
# audience and the monitor's credential carries the whole vault, booting clean
# with doctor all-ok. An operator configures what the file shows them; a
# setting with no commented line is a setting that does not exist.
for access_key in Enabled TeamDomain Audience MonitoringAudience; do
    grep -qE "^#Environment=Mcp__Access__${access_key}=" ops/systemd/knapper.service \
        || fail "knapper.service has no commented placeholder for Mcp__Access__${access_key} — §6.3 fills in all four in ONE edit, and a missing line is one an operator never sets"
done

[ "$FAILURES" -eq 0 ] && { echo "runbook-lint: ok"; exit 0; }
echo "runbook-lint: $FAILURES problem(s)" >&2
exit 1
