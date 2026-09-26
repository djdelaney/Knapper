#!/usr/bin/env bash
# ops/deploy.sh — guided upgrade of a Knapper deployment (runbook §9/§10).
# Runs on the operator's workstation, from a clean checkout of the tag being
# deployed, against the host that runs knapper.service.
#
# This is a HARNESS, not an automation. It stops at every point where a human
# decision is required — above all the unit diff, where copying a shipped file
# silently reverts site config into a service that still starts and still
# passes every check.
#
# ⛔ SITE-AGNOSTIC BY RULE. This file is in a public repository and carries NO
#    deployment's details: no hostnames, addresses, URLs, vault names or
#    folder names. Every site value comes from the operator's own config file
#    (below), which never enters the repo. tests/shell/test_deploy.sh fails
#    the build on a hostname or private address appearing here or in
#    ops/deploy.env.example.
#
# Configuration: ${KNAPPER_DEPLOY_ENV:-~/.config/knapper/deploy.env}, a bash
# file this script SOURCES (so it is code — keep it yours, mode 600). Copy
# ops/deploy.env.example and fill it in. Required: KNAPPER_CT_SSH,
# KNAPPER_PUBLIC_URL. Everything else has a default or is optional.
#
# Usage:
#   ops/deploy.sh --dry-run          # read-only: preflight + build + diffs + prune PLAN
#   ops/deploy.sh                    # full run, stops at each gate
#   ops/deploy.sh --skip-build       # use the newest existing artifact
#   ops/deploy.sh --no-prune         # skip retention (you then owe a manual prune)
#
# Why each gate exists, in brief:
#   §1  publish.sh builds the WORKING TREE: a clean tree is what binds the
#       artifact to the tag, and verify --expect-version refuses a .dirty build.
#   §2  the running version IS the rollback target, and a rollback path is only
#       printed once the tarball for it is proven present on the host.
#   §5  the extract is the point of no return; there is no snapshot step.
#       Rollback is the retained tarball, or rolling forward from a git tag.
#   §6  the unit diff is DERIVED from the artifact's ops/systemd/, never from a
#       written list (an enumerated list has been wrong before, silently).
#   §6b MEASURES the unit's environment against REQUIRED_ENV before the
#       restart, and will not take a yes for an answer. An unset knob is
#       invisible to every other step: the service starts, doctor is all-ok,
#       verify is green, and the feature simply does nothing.
#   §8  doctor runs with the RUNNING PROCESS's environment
#       (/proc/<MainPID>/environ), never a word-split `systemctl show`: a
#       value containing a space (Conventions__Style) broke that once.
#   §11b retention is the ONLY deleting code here, runs only after verify
#       passed in this run, and asks first. Opt-in pruning was skipped run
#       after run; a gate you must answer is not.

set -euo pipefail

# ─────────────────────────── helpers ───────────────────────────
say()  { printf '\n\033[1m── %s\033[0m\n' "$*"; }
ok()   { printf '   \033[32mok\033[0m   %s\n' "$*"; }
warn() { printf '   \033[33mwarn\033[0m %s\n' "$*"; }
die()  { printf '\n\033[31mSTOP\033[0m %s\n' "$*" >&2; exit 1; }

# Ordinary checkpoint: accepts yes/YES/Yes. Nothing destructive past it.
gate() {
  [ "$DRY_RUN" -eq 1 ] && { warn "dry-run: would pause here — $1"; return 0; }
  printf '\n\033[33m?\033[0m %s\n   type yes to continue: ' "$1"
  read -r reply
  case "$(printf '%s' "$reply" | tr '[:upper:]' '[:lower:]')" in
    yes) : ;;
    *) die "aborted at gate: $1" ;;
  esac
}
# Consequential checkpoint: literal YES only. The friction is the point.
gate_strict() {
  [ "$DRY_RUN" -eq 1 ] && { warn "dry-run: would pause here — $1"; return 0; }
  printf '\n\033[33m?\033[0m %s\n   type YES (exactly, uppercase) to continue: ' "$1"
  read -r reply; [ "$reply" = "YES" ] || die "aborted at gate: $1"
}

# ─────────────────────────── config ────────────────────────────
# Loads and VALIDATES the operator's config. Sets the globals the run uses.
# Every problem is reported at once, before anything touches a host.
load_config() {
  local file="${KNAPPER_DEPLOY_ENV:-$HOME/.config/knapper/deploy.env}"
  REQUIRED_ENV=()
  [ -f "$file" ] || die "no deploy config at $file — copy ops/deploy.env.example there and fill it in (or set KNAPPER_DEPLOY_ENV)"
  # shellcheck source=/dev/null
  . "$file"

  CT_SSH="${KNAPPER_CT_SSH:-}"
  PUBLIC_URL="${KNAPPER_PUBLIC_URL:-}"
  MONITOR_SSH="${KNAPPER_MONITOR_SSH:-}"
  MONITOR_UNIT="${KNAPPER_MONITOR_UNIT:-knapper-monitor.service}"
  INSTALL_DIR="${KNAPPER_INSTALL_DIR:-/opt/knapper}"
  VERIFY_ENV="${KNAPPER_VERIFY_ENV:-/root/.knapper-verify.env}"
  LOGDIR="${KNAPPER_LOGDIR:-$HOME/knapper-deploys}"
  read -r -a SSH_OPTS <<< "${KNAPPER_SSH_OPTS:-}"
  CLI="$INSTALL_DIR/cli/knapper"

  local problems=()
  [ -n "$CT_SSH" ] || problems+=("KNAPPER_CT_SSH is unset (ssh destination of the host running knapper.service)")
  case "$PUBLIC_URL" in
    https://*/) : ;;
    "") problems+=("KNAPPER_PUBLIC_URL is unset (the Access-fronted URL verify runs through)") ;;
    *) problems+=("KNAPPER_PUBLIC_URL must be https:// and end in '/', got '$PUBLIC_URL'") ;;
  esac
  local p
  if p=$(required_env_problems); then :; else problems+=("$p"); fi
  if [ "${#problems[@]}" -gt 0 ]; then
    printf '   \033[31mconfig\033[0m %s\n' "${problems[@]}" >&2
    die "fix $file and re-run"
  fi
}

# REQUIRED_ENV entries are either KEY=VALUE (the unit must carry exactly that)
# or a bare KEY (presence only). A value containing whitespace cannot be
# matched exactly against `systemctl show`'s rendering, so such a knob is
# listed bare — its content is doctor's to report, not this check's.
required_env_problems() {
  local kv key bad=0
  for kv in "${REQUIRED_ENV[@]+"${REQUIRED_ENV[@]}"}"; do
    key="${kv%%=*}"
    if ! [[ "$key" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]]; then
      echo "REQUIRED_ENV entry '$kv' does not start with a valid variable name"; bad=1
    elif [[ "$kv" == *=* && "$kv" =~ [[:space:]] ]]; then
      echo "REQUIRED_ENV entry '$key=…' has whitespace in its value — list it as a bare '$key' (presence only)"; bad=1
    fi
  done
  return $bad
}

# Compare REQUIRED_ENV against a unit's Environment= as `systemctl show
# -p Environment --value` renders it: space-separated, with an assignment
# whose value contains whitespace wrapped in double quotes. Returns 1 if
# anything is missing or wrong.
check_required_env() {
  local ke="$1" missing=0 kv key have
  case "$ke" in
    *Vault__RootPath=*) : ;;
    *) die "no usable environment from knapper.service — wrong unit name, or not loaded. Nothing below proves anything without it." ;;
  esac
  for kv in "${REQUIRED_ENV[@]+"${REQUIRED_ENV[@]}"}"; do
    key="${kv%%=*}"
    if [[ "$kv" != *=* ]]; then
      if [[ " $ke" =~ [\ \"]${key}= ]]; then ok "present: $key (presence-only)"; else warn "MISSING:  $key"; missing=1; fi
    elif [[ " $ke " == *" $kv "* ]]; then
      ok "present: $kv"
    elif [[ " $ke" =~ [\ \"]${key}= ]]; then
      # A WRONG value is worse than a missing one: it reads as configured.
      have=$(printf '%s' "$ke" | tr ' ' '\n' | grep -E "^\"?${key}=" | head -1 || true)
      warn "MISMATCH: unit has '$have', this deployment expects '$kv'"
      missing=1
    else
      warn "MISSING:  $kv"
      missing=1
    fi
  done
  return $missing
}

# doctor PRINTS what it parsed, so an unset knob reads as an `ok` line saying
# "(none)" and scrolls past in a wall of green. For each required knob with a
# doctor line, refuse that answer.
check_doctor_output() {
  local out="$1" kv
  for kv in "${REQUIRED_ENV[@]+"${REQUIRED_ENV[@]}"}"; do
    case "${kv%%=*}" in
      Vault__ArchivedPrefixes__*)
        if printf '%s' "$out" | grep -q 'ArchivedPrefixes parses (none)'; then
          echo "doctor parsed NO archived prefixes while ${kv%%=*} is required"; return 1
        fi ;;
      Conventions__*)
        if printf '%s' "$out" | grep -q 'Conventions parse (none)'; then
          echo "doctor parsed NO conventions while ${kv%%=*} is required"; return 1
        fi ;;
    esac
  done
  return 0
}

# The retention plan: which retained tarballs to DROP, given the one now
# running (keep_a), the rollback target (keep_b) and the full list. Prints one
# path per line. When keep_a == keep_b — a re-deploy of the running version —
# the "rollback target" is the build being replaced by ITSELF, so the real
# previous build is not among the keeps and would be listed for deletion:
# nothing is dropped then, because the true rollback target is unknown here.
retention_drops() {
  local keep_a="$1" keep_b="$2" f b
  shift 2
  [ "$keep_a" = "$keep_b" ] && return 0
  for f in "$@"; do
    b="${f##*/}"
    [ "$b" = "$keep_a" ] && continue
    [ "$b" = "$keep_b" ] && continue
    printf '%s\n' "$f"
  done
}

# The remote command that deletes the DROP list. A function so the exact
# string is testable: when the list lost the leading space the old inline
# `rm -f --$TGZ_DROP` relied on, it became `rm -f --/opt/…`, which rm rejects
# as an unknown option — failing closed, but failing the run at its last step
# (first real 0.11.0 deploy, 2026-09-26).
prune_command() {
  printf 'rm -f --'
  printf ' %s' "$@"
}

# What to do with the DROP list. Prints exactly one word, and `prune` is the
# ONLY answer that leads to a deletion — so the whole safety claim of §11b
# ("nothing is deleted unless verify passed in THIS run, never under
# --dry-run or --no-prune, never on a same-version re-deploy") is this
# function's truth table, and test_deploy.sh pins all of it.
#   retention_action VERIFY_OK DRY_RUN NO_PRUNE KEEP_A KEEP_B [DROP...]
retention_action() {
  local verify_ok="$1" dry_run="$2" no_prune="$3" keep_a="$4" keep_b="$5"
  shift 5
  if [ "$keep_a" = "$keep_b" ]; then echo same-version
  elif [ "$#" -eq 0 ]; then echo nothing
  elif [ "$dry_run" = 1 ]; then echo dry-run
  elif [ "$verify_ok" != 1 ]; then echo unverified
  elif [ "$no_prune" = 1 ]; then echo no-prune
  else echo prune
  fi
}

# Remote helpers. `-n` on ct and mon: ssh otherwise READS THIS SCRIPT'S STDIN,
# which is where the gate answers come from — anything typed ahead while a
# remote command runs (or the next answer on a pipe) is forwarded to the
# remote command and lost, and the gate then waits on input that already
# went. ct_stdin is the one deliberate exception, for a remote script sent
# over stdin (§8 doctor). Every ssh in this file goes through these three;
# test_deploy.sh checks both the flags and that nothing bypasses them.
ct()       { ssh -n -o BatchMode=yes "${SSH_OPTS[@]+"${SSH_OPTS[@]}"}" "$CT_SSH" "$@"; }
ct_stdin() { ssh -o BatchMode=yes "${SSH_OPTS[@]+"${SSH_OPTS[@]}"}" "$CT_SSH" "$@"; }
mon()      { ssh -n -o BatchMode=yes "${SSH_OPTS[@]+"${SSH_OPTS[@]}"}" "$MONITOR_SSH" "$@"; }

# Sourced by tests/shell/test_deploy.sh for the functions above; nothing below
# runs when sourced that way.
if [ "${KNAPPER_DEPLOY_LIB:-0}" = 1 ]; then return 0 2>/dev/null || exit 0; fi

# ─────────────────────────── main ──────────────────────────────
DRY_RUN=0; SKIP_BUILD=0; NO_PRUNE=0; VERIFY_OK=0
for a in "$@"; do
  case "$a" in
    --dry-run) DRY_RUN=1 ;;
    --skip-build) SKIP_BUILD=1 ;;
    --no-prune) NO_PRUNE=1 ;;
    -h|--help) awk 'NR>1 { if ($0 !~ /^#/) exit; print }' "$0"; exit 0 ;;
    *) echo "unknown flag: $a" >&2; exit 2 ;;
  esac
done

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
load_config


mkdir -p "$LOGDIR"
STAMP="$(date +%Y%m%d-%H%M%S)"
LOG="$LOGDIR/deploy-$STAMP.log"
exec > >(tee -a "$LOG") 2>&1
echo "knapper deploy $STAMP   dry-run=$DRY_RUN   log=$LOG"

# ───────────────────── 1. local preflight ─────────────────────
say "1. Local preflight"
cd "$REPO"
[ -f ops/publish.sh ] || die "ops/publish.sh missing — is this script inside the Knapper repo?"
if [ -n "$(git status --porcelain)" ]; then
  git status --short
  die "working tree is dirty — publish.sh builds the tree, and a .dirty build is refused by verify --expect-version"
fi
ok "git tree clean"
TAG="$(git describe --tags --exact-match HEAD 2>/dev/null || true)"
echo "   HEAD  $(git rev-parse --short HEAD)  ${TAG:-(not a tag)}"
[ -n "$TAG" ] || warn "HEAD is not exactly a tag — ship only a tag CI passed (ops/release.sh --ship)"
gate "Is HEAD the commit you intend to ship?"

# ──────────────────── 2. remote preflight ─────────────────────
say "2. Remote preflight"
ct true || die "cannot ssh $CT_SSH"
ok "ssh reachable: service host"
if [ -n "$MONITOR_SSH" ]; then
  mon true || die "cannot ssh $MONITOR_SSH (KNAPPER_MONITOR_SSH) — unset it to skip the forced monitor run"
  ok "ssh reachable: monitor host"
fi

HEALTH="$(ct "curl -s 127.0.0.1:3535/health")"
[ -n "$HEALTH" ] || die "no response from /health — is knapper up?"
# /health carries MORE THAN ONE "version" field (ripgrep reports one too).
# Take the FIRST, then validate its shape before building a path from it.
CUR_VER="$(printf '%s' "$HEALTH" | grep -o '"version":"[^"]*"' | head -1 | sed 's/^"version":"//; s/"$//')"
case "$CUR_VER" in
  [0-9]*.[0-9]*.[0-9]*) ok "currently running: $CUR_VER   ← THIS IS THE ROLLBACK TARGET" ;;
  *) die "parsed a non-version string from /health: '$CUR_VER' — refusing to build a rollback path from it" ;;
esac
printf '%s' "$HEALTH" | tr ',' '\n' | grep -E '"status"|conflictFiles|conflictScanComplete|"mode"|mutationsAllowed' || true
printf '%s' "$HEALTH" | grep -q '"conflictFiles":\[\]' \
  || die "vault has conflict files — mutations are fail-closed; resolve before upgrading"
ok "no conflict files"

ct "test -f $VERIFY_ENV" || die "$VERIFY_ENV missing on the host — verify cannot authenticate"
ct "stat -c '%a %n' $VERIFY_ENV" | grep -q '^600 ' || die "$VERIFY_ENV is not mode 600"
ok "verify credentials present, mode 600"

ct "ls -1 $INSTALL_DIR/*.tar.gz 2>/dev/null || true" | sed 's/^/   retained: /'
ct "df -h $INSTALL_DIR | tail -1" | sed 's/^/   disk: /'

ROLLBACK_TGZ="$INSTALL_DIR/knapper-$CUR_VER-linux-x64.tar.gz"
if ct "test -f '$ROLLBACK_TGZ'"; then
  ok "rollback artifact present: $(basename "$ROLLBACK_TGZ")"
else
  ROLLBACK_TGZ="(none retained — THERE IS NO ROLLBACK POINT)"
  warn "NO retained tarball for the running build: this run has NO rollback point. Roll forward from a git tag."
fi

# ───────────────────────── 3. build ───────────────────────────
say "3. Build (linux-x64)"
if [ "$SKIP_BUILD" -eq 0 ]; then
  if [ "$DRY_RUN" -eq 1 ]; then warn "dry-run: skipping actual build"; else sh ops/publish.sh; fi
else
  warn "--skip-build: using newest existing artifact"
fi
ART="$(ls -t artifacts/knapper-*-linux-x64.tar.gz 2>/dev/null | head -1 || true)"
[ -n "$ART" ] || die "no artifact in artifacts/"
NEW_VER="$(basename "$ART" | sed 's/^knapper-//; s/-linux-x64\.tar\.gz$//')"
case "$NEW_VER" in *.dirty*) die "artifact is a .dirty build — refuse" ;; esac
LOCAL_SHA="$(shasum -a 256 "$ART" | awk '{print $1}')"
ok "artifact  $ART"
ok "version   $NEW_VER"
ok "sha256    $LOCAL_SHA"
[ "$NEW_VER" = "$CUR_VER" ] && warn "built version equals running version — nothing to upgrade?"

say "Tarball layout (what you are about to lay down)"
tar -tzf "$ART" | awk -F/ '{print $2}' | sort -u | head -20 | sed 's/^/   /'

# ─────────────────────── 4. ship ──────────────────────────────
say "4. Ship the artifact"
gate "Copy the artifact to the service host?"
if [ "$DRY_RUN" -eq 0 ]; then
  # Into INSTALL_DIR, not /tmp: the rollback reads $INSTALL_DIR/*.tar.gz and /tmp clears on boot.
  scp -o BatchMode=yes "${SSH_OPTS[@]+"${SSH_OPTS[@]}"}" "$ART" "$CT_SSH:$INSTALL_DIR/"
  REMOTE_SHA="$(ct "sha256sum $INSTALL_DIR/$(basename "$ART")" | awk '{print $1}')"
  [ "$REMOTE_SHA" = "$LOCAL_SHA" ] || die "sha mismatch after scp: $LOCAL_SHA != $REMOTE_SHA"
  ok "sha matched on both sides of the scp"
else
  warn "dry-run: no scp"
fi

# ───────────────────────── 5. extract ─────────────────────────
say "5. Extract into $INSTALL_DIR"
# The tarball's top level is ./cli ./mcp ./ops — no wrapping directory, so no
# --strip-components. Unpacking does NOT touch /etc.
gate_strict "POINT OF NO RETURN. Extract $NEW_VER over $INSTALL_DIR? Rollback = $ROLLBACK_TGZ — there is no snapshot"
if [ "$DRY_RUN" -eq 0 ]; then
  # --warning=no-unknown-keyword silences the LIBARCHIVE.xattr.com.apple.*
  # noise macOS bsdtar bakes in — hundreds of harmless lines burying real output.
  ct "tar --warning=no-unknown-keyword -xzf $INSTALL_DIR/$(basename "$ART") -C $INSTALL_DIR"
  ok "extracted"
fi

# ──────────────────── 6. unit diff — HUMAN ────────────────────
say "6. Unit diff — derived from $INSTALL_DIR/ops/systemd/, not from a written list"
ct "for f in $INSTALL_DIR/ops/systemd/*; do
      b=\$(basename \$f); b=\${b%.example}
      if [ -f /etc/systemd/system/\$b ]; then
        if diff -q /etc/systemd/system/\$b \$f >/dev/null; then
          echo \"=== \$b: identical\"
        else
          echo \"=== \$b: DIFFERS\"
          diff -u /etc/systemd/system/\$b \$f || true
        fi
      else
        echo \"=== \$b: not installed in /etc (template or retired)\"
      fi
    done"
cat <<'NOTE'

   ⛔ READ THE DIFFS. '-' is what is RUNNING, '+' is what the release SHIPS.
      A '+' line is NOT an instruction to apply it. /etc is authoritative for
      site config. knapper.service is EXPECTED to differ by this deployment's
      site edits (AllowedHosts, the Access lines, MonitoringAudience,
      Sync__MaxFileBytes, Vault__ArchivedPrefixes__*, Conventions__*) —
      copying the shipped file reverts those into a service that still starts
      and still passes every check.

      Edit /etc in place for a release change or a new required knob, then the
      next step reads the result back. Copy a shipped unit ONLY when it carries
      no site edits at all.
NOTE
gate_strict "Have you reviewed every diff above and applied by hand anything that needed applying?"

# ────────────── 6b. Required environment — MEASURED ───────────
# Before the restart: the last moment the edit is free.
say "6b. Required environment (read off the loaded unit, not asked)"
env_of_unit() { ct "systemctl show knapper.service -p Environment --value"; }
# `systemctl show` reports the LOADED unit, so an edit made in another window
# is invisible until systemd re-reads the file. daemon-reload restarts nothing.
env_of_unit_reloaded() { ct "systemctl daemon-reload; systemctl show knapper.service -p Environment --value"; }
if [ "${#REQUIRED_ENV[@]}" -eq 0 ]; then
  warn "REQUIRED_ENV is empty in your deploy config — nothing is asserted about this deployment's knobs"
elif [ "$DRY_RUN" -eq 1 ]; then
  check_required_env "$(env_of_unit)" || warn "dry-run: would stop here until the unit is edited"
elif ! check_required_env "$(env_of_unit)"; then
  cat <<EDIT

   Add the missing line(s) to /etc/systemd/system/knapper.service now, in
   another window, then come back. A value with spaces needs the whole
   assignment quoted: Environment="KEY=a value with spaces".

   ⛔ Do NOT copy the shipped unit to get them — that reverts every other site
      edit at once (see §6). Edit /etc in place.
EDIT
  gate_strict "Applied the environment edit(s)? This re-reads the unit and will not take your word for it"
  check_required_env "$(env_of_unit_reloaded)" \
    || die "still missing after the edit — the file was not saved, or the key is spelled differently. Refusing to restart into a config that silently does nothing."
  ok "required environment confirmed on the loaded unit"
fi

# ─────────────────── 7. reload, restart, wait ─────────────────
say "7. daemon-reload and restart"
if [ "$DRY_RUN" -eq 0 ]; then
  ct "systemctl daemon-reload && systemctl restart knapper.service"
  ok "restarted"
  # verify right after a restart races ASP.NET startup: poll readiness instead.
  printf '   waiting for /health'
  for i in $(seq 1 30); do
    if ct "curl -sf -o /dev/null 127.0.0.1:3535/health"; then printf ' up\n'; break; fi
    printf '.'; sleep 2
    [ "$i" -eq 30 ] && die "service did not become ready in 60s"
  done
fi

# ───────────────────────── 8. doctor ──────────────────────────
say "8. doctor — with the running service's own environment"
# From /proc/<MainPID>/environ, NUL-separated: every value intact (spaces
# included), the service's own PATH, EnvironmentFile= values included. `env -i`
# drops the operator's variables so doctor sees exactly what the service runs
# with. A stopped unit reports MainPID 0 — refused, never graded against
# built-in defaults.
if [ "$DRY_RUN" -eq 0 ]; then
  DOCTOR_RC=0
  DOCTOR_OUT="$(ct_stdin "bash -s" <<REMOTE
PID=\$(systemctl show knapper.service -p MainPID --value)
if [ "\${PID:-0}" -gt 0 ] && tr '\\0' '\\n' < "/proc/\$PID/environ" | grep -q '^Vault__RootPath='; then
  xargs -0 -a "/proc/\$PID/environ" sh -c 'exec runuser -u knapper -- env -i "\$@" $CLI doctor' sh
else
  echo "REFUSING: knapper.service is not running with a usable environment (MainPID=\${PID:-?})"
  exit 1
fi
REMOTE
)" || DOCTOR_RC=$?
  printf '%s\n' "$DOCTOR_OUT"
  [ "$DOCTOR_RC" -eq 0 ] || die "doctor exited $DOCTOR_RC — read the FAIL line above"
  REFUSAL="$(check_doctor_output "$DOCTOR_OUT")" || die "$REFUSAL — the running service is not enforcing it, whatever the unit says"
  ok "doctor all-ok, and every required knob it reports on is parsed"
fi

# ───────────────────────── 9. verify ──────────────────────────
say "9. verify — through the public URL, from the service host"
# --url is what makes the request traverse the edge, not which box runs it.
# --expect-this-version is correct HERE: the CLI is the build just unpacked.
if [ "$DRY_RUN" -eq 0 ]; then
  ct "set -a; . $VERIFY_ENV; set +a
      $CLI verify --url $PUBLIC_URL --expect-this-version --expect-access" \
    || die "verify FAILED — DIAGNOSE BEFORE ROLLING BACK. Read WHICH check failed:
         · '/up answers 200 with the monitoring token' alone, while the MCP
           surface passes → the CF_MONITOR_* pair in $VERIFY_ENV is stale.
           Nothing to do with this build; fix the credential, re-run verify.
         · 'the service is running this CLI's own build' → compare the two
           versions it names before touching anything.
         · connection refused right after a restart → startup race; re-run.
       Only if the MCP surface itself is broken is the release the suspect.
       Rollback: $ROLLBACK_TGZ  (no snapshot is taken)"
  # Load-bearing: this is what catches a check that silently stopped running.
  # Update it whenever a release adds or removes a verify check.
  warn "expect 16 ok and NO skips. A lower count means a check is silently missing — do not accept 'all checks passed' alone."
  VERIFY_OK=1
fi

# ────────────────── 10. installed-file reconcile ──────────────
say "10. check-installed.sh"
if [ "$DRY_RUN" -eq 0 ]; then
  ct "$INSTALL_DIR/ops/check-installed.sh; echo \"exit=\$?\"" || true
  warn "exit=1 from the knapper.service site-config diff is expected. ZERO orphans is the thing to confirm."
fi

# ─────────────────────── 11. closeout ─────────────────────────
say "11. Closeout"
if [ "$DRY_RUN" -eq 0 ]; then
  ct "curl -s 127.0.0.1:3535/health | head -c 300; echo"
  ct "curl -s -o /dev/null -w 'public %{http_code}\n' $PUBLIC_URL"
  if [ -n "$MONITOR_SSH" ]; then
    say "Forcing a monitor run (proves the alert path is still wired)"
    mon "systemctl start $MONITOR_UNIT; journalctl -u $MONITOR_UNIT -n 8 --no-pager" || true
  else
    warn "KNAPPER_MONITOR_SSH unset — no forced monitor run; confirm the alert path another way"
  fi
fi

# ─────────── 11b. Retention — the reap that pairs with the sow ──────────
# The only deleting code in this file. Keeps exactly two tarballs: the build
# now running (verified this run) and the rollback target. Nothing is deleted
# unless verify passed in THIS run; the plan prints even under --dry-run.
say "11b. Retention — tarballs in $INSTALL_DIR (keep: running + rollback target)"
KEEP_A="$(basename "$ART")"
KEEP_B="knapper-$CUR_VER-linux-x64.tar.gz"
TGZ_ALL="$(ct "ls -1 $INSTALL_DIR/*.tar.gz 2>/dev/null || true")"
# shellcheck disable=SC2086
TGZ_DROP="$(retention_drops "$KEEP_A" "$KEEP_B" $TGZ_ALL | tr '\n' ' ')"
echo "   KEEP  $KEEP_A   (running, verified this run)"
if [ "$KEEP_A" = "$KEEP_B" ]; then
  warn "re-deploy of the running version: the previous build's tarball cannot be identified here — nothing is pruned"
elif printf '%s\n' "$TGZ_ALL" | grep -q "/$KEEP_B\$"; then
  echo "   KEEP  $KEEP_B   (rollback target)"
else
  warn "rollback tarball $KEEP_B is NOT on the host — keeping only the running build"
fi
# shellcheck disable=SC2086
ACTION="$(retention_action "$VERIFY_OK" "$DRY_RUN" "$NO_PRUNE" "$KEEP_A" "$KEEP_B" $TGZ_DROP)"
if [ "$ACTION" != same-version ]; then
  for f in $TGZ_DROP; do echo "   DROP  ${f##*/}"; done
fi
case "$ACTION" in
  same-version) : ;;  # said above: the previous build's tarball is unknown here
  nothing)      ok "already exactly the keep set — nothing to prune" ;;
  dry-run)      warn "dry-run: plan only, nothing deleted" ;;
  unverified)   warn "verify did not pass in this run — REFUSING to prune tarballs" ;;
  no-prune)     warn "--no-prune: nothing deleted — you now owe a manual prune" ;;
  prune)
    gate "Delete the DROP tarballs above? The KEEP lines stay."
    # shellcheck disable=SC2086
    ct "$(prune_command $TGZ_DROP)"
    ct "ls -1t $INSTALL_DIR/*.tar.gz" | sed 's/^/   now: /'
    ct "df -h $INSTALL_DIR | tail -1" | sed 's/^/   disk: /'
    ;;
  *) die "internal: unknown retention action '$ACTION' — nothing deleted" ;;
esac

cat <<EOF

────────────────────────────────────────────────────────────────
 CLOSEOUT — for your deployment record
────────────────────────────────────────────────────────────────
 upgraded      $CUR_VER  →  $NEW_VER
 artifact      $(basename "$ART")
 sha256        $LOCAL_SHA
 rollback      $ROLLBACK_TGZ  (existence checked; round trip untested until rehearsed)
 log           $LOG

 Record: which units differed and what you did with each; the doctor and
 verify ok-counts; whether check-installed reported zero orphans; the
 monitor's post-run state; and what retention actually deleted.
 ⛔ Do not write a step as done unless its output is in the log above.
────────────────────────────────────────────────────────────────
EOF
