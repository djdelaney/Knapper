# Proposal: a response budget for read-shaped tools

**Status: not built. Written 2026-09-29.** For what Knapper does today, read
[architecture.md](../architecture.md) and [usage.md](../usage.md). This
proposal covers `vault_read`, `vault_batch_read` and `vault_search`. It
changes how large results are shaped, and two of its phases change the
client contract, so it needs deciding before anyone builds it.

## 1. The problem

Knapper hands back results that are too large for the client to use, logs
them as `ok`, and never finds out. Evidence comes from mining client-side
transcripts over 2026-08-16 → 2026-09-29: two Macs' Claude Code transcripts
plus a claude.ai data export, 2,087 Knapper calls in all. Only aggregate
counts were kept.

| | Mac #1 | Mac #2 | claude.ai | Total |
|---|---|---|---|---|
| Result refused by the client as too large | 15 | 29 | 4 | **48** |
| Follow-up shell reads of the saved result file | 26 | 38 | n/a | 64+ |

- **Claude Code** refused results from about 52K to 889K characters. It caps
  MCP output (default about 25K tokens, set by `MAX_MCP_OUTPUT_TOKENS`),
  saves the result to a file, and the agent then digs through that file with
  `jq`, `sed` or `grep`.
- **claude.ai** accepted results up to 133K characters, and 26 results over
  50K fit. The 4 it refused went to its sandbox.
- **What produced them:**
  - whole-file `vault_read` of large capture/log files and a few very long notes
  - `vault_batch_read` of 3–4 medium notes
  - unscoped `vault_search` with context lines
- **The server saw success every time.** Neither the journal nor
  `call-economics.sh` can see this failure class or the extra calls after it.
- **Ranged reads are already habitual where they matter.** 179 of 229
  read-then-edit pairs (78%) used a ranged `vault_read`.

### What it actually costs, which is less than it looks

In Claude Code, recovering from an oversized result costs local shell calls
of a few milliseconds each, not relay round trips of several seconds (see
[call-economics.md](../call-economics.md)). The real costs are these:

- the agent loses the structured result;
- it reasons over a file of escaped JSON;
- context fills with shell plumbing;
- the whole path is invisible to the server.

**Any design that turns an oversized result into an extra MCP round trip can
make Claude Code slower than today.** A design that refuses more often makes
claude.ai worse too, since it already handles results up to 133K characters.
That trade decides the `vault_read` question in §4.4.

## 2. What already exists

| Mechanism | Today |
|---|---|
| `Vault:MaxOutputBytes` | 1,000,000. The match-text byte budget per `vault_search` page. It counts context lines and each record's line (`VaultSearchService.MatchStream`). It already sets `truncated: true` plus `nextCursor`, and it never closes an empty page, so a page always makes progress. |
| `Vault:MaxReadBytes` | 4,000,000. Past it, `vault_read` fails `[TooLarge]`. Reads are **never** silently truncated (`VaultReadResult`: `WasTruncated => false`). |
| `Vault:MaxBatchItems` | 50 items per `vault_batch_read`, with no byte bound. |
| Ranged reads | `startLine`/`endLine`. The result carries `rangeStart`/`rangeEnd`/`totalLines` and still returns the whole file's `sha256`. |
| `vault_stat` | Already returns `size`, `totalLines` and `sha256` without the body, which is enough to plan a ranged read or get a precondition. |

So search already has the mechanism and only its default is wrong, by a
factor of 25. The new work is in batch read and whole-file read.

## 3. Measured 2026-09-29: what a result weighs on the wire

A dev server over a `tools/dev-vault.sh` vault was called with raw JSON-RPC
`tools/call vault_read`. Two things matter.

1. **Every result goes over the wire twice.** It is sent once as
   `content[0].text`, a JSON document serialized into a string, and once as
   `structuredContent`. The MCP spec recommends this for clients that don't
   read structured content, so dropping the text block isn't an option.
2. **The text block escapes as JavaScript does.** The SDK's default encoder
   turns every non-ASCII character and every HTML-sensitive character (`"`,
   `'`, `<`, `>`, `&`, `+`, and the backtick) into `\uXXXX`. This happens
   *inside the string*, so the model sees `café` and `don't`
   literally. An emoji becomes a 12-character surrogate pair.

| Note | Note length | Text block | Whole JSON-RPC message |
|---|---|---|---|
| This repo's `CLAUDE.md` (dense markdown) | 66,592 | 74,729 (**1.12×**) | 152,082 (2.28×) |
| `docs/proposals/upload-grants.md` | 37,809 | 40,717 (**1.08×**) | 82,796 (2.19×) |
| 40 characters of accented text with quotes | 40 | 372 (9.3×) | 1,028 |

Which quantity Claude Code caps is **not known**: the text block, the
structured content, or both. The smallest refusal was about 52K characters.
At the usual 3.5–4 characters per token for English, that is roughly 13–15K
tokens, well under a 25K-token cap. So the client either estimates tokens
conservatively or counts more than the text block. Phase 0 settles this
before any budget is picked.

## 4. Proposal: four phases, cheapest and least contractual first

### 4.0 Measure it on the server (patch)

Add the response's **text-block length in characters** to the per-call
journal line `ToolSupport.Run` already writes. Teach `call-economics.sh` to
print its distribution per tool and a count over a threshold.

- **Why first:** it turns the §1 failure class into something the journal
  shows for every surface. That includes the ones that leave no local
  transcript: cloud sessions, Cowork, mobile.
- **Bonus:** it lets the budget be picked from the real distribution instead
  of from transcript mining.
- **Contract impact:** none. It measures the payload, never its contents, so
  no note text reaches the log.

### 4.1 Stop inflating the text block (patch)

Serialize tool results with an encoder that leaves non-ASCII and
HTML-sensitive characters alone (`JavaScriptEncoder.UnsafeRelaxedJsonEscaping`,
or a narrower custom encoder). The "unsafe" in that name refers to embedding
in HTML, which never happens here.

- **Gain:** 8–12% on English markdown, much more on non-Latin text, and the
  model reads `café` rather than `café`.
- **Emoji:** they probably stay escaped, since System.Text.Json escapes
  supplementary-plane characters under the built-in encoders. Measure after
  the change rather than assuming.
- **Test:** a wire test must pin that `content[0].text` and
  `structuredContent` still parse to the same value.
- **Scope:** the change sits in `ToolSerialization.Options`, the ONE place
  results are serialized, so it covers every tool at once.

### 4.2 `vault_search`: lower the existing budget (patch)

Bring the `MaxOutputBytes` default down to the budget (§5). No shape
changes. `truncated` + `nextCursor` already exist, the progress guarantee
already exists, and the completeness envelope already treats a budget hit
as non-exhaustive.

- **Byte vs character:** the budget counts UTF-8 bytes of match text; §5's
  budget is text-block characters. Either convert with a stated margin for
  paths and JSON overhead, or have 4.0's measurement pick the byte value
  directly.
- **Oversized single record:** a match line far larger than the budget still
  goes out whole, because a page must never be empty. That is acceptable
  when it's rare, and 4.0 will show whether it is.

### 4.3 `vault_batch_read`: fill to the budget, defer the rest (minor)

Read items in request order until the running size would exceed the budget.
Each remaining item comes back through the **existing per-item error shape**
with a new code, `[Deferred] not read: response budget reached — request
this item again`.

- **Why not a new field:** agents already handle per-item failure here
  ("one unreadable file reports its own typed error and never hides the
  others"). A new top-level `deferred` list would be a second way to say the
  same thing.
- **Progress:** the first item is always read, even when it alone exceeds
  the budget.
- **Version:** a new error code is a `--minor` release. `VaultErrorCode`, the
  error table in `usage.md`, and the `vault_batch_read` description all need
  the new code, and the description must stay under the 2048-character
  client cap.

### 4.4 `vault_read` over budget: decide after 4.0

This is the one with a real hazard. Two options:

- **(a) Typed refusal.** Past the budget, a whole-file read fails with the
  file's `size`/`totalLines` and a suggested first range. This extends the
  existing `[TooLarge]` principle: never a silently partial whole read.
  - It costs one relay round trip per oversized read.
  - It turns reads that claude.ai handles today into failures.
- **(b) Partial content as a range.** Past the budget, return lines 1..N with
  `rangeStart`/`rangeEnd` set, plus an explicit flag, as if the caller had
  asked for a range.
  - It saves the round trip.
  - A caller that ignores the flag holds a *prefix it believes is whole*.
    **Copy-then-delete turns that into data loss:** read A, create B from
    it, delete A. Soft delete makes A recoverable, but only by a human who
    notices.

**Recommendation:** ship 4.0–4.3 first, then decide 4.4 on data.

- If 4.0 shows over-budget whole reads are rare after 4.1, do neither and
  add one clause to the `vault_read` description telling agents to check
  `vault_stat` size first.
- If they are common, prefer **(a)** with a budget set above most of what
  claude.ai handles. The hazard in (b) is the silent-corruption kind this
  repository exists to refuse, and the round trip is the price.
- A per-call opt-in argument doesn't help: the agents that trip this are the
  ones that don't know to pass it.

## 5. The budget

- **Unit:** text-block characters after 4.1 (the quantity 4.0 logs).
- **Default:** about 40,000. That is under the smallest observed refusal
  (~52K) with margin, and well under claude.ai's observed ceiling.
- **Configuration:** `Vault:MaxResponseChars`, validated at startup and
  documented in `usage.md`'s configuration table. One knob for all three
  tools. 4.2 derives the search byte value from it, or keeps its own knob if
  4.0 shows the two need to differ.
- **Keep clear of the client cap, never tune up against it.** This is the
  same rule as the 2048-character description cap in CLAUDE.md: the client's
  limit is undocumented and unversioned. A budget that sits just under
  today's measured edge fails silently the day it moves.

## 6. What not to do

- **No per-client budgets.** `ClientApp` can't separate Claude Code going
  through the claude.ai connector from claude.ai chat (both log as
  `Anthropic/ClaudeAI`), so a budget keyed on it would guess.
- **No silently partial whole-file reads**, per §4.4.
- **No dropping the text block** to halve the wire size. Clients that don't
  read `structuredContent` would get nothing.

## 7. Contract checklist for whoever builds this

- `ToolResponseConformanceTests`: any new response member is either
  required or genuinely optional (a C# default plus
  `JsonIgnore(WhenWritingNull)`).
- `UseStructuredContent = true` and concrete return types stay as they are.
  4.3 needs no new return type.
- Tool descriptions changed in 4.3/4.4 stay under
  `ToolSchemaContract.ClientTextBudget`, with the new behavior near the front.
- The completeness envelope never lies. A budget hit is `truncated: true`
  with a cursor, never an exhaustive-looking short page.
- A budget never closes an empty page or an empty batch.
- Versions: 4.0–4.2 are patch releases; 4.3 and any 4.4 option that adds a
  code or field are `--minor`.

## 8. Success measure

After shipping, re-mine the transcripts the same way and check three things:

- the too-large count drops to about 0;
- `vault_read` calls per edit do not rise;
- the §4.0 journal count of over-budget responses stays at 0 across every
  surface, including those with no local transcripts.

**Keep the analysis local.** Transcripts and exports contain vault content;
only aggregate counts belong in this repository.
