#!/bin/sh
# THE build identity, in one place. Every other consumer calls this script:
# the MSBuild stamping target in Directory.Build.props (which turns the
# revision into AssemblyInformationalVersion, and thence into
# initialize.serverInfo.version, /health and /up), ops/publish.sh (tarball
# name), and ops/release.sh (bump target).
#
# Modes:
#   ops/version.sh              → 0.2.0                    the <Version> alone
#   ops/version.sh --revision   → g1f5ff1c.dirty           the build-identity suffix
#   ops/version.sh --full       → 0.2.0+g1f5ff1c.dirty     what the binary reports
#
# Why the revision exists at all: <Version> alone cannot distinguish the
# tagged release from a tarball someone built off a dirty tree an hour later.
# Both would name themselves 0.2.0 in the artifact filename AND at every
# runtime surface, so a wrong binary in production reports the right version
# and nothing anywhere disagrees. The suffix makes that mismatch visible —
# `knapper verify --url --expect-version` is what turns visible into checked.
#
# FAIL-SOFT on the revision, deliberately: no git, no repo (a source tarball),
# or a repo with no commits yet all print an EMPTY revision and exit 0. The
# version is a build input; refusing to build outside a git checkout would
# make the source archive unbuildable to buy nothing. A missing revision is
# self-describing — "0.2.0" with no suffix means "built somewhere that could
# not say where from".
#
# NOT fail-soft on <Version>: it is the one thing this repo controls, and a
# malformed one silently ships an artifact named "knapper--linux-x64.tar.gz".
set -eu

cd "$(dirname "$0")/.."

# One <Version> element, exactly. `sed -n s///p` prints once per match, so two
# elements produce two lines and the count check below catches it — a merge
# that duplicated the property would otherwise resolve to whichever line the
# consumer happened to read.
VERSION=$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' Directory.Build.props)
LINES=$(printf '%s' "$VERSION" | grep -c '' || true)
if [ "$LINES" != "1" ]; then
    echo "version.sh FAILED: Directory.Build.props must hold exactly one <Version> element (found $LINES)" >&2
    exit 1
fi
case "$VERSION" in
    [0-9]*.[0-9]*.[0-9]*) ;;
    *) echo "version.sh FAILED: <Version> is '$VERSION', expected MAJOR.MINOR.PATCH" >&2
       exit 1 ;;
esac

revision() {
    command -v git >/dev/null 2>&1 || return 0
    git rev-parse --is-inside-work-tree >/dev/null 2>&1 || return 0
    sha=$(git rev-parse --short=7 HEAD 2>/dev/null) || return 0   # no commits yet
    # `git status --porcelain` honors .gitignore, so bin/, obj/ and artifacts/
    # never make a build "dirty". Untracked files DO — an untracked .cs is
    # globbed into the compilation, so it is a real difference from the tag.
    if [ -n "$(git status --porcelain 2>/dev/null)" ]; then
        printf 'g%s.dirty' "$sha"
    else
        printf 'g%s' "$sha"
    fi
}

case "${1:---version}" in
    --version) printf '%s\n' "$VERSION" ;;
    --revision) revision; echo ;;
    --full)
        REV=$(revision)
        if [ -n "$REV" ]; then printf '%s+%s\n' "$VERSION" "$REV"; else printf '%s\n' "$VERSION"; fi
        ;;
    *) echo "usage: $0 [--version|--revision|--full]" >&2; exit 2 ;;
esac
