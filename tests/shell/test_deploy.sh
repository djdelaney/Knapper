#!/bin/sh
# Tests for ops/deploy.sh — the guided upgrade harness.
#
# Two kinds of claim are worth pinning here. First, the file is SITE-AGNOSTIC:
# it lives in a public repo, and a deployment's hostname, address or URL
# pasted in "just for now" is published the moment it is pushed. That cannot
# be tested by listing the forbidden values (the list would publish them), so
# it is tested by SHAPE: no private or routable IPv4, no hostname outside the
# reserved example domains. Second, the checks that decide whether a restart
# may proceed — the ones whose failure is silent — behave as documented. The
# run itself needs a real host and is exercised by deploying.
set -u

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SCRIPT="$ROOT/ops/deploy.sh"
EXAMPLE="$ROOT/ops/deploy.env.example"
FAILURES=0
TMPROOT=$(mktemp -d)
trap 'rm -rf "$TMPROOT"' EXIT

fail() {
    echo "   FAIL: $1" >&2
    FAILURES=$((FAILURES + 1))
}

# Run bash with the script's functions loaded (its main never runs).
lib() {
    KNAPPER_DEPLOY_LIB=1 bash -c 'set -euo pipefail; DRY_RUN=0; . "$0"; '"$1" "$SCRIPT" 2>&1
}

# ── site-agnostic, by shape ─────────────────────────────────────────────────
for f in "$SCRIPT" "$EXAMPLE"; do
    name=$(basename "$f")
    # Any IPv4 literal other than loopback, 0.0.0.0 and the three
    # documentation ranges (RFC 5737).
    ips=$(grep -oE '\b([0-9]{1,3}\.){3}[0-9]{1,3}\b' "$f" \
        | grep -vE '^(127\.0\.0\.1|0\.0\.0\.0|192\.0\.2\.[0-9]+|198\.51\.100\.[0-9]+|203\.0\.113\.[0-9]+)$' || true)
    [ -z "$ips" ] || fail "$name carries a real-looking IPv4 address: $(echo $ips)"
    # Any WHOLE dotted token whose last label is a real TLD, other than the
    # reserved example domains. Judged on the whole token so a key like
    # LIBARCHIVE.xattr.com.apple (a tar metadata name) is not read as a host,
    # and file names (deploy.env, *.sh, *.tar.gz) never qualify.
    hosts=$(grep -oE '[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+' "$f" \
        | grep -E '\.(com|net|org|io|dev|app|lan|local|home|internal|arpa|cloud|me|co|uk|de)$' \
        | grep -vE '(^|\.)example\.(com|net|org)$' || true)
    [ -z "$hosts" ] || fail "$name carries a real-looking hostname: $(echo $hosts)"
done

# ── REQUIRED_ENV against a unit's Environment= rendering ────────────────────
# systemctl show renders an assignment whose value has spaces in quotes.
UNIT='Vault__RootPath=/v Vault__ArchivedPrefixes__0=Archive "Conventions__Style=a b c" Conventions__NoNewTags=true'

out=$(lib 'REQUIRED_ENV=("Vault__ArchivedPrefixes__0=Archive" "Conventions__Style" "Conventions__NoNewTags=true"); check_required_env '"'$UNIT'"' && echo PASSED')
case "$out" in *PASSED*) ;; *) fail "exact and presence-only entries that ARE present did not pass: $out" ;; esac
case "$out" in *"present: Conventions__Style (presence-only)"*) ;; *) fail "a quoted value with spaces was not recognised as present: $out" ;; esac

out=$(lib 'REQUIRED_ENV=("Vault__ArchivedPrefixes__0=Old"); check_required_env '"'$UNIT'"' || echo REFUSED')
case "$out" in *MISMATCH*REFUSED*) ;; *) fail "a WRONG value must be reported as a mismatch and refuse: $out" ;; esac

out=$(lib 'REQUIRED_ENV=("Conventions__NewNoteFolder"); check_required_env '"'$UNIT'"' || echo REFUSED')
case "$out" in *MISSING*REFUSED*) ;; *) fail "an absent presence-only key must refuse: $out" ;; esac

# A key that merely ENDS with the required one is not it.
out=$(lib 'REQUIRED_ENV=("Tags=true"); check_required_env '"'$UNIT'"' || echo REFUSED')
case "$out" in *REFUSED*) ;; *) fail "a suffix match (Conventions__NoNewTags) was taken for Tags: $out" ;; esac

out=$(lib 'REQUIRED_ENV=("X=1"); check_required_env "" || true')
case "$out" in *"no usable environment"*) ;; *) fail "an empty environment (unit missing or not loaded) must stop the run: $out" ;; esac

# ── REQUIRED_ENV validation ─────────────────────────────────────────────────
out=$(lib 'REQUIRED_ENV=("Conventions__Style=a b"); required_env_problems || echo INVALID')
case "$out" in *"bare 'Conventions__Style'"*INVALID*) ;; *) fail "an exact entry with whitespace must be refused with the bare-key fix: $out" ;; esac
out=$(lib 'REQUIRED_ENV=("1BAD=x"); required_env_problems || echo INVALID')
case "$out" in *INVALID*) ;; *) fail "an entry without a valid variable name must be refused: $out" ;; esac

# ── doctor's "(none)" is a refusal for a required knob ──────────────────────
out=$(lib 'REQUIRED_ENV=("Conventions__NoNewTags=true"); check_doctor_output "ok    Conventions parse (none)" || echo REFUSED')
case "$out" in *REFUSED*) ;; *) fail "doctor parsing no conventions must refuse when one is required: $out" ;; esac
out=$(lib 'REQUIRED_ENV=("Vault__ArchivedPrefixes__0=Archive"); check_doctor_output "ok    Vault:ArchivedPrefixes parses (none)" || echo REFUSED')
case "$out" in *REFUSED*) ;; *) fail "doctor parsing no archived prefixes must refuse when one is required: $out" ;; esac
out=$(lib 'REQUIRED_ENV=("Conventions__NoNewTags=true"); check_doctor_output "ok    Conventions parse (no-new-tags)" && echo PASSED')
case "$out" in *PASSED*) ;; *) fail "a parsed convention must pass: $out" ;; esac

# ── retention never drops the rollback target ───────────────────────────────
out=$(lib 'retention_drops a.tgz b.tgz /o/a.tgz /o/b.tgz /o/c.tgz')
[ "$out" = "/o/c.tgz" ] || fail "retention must drop exactly the tarballs outside the keep pair: '$out'"
# A re-deploy of the running version makes both keeps the SAME file; the real
# previous build would then be dropped. Nothing may be.
out=$(lib 'retention_drops a.tgz a.tgz /o/a.tgz /o/prev.tgz')
[ -z "$out" ] || fail "a re-deploy of the running version must prune nothing (the rollback target is unknown): '$out'"

# The deletion command keeps `--` a separate word, whatever spacing the DROP
# list arrives with (fused, `rm -f --/opt/…` is an unknown option to rm).
out=$(lib 'prune_command $(retention_drops a.tgz b.tgz /o/a.tgz /o/b.tgz /o/c.tgz /o/d.tgz | tr "\n" " ")')
[ "$out" = "rm -f -- /o/c.tgz /o/d.tgz" ] || fail "prune command is malformed: '$out'"

# ── config loading ──────────────────────────────────────────────────────────
out=$(KNAPPER_DEPLOY_ENV="$TMPROOT/absent.env" lib 'load_config' || true)
case "$out" in *"no deploy config"*) ;; *) fail "a missing config file must stop the run: $out" ;; esac

printf 'KNAPPER_PUBLIC_URL=http://mcp.example.com\nREQUIRED_ENV=()\n' > "$TMPROOT/bad.env"
out=$(KNAPPER_DEPLOY_ENV="$TMPROOT/bad.env" lib 'load_config' || true)
case "$out" in *"KNAPPER_CT_SSH is unset"*"must be https://"*) ;; *) fail "every config problem must be reported at once: $out" ;; esac

# The shipped example, filled in only where it must be, loads clean.
out=$(KNAPPER_DEPLOY_ENV="$EXAMPLE" lib 'load_config && echo LOADED' || true)
case "$out" in *LOADED*) ;; *) fail "ops/deploy.env.example does not load: $out" ;; esac

# Sourcing for tests must never reach the run (it would ssh).
out=$(KNAPPER_DEPLOY_LIB=1 bash -c '. "$0"; echo SOURCED' "$SCRIPT" 2>&1)
case "$out" in SOURCED) ;; *) fail "sourcing with KNAPPER_DEPLOY_LIB=1 ran more than definitions: $out" ;; esac

[ "$FAILURES" -eq 0 ]
