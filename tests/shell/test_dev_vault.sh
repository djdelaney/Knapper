#!/bin/sh
# Tests for tools/dev-vault.sh — the synthetic vault for hand-run dev servers.
#
# The claims worth holding it to are the safety ones, because the failure is
# someone pointing it at the wrong folder: it never writes into a directory
# that already has content, never inside an existing Obsidian vault, and adds
# the fail-closed hazards only when asked. Then determinism (a behavior seen
# over the dev vault once can be reproduced) and the handful of byte-exact
# files whose whole point is their bytes.
set -u

SCRIPT="$(cd "$(dirname "$0")/../.." && pwd)/tools/dev-vault.sh"
FAILURES=0
TMPROOT=$(mktemp -d)
trap 'rm -rf "$TMPROOT"' EXIT

fail() {
    echo "   FAIL: $1" >&2
    FAILURES=$((FAILURES + 1))
}

bash -n "$SCRIPT" || fail "tools/dev-vault.sh does not parse"

# ---- 1. a plain run: layout, content, no hazards ---------------------------
"$SCRIPT" "$TMPROOT/a" >/dev/null 2>&1 || fail "plain run exited non-zero"
V="$TMPROOT/a/vault"
for f in "Home.md" "Daily/2026-09-07.md" "Projects/Garden Shed/Project Plan.md" \
         "Notes/🚀 Launch Ideas.md" "Notes/Ａ Fullwidth Title.md" "Notes-old/Old Scratch.md" \
         "Frontmatter/broken.md" "Lint Demo.md" "Archive/Old Plan.md" \
         ".obsidian/app.json" ".trash/Deleted Note.md" ".hidden.md"; do
    [ -f "$V/$f" ] || fail "missing $f"
done
[ -d "$TMPROOT/a/state/locks" ] || fail "state/locks not created outside the vault"
[ ! -e "$V/Hazards" ] || fail "hazards present without --hazards"
ls "$V/Work" | grep -q 'Conflicted copy' && fail "conflict copy present without --hazards"

# Byte-exact files: their bytes ARE the test case.
[ "$(head -c 8 "$V/Attachments/shed-sketch.png" | od -An -tx1 | tr -d ' \n')" = "89504e470d0a1a0a" ] \
    || fail "shed-sketch.png does not start with the PNG signature"
[ "$(head -c 4 "$V/Encoding/legacy-latin1.md" | od -An -tx1 | tr -d ' \n')" = "636166e9" ] \
    || fail "legacy-latin1.md is not Latin-1 (expected caf + 0xE9)"
[ "$(head -c 3 "$V/Encoding/utf8-bom.md" | od -An -tx1 | tr -d ' \n')" = "efbbbf" ] \
    || fail "utf8-bom.md does not start with a BOM"
grep -q "$(printf '\r')" "$V/Encoding/windows-crlf.md" || fail "windows-crlf.md has no CR"
[ ! -s "$V/empty.md" ] || fail "empty.md is not empty"

# env.sh points a dev server at THIS vault, with the dev-only sync opt-out.
(
    . "$TMPROOT/a/env.sh"
    [ "$Vault__RootPath" = "$(cd "$V" && pwd -P)" ] || exit 1
    [ "$Sync__Mode" = "open" ] || exit 1
    case "$Vault__LockDirectory" in "$Vault__RootPath"/*) exit 1 ;; esac
) || fail "env.sh does not describe the generated vault (root, open sync gate, locks outside)"

# ---- 2. deterministic: two runs, identical vaults --------------------------
"$SCRIPT" "$TMPROOT/b" >/dev/null 2>&1
diff -r "$TMPROOT/a/vault" "$TMPROOT/b/vault" >/dev/null || fail "two runs produced different vaults"

# ---- 3. refusals: nothing is written ---------------------------------------
mkdir -p "$TMPROOT/full" && echo keep > "$TMPROOT/full/mine.md"
if "$SCRIPT" "$TMPROOT/full" >/dev/null 2>&1; then fail "wrote into a non-empty directory"; fi
[ "$(ls -A "$TMPROOT/full")" = "mine.md" ] || fail "refused run still changed the non-empty directory"

mkdir -p "$TMPROOT/real/.obsidian" "$TMPROOT/real/Sub"
if "$SCRIPT" "$TMPROOT/real/Sub/dev" >/dev/null 2>&1; then fail "wrote inside an Obsidian vault"; fi
[ ! -e "$TMPROOT/real/Sub/dev" ] || fail "refused run still created a directory inside the vault"

if "$SCRIPT" "$TMPROOT/no/such/parent" >/dev/null 2>&1; then fail "accepted a missing parent directory"; fi

touch "$TMPROOT/afile"
if "$SCRIPT" "$TMPROOT/afile" >/dev/null 2>&1; then fail "accepted a file as the target"; fi

mkdir "$TMPROOT/emptydir"
"$SCRIPT" "$TMPROOT/emptydir" >/dev/null 2>&1 || fail "refused an existing EMPTY directory"

# ---- 4. --hazards -----------------------------------------------------------
"$SCRIPT" --hazards "$TMPROOT/h" >/dev/null 2>&1 || fail "--hazards run exited non-zero"
H="$TMPROOT/h/vault"
[ -f "$H/Work/Meeting (Conflicted copy 2026-09-02 101500).md" ] || fail "no Sync conflict copy"
[ -L "$H/Hazards/outside-link.md" ] || fail "no symlink"
[ -f "$H/Hazards/outside-link.md" ] || fail "symlink does not resolve"
case "$(cd "$(dirname "$H/Hazards/outside-link.md")" && cd "$(dirname "$(readlink "outside-link.md")")" && pwd -P)" in
    "$(cd "$H" && pwd -P)"*) fail "symlink target is inside the vault; it must leave it" ;;
esac
[ -p "$H/Hazards/pipe.md" ] || fail "no FIFO"
[ "$(wc -c < "$H/Hazards/oversized.md" | tr -d ' ')" = "5000001" ] || fail "oversized.md is not 5,000,001 bytes"

[ "$FAILURES" -eq 0 ] || exit 1
echo "   dev vault: guards, determinism, byte-exact files, hazards"
