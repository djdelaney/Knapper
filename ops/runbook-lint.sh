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
    && fail "a line ties /etc/knapper-monitor.conf to Claude Code — those are different Access apps (§6.2 root vs §6.4 /up)"

# ---- 4. placeholders are declared ---------------------------------------
# Every <thing> in the document should be substitutable and known. A new
# placeholder is fine; adding it here is the cost of admission, and the point
# is that a deployment can key on the list.
KNOWN='lan-resolver|backup-storage|parent-dataset|rootfs-dataset|archive|fresh-archive|storage|nn|PINNED|v|token|secret|monitor-token|monitor-secret|smoke-hostname|smoke-access-app-aud|ct-address|term|ref|vmid|scratch-vmid|vaultId|https://YOURTEAM.cloudflareaccess.com|EDITOR'
for ph in $(grep -oE '<[a-zA-Z][^ >]*>' "$DOC" | tr -d '<>' | sort -u); do
    printf '%s' "$ph" | grep -qE "^($KNOWN)$" \
        || fail "undeclared placeholder <$ph> — add it to KNOWN in this script, or use an existing one"
done

# ---- 5. the smoke instance never points at the live vault ---------------
# The one invariant of §8b that would be catastrophic rather than annoying.
if grep -q 'Vault__RootPath=/vault' ops/systemd/knapper-smoke.service.example; then
    fail "knapper-smoke.service.example points at /vault — its vault MUST live outside Helios"
fi
grep -q 'Sync__Mode=open' ops/systemd/knapper-smoke.service.example \
    || fail "knapper-smoke.service.example lost Sync__Mode=open — its mutation tests would MutationBlock"
grep -q 'Sync__Mode=heartbeat' ops/systemd/knapper.service \
    || fail "knapper.service is not Sync__Mode=heartbeat — the fail-closed gate is off in PRODUCTION"

[ "$FAILURES" -eq 0 ] && { echo "runbook-lint: ok"; exit 0; }
echo "runbook-lint: $FAILURES problem(s)" >&2
exit 1
