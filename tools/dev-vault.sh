#!/bin/bash
# Generate a SYNTHETIC Obsidian-shaped vault for running Knapper by hand —
# a local or cloud dev server that an agent (or you) can drive through the
# real tools. The test suite does not use this; it builds its own fixtures.
#
#   tools/dev-vault.sh <dir>              # <dir> must not exist, or be empty
#   tools/dev-vault.sh --hazards <dir>    # add the fail-closed cases (below)
#   . <dir>/env.sh && dotnet run --project src/Knapper.Mcp
#
# Layout written:
#   <dir>/vault/   the vault (Vault__RootPath)
#   <dir>/state/   locks, audit log, metrics — OUTSIDE the vault, as required
#   <dir>/env.sh   the exports for a dev server over it (Sync__Mode=open)
#
# Everything in it is invented. This repository is public: never seed this
# script, or a vault it made, from a real vault's content.
#
# The output is deterministic (same bytes every run) so a behavior seen once
# can be reproduced. File mtimes are whatever the run makes them.
#
# The content is chosen to exercise what a real vault throws at Knapper:
# wikilinks with aliases, headings and block refs, embeds, attachments,
# frontmatter in every shape including broken, non-UTF-8 / CRLF / BOM notes,
# unicode names including the UTF-8-vs-UTF-16 ordering pair, a prefix that
# sorts between a folder and its child, enough matches to paginate, one
# instance of every lint finding, archive-prefix candidates, and hidden
# entries that must stay invisible on every surface.
#
# --hazards adds the cases that make Knapper refuse or degrade ON PURPOSE:
# a Sync conflict copy (mutations to its original block; /up answers 503),
# a symlink leaving the vault, a FIFO named *.md, and a file over the
# default Sync__MaxFileBytes ceiling. Off by default so a plain dev vault
# reports healthy.
set -euo pipefail

usage() {
    echo "usage: $0 [--hazards] <dir>" >&2
    exit 2
}

HAZARDS=0
TARGET=""
while [ $# -gt 0 ]; do
    case "$1" in
        --hazards) HAZARDS=1 ;;
        -h|--help) usage ;;
        -*) echo "unknown option: $1" >&2; usage ;;
        *) [ -z "$TARGET" ] || usage; TARGET=$1 ;;
    esac
    shift
done
[ -n "$TARGET" ] || usage

# ── Refuse anything that could be a real vault ───────────────────────────────
# The target must be new or empty: this script never writes over files that
# are already there. And it must not sit inside an existing Obsidian vault —
# pointing it at a folder in a synced replica would push invented notes to
# real devices.
if [ -e "$TARGET" ]; then
    [ -d "$TARGET" ] || { echo "refusing: $TARGET exists and is not a directory" >&2; exit 1; }
    [ -z "$(ls -A "$TARGET")" ] || { echo "refusing: $TARGET is not empty" >&2; exit 1; }
fi
PARENT=$(dirname "$TARGET")
[ -d "$PARENT" ] || { echo "refusing: parent directory $PARENT does not exist" >&2; exit 1; }
ROOT="$(cd "$PARENT" && pwd -P)/$(basename "$TARGET")"
d=$(dirname "$ROOT")
while :; do
    [ ! -d "$d/.obsidian" ] || { echo "refusing: $d is an Obsidian vault (has .obsidian/)" >&2; exit 1; }
    [ "$d" != "/" ] || break
    d=$(dirname "$d")
done

VAULT="$ROOT/vault"
STATE="$ROOT/state"
mkdir -p "$VAULT" "$STATE/locks"

# note <vault-relative path>  — writes stdin to the file, creating folders.
note() {
    mkdir -p "$(dirname "$VAULT/$1")"
    cat > "$VAULT/$1"
}

# bytes <vault-relative path> <printf format>  — exact bytes, no newline added.
bytes() {
    mkdir -p "$(dirname "$VAULT/$1")"
    # shellcheck disable=SC2059  # the format IS the content
    printf "$2" > "$VAULT/$1"
}

# ── Home and daily notes ─────────────────────────────────────────────────────
note "Home.md" <<'EOF'
---
aliases: [Start, Dashboard]
tags: [index]
---
# Home

Entry point for the dev vault. Nothing here is real.

## Projects
- [[Project Plan]] — the garden shed build ([[Project Plan#Milestones|milestones]])
- [[Reading List]]
- [[🚀 Launch Ideas]]

## People
- [[Ada Example]] and [[Grace Sample|Grace]]

## Daily
- [[2026-09-01]] through [[2026-09-07]]

#index #dev-vault
EOF

for day in 01 02 03 04 05 06 07; do
    note "Daily/2026-09-$day.md" <<EOF
---
date: 2026-09-$day
type: daily
mood: $(( (10#$day % 5) + 1 ))
---
# 2026-09-$day

## Tasks
- [ ] Water the tomatoes
- [x] Check the [[Project Plan]] budget
- [ ] Reply to [[Ada Example]] about the timber order

## Log
TODO: sort the shed screws by size.
Met [[Grace Sample]] for coffee; talked about [[Reading List]].

#daily
EOF
done

# ── A project folder: headings, block refs, embeds, attachments ─────────────
note "Projects/Garden Shed/Project Plan.md" <<'EOF'
---
title: Garden shed build
status: active
priority: 2
due: 2026-10-15
owner: "[[Ada Example]]"
tags:
  - project
  - outdoors
budget:
  timber: 420
  roofing: 180
  fixings: 55
---
# Project Plan

![[shed-sketch.png]]

## Milestones
1. Base and frame — needs [[Materials]] confirmed
2. Walls and roof
3. Paint and fit-out

## Budget
Total so far is 655. ^budget-total

| Item    | Cost | Owner             |
| ------- | ---- | ----------------- |
| Timber  | 420  | [[Ada Example]]   |
| Roofing | 180  | [[Grace Sample]]  |
| Fixings | 55   | —                 |

## Risks
- Weather in October (see [[2026-09-03]])
- Embedded budget line: ![[Project Plan#^budget-total]]
EOF

note "Projects/Garden Shed/Materials.md" <<'EOF'
# Materials

Full list in ![[materials.csv]]. Layout sketch on the canvas: [[Shed Board.canvas]].

Back to [[Project Plan#Budget|the budget]].
EOF

note "Projects/Garden Shed/materials.csv" <<'EOF'
item,quantity,unit,cost
2x4 timber,24,length,288
OSB sheet,6,sheet,132
Roofing felt,2,roll,180
Screws 5x60,1,box,55
EOF

note "Projects/Garden Shed/Shed Board.canvas" <<'EOF'
{
  "nodes": [
    {"id": "a1", "type": "file", "file": "Projects/Garden Shed/Project Plan.md", "x": 0, "y": 0, "width": 400, "height": 300},
    {"id": "b2", "type": "text", "text": "Order timber first", "x": 480, "y": 0, "width": 240, "height": 80}
  ],
  "edges": [
    {"id": "e1", "fromNode": "b2", "fromSide": "left", "toNode": "a1", "toSide": "right"}
  ]
}
EOF

# A real 1x1 PNG (valid signature, IHDR, IDAT, IEND).
bytes "Attachments/shed-sketch.png" '\211\120\116\107\015\012\032\012\000\000\000\015\111\110\104\122\000\000\000\001\000\000\000\001\010\002\000\000\000\220\167\123\336\000\000\000\014\111\104\101\124\170\234\143\360\232\360\010\000\002\344\001\275\211\225\120\165\000\000\000\000\111\105\116\104\256\102\140\202'

note "Projects/Reading List.md" <<'EOF'
---
tags: [reading]
---
# Reading List

| Title                 | Status   | Rating |
| --------------------- | -------- | ------ |
| The Timber Handbook   | reading  | 4      |
| Sheds of the World    | queued   |        |
| Paint Chemistry Basics | done    | 3      |

Notes on each go under [[Home]].
EOF

# ── People (aliases), and two notes sharing one name (ambiguous link) ───────
note "People/Ada Example.md" <<'EOF'
---
aliases: [Ada, A. Example]
role: carpenter
---
# Ada Example

Handles timber for the [[Project Plan]].
EOF

note "People/Grace Sample.md" <<'EOF'
---
aliases: [Grace]
role: friend
---
# Grace Sample

Recommends books for the [[Reading List]].
EOF

note "Work/Meeting.md" <<'EOF'
# Meeting

Weekly sync. Agenda lives in [[Home]].
EOF

note "Personal/Meeting.md" <<'EOF'
# Meeting

Book club meeting notes.
EOF

# ── Unicode names ────────────────────────────────────────────────────────────
note "Recipes/Café Crème Brûlée.md" <<'EOF'
---
tags: [recipe, dessert]
servings: 4
nutrition:
  kcal: 350
  sugar_g: 28
vegetarian: true
---
# Café Crème Brûlée

Crème, sucre, vanille. Serve with [[Grace Sample|Grace]].
EOF

note "Notes/日本語メモ.md" <<'EOF'
# 日本語メモ

検索のテスト用のメモです。needle
EOF

note "Notes/שלום עולם.md" <<'EOF'
# שלום עולם

Right-to-left name; content is left-to-right. needle
EOF

# The ordering pair: a non-BMP emoji (U+1F680) and a U+E000..U+FFFF
# character (U+FF21, fullwidth A). UTF-16 sorts the emoji FIRST (surrogates
# are 0xD8xx), UTF-8 sorts it LAST (0xF0 leads) — the divergence the cursor
# order has to get right.
note "Notes/🚀 Launch Ideas.md" <<'EOF'
# 🚀 Launch Ideas

- A shed-warming party 🎉
- needle
EOF

note "Notes/Ａ Fullwidth Title.md" <<'EOF'
# Ａ Fullwidth Title

Fullwidth letters sort after the emoji in UTF-16 and before it in UTF-8. needle
EOF

# "Notes-old" sorts BETWEEN "Notes" and "Notes/…" ('-' is 0x2D, '/' is 0x2F):
# the prefix-overlap case. Scope a query to Notes, Notes-old and Notes/Deep.
note "Notes-old/Old Scratch.md" <<'EOF'
# Old Scratch

Superseded scratch page. needle
EOF

note "Notes/Deep/Nested/Deep Note.md" <<'EOF'
# Deep Note

Three folders down. needle
EOF

# ── Frontmatter in every shape ───────────────────────────────────────────────
note "Frontmatter/scalars.md" <<'EOF'
---
title: "Scalars: quoted, with a colon"
count: 42
ratio: 0.75
done: false
when: 2026-09-01T10:15:00Z
nothing: null
---
Body.
EOF

note "Frontmatter/lists-and-maps.md" <<'EOF'
---
tags:
  - alpha
  - beta
inline_tags: [gamma, delta]
links:
  - "[[Home]]"
  - "[[Project Plan]]"
meta:
  source: invented
  depth:
    level: 2
---
Body.
EOF

note "Frontmatter/empty-block.md" <<'EOF'
---
---
Empty frontmatter block.
EOF

note "Frontmatter/none.md" <<'EOF'
No frontmatter at all.
EOF

note "Frontmatter/broken.md" <<'EOF'
---
status: [unclosed
tags: alpha
---
Broken YAML: must be reported as unparseable, never silently skipped.
EOF

note "Frontmatter/unterminated.md" <<'EOF'
---
status: open
This fence never closes.
EOF

# ── Encodings and line endings ───────────────────────────────────────────────
bytes "Encoding/legacy-latin1.md" 'caf\351 cr\350me \342 la Latin-1 \226 not UTF-8. needle\n'
bytes "Encoding/windows-crlf.md" '# CRLF note\r\n\r\nEvery line ends in CRLF.\r\nneedle\r\n'
bytes "Encoding/utf8-bom.md" '\357\273\277# BOM note\n\nStarts with a UTF-8 byte-order mark. needle\n'
bytes "empty.md" ''

# ── Pagination: 5 files x 15 lines = 75 "needle" matches, plus the ones above
for f in 0 1 2 3 4; do
    {
        for l in $(seq 0 14); do
            echo "line $l needle $f"
        done
    } | note "Search/needles-$f.md"
done

# ── One instance of every lint finding ───────────────────────────────────────
note "Lint Demo.md" <<'EOF'
# Lint Demo

Each line below is one finding `vault_lint` should report.

- unresolved_link: [[Does Not Exist]]
- broken_anchor: [[Project Plan#No Such Heading]]
- ambiguous_link: [[Meeting]] (Work/Meeting.md and Personal/Meeting.md)

A table right under a paragraph, with no blank line (table_needs_blank_line):
| Step | Note                                  |
| ---- | ------------------------------------- |
| 1    | Obsidian renders this as a paragraph  |

A real table whose aliased link splits its row (table_pipe):

| Link                      | Note                     |
| ------------------------- | ------------------------ |
| [[Project Plan|the plan]] | the alias pipe unescaped |
EOF

# ── Archive-prefix candidates ────────────────────────────────────────────────
# Set Vault__ArchivedPrefixes__0=Archive (see env.sh) to exercise them.
# "Archived Recipes" must NOT be claimed by the "Archive" prefix.
note "Archive/2025 Retro.md" <<'EOF'
# 2025 Retro

Superseded; reachable only by search. needle
EOF

note "Archive/Old Plan.md" <<'EOF'
# Old Plan

An earlier [[Project Plan]]. needle
EOF

note "Archived Recipes/Toast.md" <<'EOF'
# Toast

Not archived: "Archive" does not claim this folder. needle
EOF

# ── Non-note files ───────────────────────────────────────────────────────────
note "scripts/backup.sh" <<'EOF'
#!/bin/sh
echo "needle: a script living in the vault"
EOF
bytes "raw/blob.bin" '\000\001needle\000\377'

# ── Hidden entries: must be invisible to EVERY listing and search ───────────
note ".obsidian/app.json" <<'EOF'
{"attachmentFolderPath": "Attachments", "needle": true}
EOF
note ".obsidian/workspace.json" <<'EOF'
{"main": {"id": "dev", "type": "split"}, "needle": "workspace"}
EOF
note ".obsidian/plugins/example-plugin/manifest.json" <<'EOF'
{"id": "example-plugin", "name": "Example", "version": "0.0.1"}
EOF
note ".trash/Deleted Note.md" <<'EOF'
needle in the trash
EOF
note ".hidden.md" <<'EOF'
needle in a hidden file at the root
EOF
note "Notes/.drafts/draft.md" <<'EOF'
needle in a hidden folder
EOF

# ── Hazards (opt-in) ─────────────────────────────────────────────────────────
if [ "$HAZARDS" -eq 1 ]; then
    # Sync conflict copy: blocks mutations to Work/Meeting.md and to itself;
    # /health reports it and /up answers 503 until it is removed.
    note "Work/Meeting (Conflicted copy 2026-09-02 101500).md" <<'EOF'
# Meeting

The other device's version of the weekly sync.
EOF

    # A symlink leaving the vault. Every surface rejects or skips it.
    echo "outside the vault" > "$ROOT/outside.md"
    mkdir -p "$VAULT/Hazards"
    ln -s ../../outside.md "$VAULT/Hazards/outside-link.md"

    # A FIFO named like a note: any read that does not classify first hangs.
    mkfifo "$VAULT/Hazards/pipe.md"

    # One byte over the default Sync__MaxFileBytes (5,242,880): Obsidian Sync
    # would silently never deliver it. /health lists it; /up stays 200.
    head -c 5242881 /dev/zero | tr '\0' 'x' > "$VAULT/Hazards/oversized.md"
fi

# ── env.sh ───────────────────────────────────────────────────────────────────
cat > "$ROOT/env.sh" <<EOF
# Source me: . $ROOT/env.sh
# Dev server over the generated vault. Sync__Mode=open is the dev-only
# opt-out of the sync gate; never use it against a real vault.
export Vault__RootPath='$VAULT'
export Vault__LockDirectory='$STATE/locks'
export Vault__AuditLogPath='$STATE/audit.jsonl'
export Vault__MetricsPath='$STATE/metrics.json'
export Sync__Mode=open
# Uncomment to treat Archive/ as an archived prefix:
# export Vault__ArchivedPrefixes__0=Archive
EOF

count=$(find "$VAULT" -type f -not -path '*/.*' | wc -l | tr -d ' ')
echo "dev vault: $VAULT ($count visible files$([ "$HAZARDS" -eq 1 ] && echo ', with hazards'))"
echo "run:  . '$ROOT/env.sh' && dotnet run --project src/Knapper.Mcp"
