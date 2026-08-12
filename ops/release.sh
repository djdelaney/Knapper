#!/usr/bin/env bash
# Bump THE Knapper version and cut a release.
#
# There is exactly ONE version carrier — <Version> in Directory.Build.props —
# and it reaches every surface from there: all four assemblies are stamped from
# it, Knapper.Core.BuildInfo reads the stamp, and initialize.serverInfo.version,
# /health, /up and `knapper version` all report BuildInfo. Nothing else in the
# repo spells a version, which is why this script has no lockstep problem to
# solve and must never grow a second carrier: adding one means adding a way for
# the two to disagree. (ops/version.sh computes the identity; the MSBuild target
# in Directory.Build.props stamps it; VersionSurfaceTests pins the surfaces.)
#
# Version semantics:
#   --patch (default)  anything that does not change a client-facing contract
#   --minor            MCP tool-surface change (a name, a shape, a new tool), a
#                      new error code, or a config knob deployments must set.
#                      Tool names are a client contract: renames are minor bumps,
#                      not refactors.
#   --major            reserved; nothing has earned it yet
#
# Usage:
#   ops/release.sh [--patch|--minor|--major] [--no-commit]
#   ops/release.sh [--patch|--minor|--major] --ship [--yes]
#
# Flow (default): run this, push to main, wait for green CI, then run the
# printed tag commands. Only tag commits that already passed CI on main.
#
# Flow (--ship): automates that discipline end to end — push the bump to main,
# poll the CI run for THIS commit until it completes, and tag + push v<version>
# ONLY if it went green. A red/cancelled/timed-out run aborts before tagging
# (the bump commit stays on main, just untagged, ready to retry once green).
# Requires `gh` authenticated and the current branch to be main.
# --yes skips the confirmation prompt.
#
# What this script does NOT do: build or ship anything. Knapper's artifact is a
# tarball built by ops/publish.sh and installed by hand (ops/ct106-runbook.md),
# so publishing stays a deliberate act. Build it AFTER tagging, from the clean
# tagged tree — that is what makes the artifact name and the version the service
# reports say "v0.2.0" rather than "0.2.0+g<sha>.dirty".

set -euo pipefail

PART="patch"
COMMIT=1
SHIP=0
ASSUME_YES=0
for arg in "$@"; do
    case "$arg" in
        --patch) PART="patch" ;;
        --minor) PART="minor" ;;
        --major) PART="major" ;;
        --no-commit) COMMIT=0 ;;
        --ship) SHIP=1 ;;
        --yes|-y) ASSUME_YES=1 ;;
        -h|--help)
            sed -n '2,/^$/p' "$0" | sed 's/^# \?//'
            exit 0
            ;;
        *)
            echo "Unknown argument: $arg" >&2
            echo "Usage: $0 [--patch|--minor|--major] [--no-commit]" >&2
            echo "       $0 [--patch|--minor|--major] --ship [--yes]" >&2
            exit 2
            ;;
    esac
done

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

CARRIER=Directory.Build.props

# --ship tags THIS commit, so there must be one; and it must be on main, the
# branch CI gates on.
if [[ $SHIP -eq 1 ]]; then
    if [[ $COMMIT -eq 0 ]]; then
        echo "ERROR: --ship needs a commit to push and tag; drop --no-commit." >&2
        exit 2
    fi
    if ! command -v gh >/dev/null 2>&1; then
        echo "ERROR: --ship needs the GitHub CLI (gh). Install it or run without --ship." >&2
        exit 2
    fi
    if ! gh auth status >/dev/null 2>&1; then
        echo "ERROR: gh is not authenticated (run 'gh auth login')." >&2
        exit 2
    fi
    branch="$(git rev-parse --abbrev-ref HEAD)"
    if [[ "$branch" != "main" ]]; then
        echo "ERROR: --ship must run on main (on '$branch'). Releases are cut from main." >&2
        exit 2
    fi
fi

# Refuse to fold an unrelated uncommitted edit to the carrier into the bump
# commit (or, with --no-commit, to bump on top of one).
if ! git diff --quiet HEAD -- "$CARRIER"; then
    echo "ERROR: uncommitted changes in $CARRIER — commit or stash them first." >&2
    exit 1
fi

# version.sh validates the shape and the "exactly one <Version> element" rule,
# so by here CURRENT is known-good MAJOR.MINOR.PATCH.
CURRENT="$(./ops/version.sh --version)"
IFS=. read -r MAJOR MINOR PATCH <<<"$CURRENT"
case "$PART" in
    major) NEW_VERSION="$((MAJOR + 1)).0.0" ;;
    minor) NEW_VERSION="${MAJOR}.$((MINOR + 1)).0" ;;
    patch) NEW_VERSION="${MAJOR}.${MINOR}.$((PATCH + 1))" ;;
esac
TAG="v${NEW_VERSION}"

# A tag that already exists means the bump target is taken — most likely the
# last release was cut without the version being bumped afterwards, or two
# people are releasing at once. Stop before writing anything: pushing a second
# v0.2.0 is refused by the remote anyway, but only after the commit exists.
if git rev-parse -q --verify "refs/tags/${TAG}" >/dev/null; then
    echo "ERROR: tag ${TAG} already exists — ${CURRENT} was already released, or the bump is duplicated." >&2
    exit 1
fi

# Targeted rewrite of the version text only: the file also carries the platform
# attributes and the stamping target, and a reformat would churn them. Written
# via a temp file rather than `sed -i`, whose syntax differs between the BSD sed
# on the dev Mac and the GNU sed in CI.
TMP="$(mktemp "${CARRIER}.XXXXXX")"
trap 'rm -f "$TMP"' EXIT
sed "s|<Version>${CURRENT}</Version>|<Version>${NEW_VERSION}</Version>|" "$CARRIER" >"$TMP"
if ! grep -q "<Version>${NEW_VERSION}</Version>" "$TMP"; then
    echo "ERROR: rewriting <Version> in $CARRIER produced no change — the file is not in the expected shape." >&2
    exit 1
fi
cat "$TMP" >"$CARRIER"
rm -f "$TMP"
trap - EXIT
echo "→ Bumped $CARRIER: ${CURRENT} → ${NEW_VERSION}"

# Read it back through the same resolver every consumer uses. A rewrite that
# produced something version.sh rejects must not survive to the commit.
CONFIRMED="$(./ops/version.sh --version)"
if [[ "$CONFIRMED" != "$NEW_VERSION" ]]; then
    echo "ERROR: after the rewrite, ops/version.sh reports '${CONFIRMED}', not '${NEW_VERSION}'." >&2
    exit 1
fi

echo
if [[ $COMMIT -eq 1 ]]; then
    git commit -q -m "Bump version to ${NEW_VERSION}" -- "$CARRIER"
    echo "✓ Committed bump to ${NEW_VERSION}"
else
    echo "✓ Bumped to ${NEW_VERSION} (not committed)"
fi

# --- deploy note, printed either way ---------------------------------------
deploy_note() {
    cat <<EOF

Deploy (ops/ct106-runbook.md §5, upgrade path in §9):

    git checkout ${TAG} && git status --porcelain    # must print NOTHING
    ops/publish.sh                                   # → artifacts/knapper-${NEW_VERSION}+g<sha>-linux-x64.tar.gz
    # snapshot the CT, scp, unpack, restart, then prove the restart took:
    knapper verify --url https://<host>/ --expect-version ${NEW_VERSION}

Publish from the clean tagged tree: a dirty tree stamps the artifact AND the
running service '.dirty', and --expect-version ${NEW_VERSION} refuses it.
EOF
}

if [[ $SHIP -eq 0 ]]; then
    echo
    if [[ $COMMIT -eq 1 ]]; then
        echo "Next: push this commit to main, wait for green CI, then cut the release:"
    else
        echo "Next: commit the bump, push to main, wait for green CI, then cut the release:"
    fi
    echo
    echo "    git tag -a ${TAG} -m \"Knapper ${NEW_VERSION}\""
    echo "    git push origin ${TAG}"
    echo
    echo "(Or re-run with --ship to push, wait for green CI, and tag automatically.)"
    deploy_note
    exit 0
fi

# --- --ship: push, wait for green CI on THIS commit, then tag --------------
SHA="$(git rev-parse HEAD)"
echo
echo "About to:  push main → wait for CI on ${SHA:0:12} to go green → tag ${TAG} + push."
if git -c core.pager=cat status --porcelain | grep -q .; then
    echo "Note: working tree has uncommitted changes; only the committed HEAD is pushed/tagged."
fi
if [[ $ASSUME_YES -ne 1 ]]; then
    read -rp "Proceed? [y/N] " reply || reply=""
    case "$reply" in
        [yY]|[yY][eE][sS]) ;;
        *) echo "Aborted. The bump commit is local (unpushed) — push and tag by hand when ready."; exit 0 ;;
    esac
fi

echo "→ Pushing main…"
git push origin HEAD

# The CI run is created a few seconds after the push (webhook latency); wait for
# it to appear before watching it. `gh run list -c <sha>` filters to this exact
# commit, so we never watch someone else's run and call it ours.
run_field() { gh run list -c "$SHA" -w ci.yml -b main --json "$1" --limit 1 -q "$2" 2>/dev/null || true; }

echo "→ Waiting for the CI run to appear…"
appear_deadline=$(( $(date +%s) + 180 ))
while :; do
    count="$(run_field databaseId 'length')"
    [[ "$count" == "1" ]] && break
    if [[ "$(date +%s)" -ge "$appear_deadline" ]]; then
        echo "ERROR: no CI run appeared for ${SHA:0:12} within 3 min." >&2
        echo "       The commit is pushed. Check GitHub Actions, then tag by hand once green:" >&2
        echo "         git tag -a ${TAG} -m \"Knapper ${NEW_VERSION}\" && git push origin ${TAG}" >&2
        exit 1
    fi
    sleep 5
done

RUN_URL="$(run_field url '.[0].url')"
echo "→ Watching CI: ${RUN_URL}"
watch_deadline=$(( $(date +%s) + 1800 ))
conclusion=""
while :; do
    # Read status + conclusion from ONE snapshot so the success check cannot
    # race a transient gh failure between two separate fetches. Empty output
    # (a gh hiccup) leaves both blank → the loop just polls again.
    read -r status conclusion <<< "$(run_field 'status,conclusion' '.[0] | "\(.status) \(.conclusion // "")"')"
    [[ "$status" == "completed" ]] && break
    if [[ "$(date +%s)" -ge "$watch_deadline" ]]; then
        echo "ERROR: CI still '${status:-unknown}' after 30 min — NOT tagging. See ${RUN_URL}." >&2
        exit 1
    fi
    echo "   CI ${status:-queued}…"
    sleep 30
done

if [[ "$conclusion" != "success" ]]; then
    echo "ERROR: CI concluded '${conclusion:-unknown}' — NOT tagging. See ${RUN_URL}." >&2
    echo "       Fix, push a green commit, and re-run ops/release.sh --ship (or tag by hand)." >&2
    exit 1
fi

echo "✓ CI green. Tagging ${TAG}…"
git tag -a "${TAG}" -m "Knapper ${NEW_VERSION}"
git push origin "${TAG}"
echo "✓ Pushed ${TAG}."
deploy_note
