# Proposal: vault lint

**Status: proposed, not built. Written 2026-08-24.** Nothing here describes
shipped behavior; if you are looking for what Knapper does today, read
[architecture.md](../architecture.md) and [usage.md](../usage.md). This
document exists so the shape is decided before anyone touches
`ToolSurface.All`, because a tool name is a locked contract from the moment
it ships.

## 1. The problem this addresses

Knapper turns distributed agent concurrency into one server-side transaction
problem, and does it well: every mutation is SHA-preconditioned under a
cross-process lock, verified by reopen-and-byte-compare, audited, and (since
2026-08-14) committed to git on a 30-minute timer.

**None of that makes a vault consistent.** Conditional writes prevent *lost
updates*; they do not prevent *semantic divergence*. Two perfectly
serialized, non-conflicting writes to two different notes still leave one
fact stated two ways, and git answers "what changed?" but never "what should
have changed alongside it". A renamed note leaves dangling links in eleven
others and every one of those writes was correct.

That gap is the whole subject here. It is not a concurrency bug, so the
transaction layer cannot close it, and it is not a syntax error, so nothing
loud ever fires.

## 2. Prior art, and what it already paid for

**`notes-drift.py`** (in Dan's vault, `Tech/Homelab/`, written 2026-07-30 in
response to concurrent-agent clobbering) is the working prototype of this
idea. Its checks: stale values still stated as current, broken heading
anchors, broken wikilinks, checkbox/prose disagreement, structural integrity,
frontmatter freshness. It is silent on success and exits non-zero on a
finding, matching the homelab monitor posture.

It earned three things worth keeping, all of which cost real effort to learn:

- **A precision doctrine.** Its first cut flagged any fact whose values
  disagreed across notes: 3 real findings out of 12 on a corpus that had just
  been cleaned. Its own design note calls a monitor at 25% precision "one you
  learn to ignore". That is the acceptance bar for anything default-on here.
- **The false-positive classes of wikilink checking** (§8). The first draft
  produced 93 findings on a clean vault, roughly 85 of them junk.
- **Evidence the checks find real defects.** The broken-link check surfaced
  68 dangling links vault-wide on first run, cleared to 14 across two passes.

It has also decayed in exactly the way this design must not, and the failure
is instructive rather than embarrassing: its hand-maintained stale-value list
still tracks a Mailvec pin of `v0.1.29`–`v0.1.35` with the reason "running
v0.1.36 as of 2026-08-01", while that stack has been pinned to `v0.6.0` since
2026-08-11. A stale-fact monitor whose facts are stale is the alarm it warns
about. Note what actually retired that entry: `mailvec doctor` now verifies
pin-versus-running itself. **The durable fix for an assertion is usually to
make the system report the fact, not to track its old value in a list** — so
assertions are a tier, never the foundation.

Two more facts about the prototype bear on the design. It has no exec bit in
the vault, so the documented `./notes-drift.py` invocation exits **126** —
which this posture cannot distinguish from a real finding. And since the §9
cutover it is unrunnable by agents at all: it is a filesystem walker over a
vault that agents no longer reach.

**`ops/runbook-lint.sh`** is the second piece of prior art, and it is this
repo's own. It exists because six review rounds of careful human reading
still missed "a fact corrected in one of the two places it lived". Its
docstring draws the boundary this proposal inherits verbatim: it cannot check
whether the procedure is *right*; it checks that the document is internally
consistent *with itself*.

## 3. Shape: one engine, two surfaces

Behavior lives in Core as a query-shaped service returning
`QueryEnvelope<LintFinding>`. Two thin surfaces over it, per the existing
layering:

- **`vault_lint`** — a read-only MCP tool. The 14th.
- **`knapper lint`** — a CLI subcommand beside `doctor` / `status` /
  `audit-tail` / `verify`, silent on success, non-zero on findings, for the
  `ops/monitor/` timer.

### Why an MCP tool and not CLI-only

The first draft of this proposal argued CLI-only, on the grounds that the
consumer is the monitor. That argument is wrong under MCP-only routing. The
agent cannot shell out — that is the point of the cutover — so a CLI-only
lint withholds the check from the one party most able to introduce drift, in
the same session where a fix is cheapest. The vault's own rule is already
"verify by content, not receipt"; this is that rule made cheap enough to
actually follow.

Nothing in the brief's §15 prohibitions touches a read-only derived query. It
adds no mutation, no fallback path, no unconditional write.

Compiling it also disposes of the exit-126 class of failure permanently: a
subcommand has no exec bit to lose to Obsidian Sync.

## 4. Tiers

The tier boundary is precision, not subject matter.

| Tier | Default | Checks | Configuration |
|---|---|---|---|
| **1 · Structural** | on | broken wikilinks; broken heading anchors and block refs; unbalanced code fences; empty file; file ending blank (possible truncation); unparseable frontmatter | none |
| **2 · Heuristic** | off | checkbox versus prose disagreement; `updated:` frontmatter freshness | vocabulary overridable |
| **3 · Assertions** | off | user-supplied stale-value patterns | a vault-side file |

**Tier 1 is a product feature, not a homelab one.** "Which links do not
resolve?" is what Obsidian's own unresolved-links pane answers; every vault
wants it, and none of it encodes anybody's facts. It needs a link graph over
filename, frontmatter aliases, heading anchors and block ids — which is the
same substrate as the "Obsidian-flavored queries (backlinks, tags-as-index)"
idea already scoped in [extending.md](../extending.md). If lint is built,
that idea is half-built as a side effect; decide deliberately whether to
expose the graph as its own query capability or keep it internal.

**Tier 2 is where taste lives.** The prototype's done/pending/conditional
word lists are Dan's vocabulary — `done`, `complete`, `proven` against
`deferred`, `not yet`, `pending`, `TBD`. Another vault says "shipped" and
"blocked". Ship the lists as defaults, make them configurable, leave the tier
off. Two of the prototype's exclusions are hard-won and should ship as
defaults with their reasoning attached: `closed` and `resolved` are **not**
done-words (they describe state as often as completion — "port still
closed"), and neither is `verified` (checklists are full of "X verified"
written as the thing still to do).

The `updated:` check changes substrate here. The prototype compares
frontmatter against file mtime with a one-day grace for timezone skew,
because mtime was the only evidence it had — and on a synced replica mtime is
when Sync *downloaded*, not when anyone edited. Knapper has git: compare
against the last commit that touched the file. Strictly better evidence,
immune to sync-touch, and available for free on the deployment where lint
would run.

**Tier 3 is the stale-value list, as data rather than code.** Entries are
`{name, pattern, reason, scope}` — the prototype's shape exactly, including
its optional single-note restriction, which exists because a phrase can be
stale in one context and current in another ("Live site today | Azure" was
stale for one site and correct for the other). Two properties make this safe
and cheap:

- **Patterns run through the existing ripgrep layer.** Rust regex is linear
  time with no backtracking, so a user-supplied pattern cannot become a
  denial of service against the server. No new engine, no new sandbox
  question — and the constrained-args discipline of `vault_search` already
  covers passing patterns from data into ripgrep safely.
- **The file lives in the vault**, so it is edited through `vault_edit` like
  everything else, syncs to every device, and versions in the same git
  history as the notes it describes.

Tier 3 also needs the prototype's "this line is history" suppressor —
strikethrough, superseded, deprecated, "no longer", `was`/`were`,
"previously". Configurable vocabulary, same as tier 2. One entry in that list
generalizes past this vault and should be a default: **advice against a value
reads exactly like the value** ("not a bare A record to 1.2.3.4"), so
`not a`, `instead of`, `rather than`, `avoid` suppress a match.

## 5. Baseline against git — the load-bearing idea

**Findings are reported relative to a baseline commit; by default only
findings absent from the baseline are returned.**

This is the feature that makes everything else adoptable, and it is the one
piece that only works inside Knapper.

### The problem it solves

A lint over a vault with a past answers *"has this vault ever been
imperfect?"*. The question worth putting on a timer is *"did the last session
break something?"*. The first answer drowns the second, and it does so
permanently.

This vault is already in the failure state. The prototype's posture — silent
on success, non-zero on a finding — is worth having only because a non-zero
exit is **news**. With 14 dangling links outstanding, the checker exits
non-zero on every run, forever, whatever anyone did or did not do. The exit
code carries no information at all: a session that broke three links an hour
ago is indistinguishable from the fourteen that have been sitting there for
weeks. That is a monitor that is dead while still running, and the vault
roadmap has already recorded the symptom in its own words — *silence = clean
no longer holds*.

Note that precision is not the problem. All 68 findings on the first run were
real. **Volume plus permanence** is the problem, and no amount of tuning
touches it.

### Why not an ignore list

The reflex fix — the one the vault roadmap already floats — is a list of
acknowledged findings that are suppressed. It works, and it has three
defects:

- It is hand-maintained, so it decays. Demonstrated in the same file: the
  `STALE` list decayed within weeks (§2). **An ignore list is a stale-value
  list wearing a different hat.**
- It does not self-clear. Fix a link and the ignore entry survives, now
  suppressing a finding that can no longer occur — and the symptom of a wrong
  ignore entry is silence, so nothing ever surfaces it.
- Somebody has to write it, and rewrite it whenever intent changes.

### What the baseline does instead

The same suppression, derived rather than curated. Run the checks over the
baseline tree, run them over the working tree, report the difference:

```
findings(HEAD) - findings(baseline) = what changed under you
```

| | dangling links |
|---|---|
| findings at baseline | 14 |
| findings now | 15 |
| **reported** | **1** — the one this session introduced |

Nobody wrote anything down. Fix a backlog finding and it simply leaves both
sets, with no residue to clean up. And because "since commit X" is also
"introduced by these commits", a finding arrives with provenance — the audit
log names the caller behind the write.

Silence now means *nothing new broke*, which is a claim worth mailing about,
rather than *this vault has never been perfect*, which is not.

This is also what makes tiers 2 and 3 shippable to somebody who is not Dan.
Any heuristic carries a false-positive rate; against a decade-old vault that
is hundreds of findings on day one and an uninstall by day two. Baseline-first
makes a new user's first run silent **by construction**, so the only findings
they ever see are consequences of their own edits. It converts "audit my whole
vault", which is a project, into "did I just break something?", which is a
check — and only the second one belongs on a timer.

### Why it is Knapper-only

A standalone checker sees one snapshot of a filesystem and has no past to
compare against. Not because it could not shell out to git, but because **the
vault has a history at all only because Knapper made one**: `git-init` on
2026-08-14 plus the commit timer are what turned "the vault" into "the vault
as it stood 30 minutes ago", and that second thing is the entire input here.

### The two ways to get it wrong

Both are silent, which is why they are specified rather than left to the
implementation.

**Do not key a finding by line number.** Key on
`(check, path, normalized subject)` — the link target, the anchor text, the
checkbox body. Insert a paragraph at the top of a note and every finding
below it moves; a line-keyed baseline calls them all new. The result is a
flood that looks exactly like real drift, produced by an edit that changed
nothing.

**Do not advance the baseline automatically.** Tempting, and it inverts the
tool: a new finding is reported once, becomes baseline, and is never mentioned
again. Report-once means the monitor forgives everything exactly once — which,
for anything not acted on the same day, means it forgives everything.
Re-baselining is an explicit operation (`knapper lint --accept`), never
implicit in a run, so a real finding nags until it is fixed or accepted.

### The cost, stated

The backlog is invisible unless asked for (`--all`). That is deliberate:
clearing 14 dangling links where *"Mayapple"* means four different things is a
project to be scheduled and thought about, not something a timer should raise
at 02:30. Reporting is the timer's job; auditing is not.

## 6. Scope, budget, envelope

Contract obligations, not preferences:

- **Every response carries the completeness envelope** — `truncated` +
  cursor, generation span, `changedDuringQuery`. "No findings" must mean the
  named scope was exhaustively checked, or the response must say it was not.
  A silent partial pass here is worse than no lint, because it manufactures
  the exact false confidence the tool exists to remove.
- **Scope is an argument, and the agent default is narrow.** Knapper's audit
  log knows which paths *this caller* wrote this session, so `scope:
  "myWrites"` — lint what I just touched — is the natural default for
  `vault_lint`, and it is nearly free. Whole-vault sweeps are the monitor's
  job, on a timer, from the CLI.
- **A time budget, with the expiry surfaced.** Follow the
  `OversizedFiles.DefaultBudget` precedent — and its lesson: that budget was
  shipped without a config knob deliberately, but only because the condition
  self-announces. Whatever budget lint gets must announce its own expiry the
  same way, and the knob decision should be made at the same time rather than
  discovered in production.

## 7. Findings are observations, not a work list

The steering risk is concrete and this vault is the worked example. The
roadmap carries an explicit prohibition on bulk-fixing dangling links,
because *"Mayapple"* means the house, the neighbourhood, the network and the
HOA depending on context, so each cluster needs a decision about **intent**,
not a find-and-replace. A tool that hands an agent a tidy list of "problems"
in someone's personal vault is an invitation to unrequested writes.

So: the tool description says findings are observations for the user, that
fixing them is not implied by finding them, and that a cluster of related
findings usually means a decision rather than an edit. The description is the
steering surface, and §8b's behavioral tests are how that claim gets checked
— a lint tool shipping without an §8b case for "does it start editing?" is
shipping the risk untested.

## 8. Port the false positives, not the code

The expensive knowledge in the prototype is not its structure, which is a few
hundred lines of regex, but the classes of thing that *look* like findings
and are not. A from-scratch implementation re-learns these one production
false alarm at a time. They should land as test fixtures before the checks
land:

- **Bash test syntax inside code fences reads as a wikilink** — `[[ -t 1 ]]`.
  Strip fenced and inline code before link extraction.
- **Escaped pipes in tables leave a trailing backslash** — `[[Note\|Alias]]`
  yields a target of `Note\`, which resolves to nothing.
- **Obsidian resolves by frontmatter alias as well as filename**, so an alias
  index is required or every aliased link is a false positive.
- **Embeds are not links for this purpose** — `![[image.png]]` and other
  attachments are not notes and must not be reported as missing ones.
- **A date written in a note is a statement about a date**, not evidence the
  note was edited that day. The prototype's freshness check fired twice on
  one day from body-scanned dates: once on a history entry, once on a
  schedule that merely became past when the clock rolled over.
- **A struck line is a deliberate historical statement**, not drift.

## 9. Configuration

Split by what changes and who owns it:

- **`Lint:*` in appsettings** — tier toggles, budget, vocabulary overrides,
  and the path to the assertions file. Deployment state, same as every other
  knob.
- **The assertions file, in the vault** — user data, edited through the
  mutation tools, synced, versioned with the notes.

⚠️ **The assertions file must not live in a dotfolder.** Obsidian Sync
ignores dotfolders (which is exactly why `.git` survives in the vault), and
Knapper's own query layer never lists or searches hidden entries. A default
of `.knapper/lint.yml` would therefore produce a file that does not sync, is
invisible to `vault_files`, and cannot be found by the agent asked to edit
it — three silent failures from one convention. Default to **absent** (tier
off), and require an explicit ordinary path.

## 10. Deliberately not built

- **A "same fact, two live values" heuristic.** The prototype has one behind
  `STRICT=1` and its own docstring calls the precision unacceptable; the two
  facts hardcoded into it have both since moved on. Assertions cover the same
  ground with the user's judgment in the loop.
- **Fix-it / autofix.** See §7. The one thing this must never do.
- **Cross-note fact extraction of any kind.** Checking whether a document is
  internally consistent is a solvable problem; checking whether it is *true*
  is not this tool's job, per `runbook-lint.sh`'s boundary.
- **A second suppression mechanism.** Inline `<!-- knapper-lint: ignore -->`
  comments are attractive and would overlap the baseline almost entirely. One
  mechanism or the other; see open decisions.

## 11. Known blind spot

Lint on CT 106 inherits the deployment's known one: Obsidian Sync's ~5 MB
per-file ceiling is symmetric, so a note over it never reaches the container,
and the vault Knapper serves is a strict subset of Helios with nothing local
saying so. "Clean" therefore means clean over what Knapper can see. This is
recorded, not solved, and is the same open item as
[extending.md](../extending.md) "Files Helios has that CT 106 does not" — if
that gets an answer, lint gets it too.

## 12. Cost of shipping the tool

Not free, and worth stating so the decision is priced:

- Core service + typed finding record + envelope wiring.
- Tool class, `ToolSurface.All` registration, and the name locked forever
  from first ship.
- `ToolManifestTests` conformance case, wire round-trip through
  `McpSurfaceTests.ConnectAsync`, Core semantics tests.
- An §8b behavioral case for the steering claim in §7.
- Client-side: the tool count in `verify` moves 13 → 14, and the runbook's
  expected-tools assertion with it.
- A minor version bump; tool-surface changes are minor by house rule.

## 13. Open decisions

- **Tier 1 default-on, or off until measured?** Recommendation: on, with the
  baseline. The precision case is strong and the baseline removes the
  first-run flood. Dan's call.
- **Expose the link graph as a query capability** (`vault_backlinks` or
  similar) or keep it internal to lint? Building lint answers the
  "do agents demonstrably need it?" question that entry is gated on, so decide
  it then, not now.
- **Baseline only, or baseline plus inline suppression?** Recommendation:
  baseline only, until a real case appears that it cannot express.
- **Assertions file format** — YAML file versus a structured note with the
  rules in frontmatter. The note is more Obsidian-native and editable in the
  app; YAML is simpler to validate and harder to break by accident.
- **Budget knob now or later** — see §6.

## 14. Vault-side follow-ups (not this repo)

Two lines in Dan's vault are already wrong regardless of whether this gets
built, and should be fixed when it is decided either way:

- The homelab housekeeping instruction "After any session that edits
  `Tech/Homelab/` notes, run `./notes-drift.py`" cannot be followed by an
  agent under MCP-only routing, and names the invocation that exits 126.
- The roadmap item "Schedule `notes-drift.py`" still proposes Mac launchd or
  the Syncthing mirror as the venue, both of which predate CT 106 holding the
  authoritative replica and the git history.
