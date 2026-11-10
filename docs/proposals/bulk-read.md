# Proposal: reading a folder's notes without naming each one

**Status: proposed, not built. Written 2026-11-10.** Nothing here describes
shipped behavior. The recommendation is **build nothing yet**: §4 is a
workflow that uses only tools that already exist. §5 onward records the shape
so that it is decided before anyone touches `ToolSurface.All`, if a re-open
trigger in §8 ever fires.

Triggered by a recurring question: should a vault-wide project (assembling a
reference document from notes spread across the vault, sweeping the vault for
every mention of a retired system) skip Knapper and read a local read-only
replica, for speed? This document measured that question and found that a
replica wouldn't speed things up much. It also found one real cost in the
Knapper path: an agent has to write out the name of every note it wants.

## 1. The question, measured

Evidence comes from client-side Claude Code transcripts (15 sessions with
Knapper calls, 336 calls), live read-only calls against the production
server, and a local replica of the same vault. Only aggregate numbers are
kept here.

The vault is small: about 400 files, about 300 of them notes, about 2 MB of
note text. 2 MB is roughly 450K tokens, so no path, Knapper or local, puts the
whole vault in a model's context. A vault-wide project is always search, then
targeted reads.

| Measurement | Value |
|---|---|
| Knapper call latency, p50 / p90 | 1.2 s / 2.7 s |
| Knapper's share of wall time, in turns that called it | **4.5%** |
| Model time between one tool result and the next call, p50 / p90 | 5.3 s / 56 s |
| Largest real vault-wide sweep (search, classify, apply ~20 edits) | ~225 s total, ~29 s in Knapper calls |
| Local ripgrep over the whole replica | ~5 ms |
| Local read of every note | ~10 ms |
| Replica content vs. Knapper, all ~400 files by SHA-256 | identical |

**The latency difference is real but does not move the total.** A local read
saves about one to two seconds per round trip, and a step costs about five
seconds of model time at the median. Writes still go through Knapper, with a
fresh read for the precondition, so a replica only removes read round trips.

**The replica's correctness risk isn't stale content; it's scope.** On the
day measured, the replica was byte-identical. The same search run locally
first returned about 20% more files than Knapper, all of them from an archived
prefix. Knapper skips that prefix by default and says so in
`excludedPrefixes`. The local exclusion glob silently did not apply, because
the search root was given as an absolute path. Fixed, the two agreed exactly.
Nothing on the local side reports what a search covered.

## 2. The cost that remains: naming every note

Reading one folder of about 100 notes (about 200 KB) through Knapper took
three `vault_batch_read` calls, because `Vault:MaxBatchItems` caps a batch at
50. Sent together, the server answered all three in about 2 s. **Generating
the three calls took about 14 s**, all of it the model writing out about 100
paths it had just been shown by `vault_files`. Locally, the same read is one
short glob.

That is the one place where the Knapper path costs more than a replica, and
it is model output, not server time. It grows with the number of notes, not
their size.

## 3. How often it happens

Rarely. All 32 historical `vault_batch_read` calls held 2 to 7 items, most of
them line ranges of specific notes, and 22 of 32 stayed inside one folder.
None came near the cap, and none read a folder whole. The established
pattern is search, then read a few notes, and it pays the path-writing cost
only a few paths at a time.

## 4. The workflow that needs no new tool

1. **Map the scope with one call:** `vault_search` in `counts` mode over the
   whole vault (or `vault_files` with filters). That returns which notes
   matter and how much each one matters.
2. **Read in parallel batches of up to 50**, sent in one message. In Claude
   Code, a result over the client's MCP output cap is saved to a file, which
   the agent can process with `jq` or a script without loading it into
   context. That is the replica's main advantage, available through Knapper.
3. **When a whole folder really is needed**, build the path list from the
   `vault_files` result with a script instead of retyping it. That removes
   §2's cost wherever the agent has a shell.
4. **Write back with one `vault_batch`**, not one `vault_edit` per note.

Step 3 is the gap: claude.ai and other clients without a shell cannot do
it, and pay §2 in full.

## 5. If it is built: options

### 5a. `includeContent` on `vault_files` (preferred)

`vault_files` already has everything a folder read needs except the bytes:
prefix, glob, extension, size and mtime filters; a path-ordered cursor bound
to those filters; the completeness envelope; archived-prefix exclusion
declared in `excludedPrefixes`; and `includeSha`, which already reads every
file. Add `includeContent: boolean` (default `false`), and each file entry
gains `content` and `encoding`, read in the same pass as `sha256`.

- **For:** no new tool name, so the locked surface and manifest don't
  grow. The filter and cursor semantics are the ones agents already use, so
  "list it" and "read it" become the same call with one more argument.
- **Against:** a listing tool that returns bodies is a bigger contract than
  its name suggests. A page now needs a byte bound as well as a count
  (§6). Directory and binary entries need a defined answer (no `content`,
  `encoding` says why) rather than an error that stops the page.

### 5b. A path-pattern alternative to `items` on `vault_batch_read`

Accept `pathPrefix`/`glob` as an alternative to `items`.

- **For:** it lives on the read tool, where an agent would look for it.
- **Against:** two mutually exclusive argument shapes in one tool, and
  `vault_batch_read` has no cursor today. Adding pagination to it means a new
  envelope on an existing response: a bigger contract change than 5a, for
  the same capability.

### 5c. A new tool (`vault_read_tree` or similar)

- **For:** the cleanest contract on paper.
- **Against:** a 19th locked name that duplicates `vault_files`'s filters and
  cursor. Two implementations of "which files are in this scope" can drift
  apart, as `vault_search` and `vault_files` once disagreed about dot-files
  (see CLAUDE.md, query-layer invariants).

## 6. Pros and cons of building it at all

**For:**

- **Removes the one measured cost of the Knapper path for folder-sized
  work.** The time to write the call no longer grows with the number of notes.
- **Helps exactly the clients that have no workaround:** claude.ai, mobile,
  and any client without a shell to build a path list.
- **Keeps vault-wide projects on the audited path.** It weakens the
  temptation to read a local replica, which reports neither what a scope
  excluded nor whether it was searched exhaustively.

**Against:**

- **It is rare.** §3 found no historical call it would have helped. A
  client-facing contract for a once-in-a-while project is a poor trade
  unless that changes.
- **Small win per project.** On a folder-sized read it saves roughly 15 to
  30 seconds of a project measured in minutes (§1).
- **It floods clients that keep large results in context.** Claude Code
  saves an oversized result to a file; other clients may not. A bulk read
  without a byte bound is the
  [response-budget](response-budget.md) failure class made easy to trigger.
  With a bound, a folder comes back as several pages, so some of the saving
  goes back into round trips. Paging only means passing a cursor back,
  though, which is far cheaper than writing out paths.
- **It undercuts the save-to-file workflow.** Under a response budget, a
  folder can no longer arrive as one oversized result saved to a file. In
  Claude Code that file is the most useful form of the data for scripting.
  The two proposals have to be designed together.
- **It makes bulk export easier.** Read-only tools are usually
  auto-approved by harnesses. A note carrying a prompt injection could
  already ask an agent to read a whole subtree; this cuts that from several
  calls to one plus pagination. The data reachable doesn't change, only the
  effort, but the trust model in the server instructions exists because
  vault content is attacker-controllable text.
- **Release cost.** A new argument and response members on an existing tool
  are a `--minor` release, a description change under the 2048-character
  client cap, a conformance-test case, and a manifest check through
  `knapper verify`.

## 7. Contract checklist for whoever builds this

- **Content and hash from ONE read.** `content` and `sha256` come from the
  same bytes, through `Posix.OpenRegularForRead`. A separate hashing pass lets
  a concurrent write pair one version's text with another's hash, and that
  hash is mutation-precondition currency.
- **Byte-bounded pages.** The page closes when the next file would cross the
  response budget. It never closes empty: the first file is always included,
  even if it alone is over the budget, or its entry carries a typed
  `[TooLarge]`-style refusal with its size. The cursor stays path-ordered
  (`QueryCursor.ComparePathUtf8`), so a byte bound changes where pages break,
  never which records exist.
- **The envelope never lies.** `truncated`, `totalMatches` and
  `excludedPrefixes` mean what they mean on `vault_files` today. A budget hit
  is `truncated: true` plus a cursor.
- **`changedDuringQuery` covers the reads,** not just the walk. A file that
  changed between listing and reading is reported by the generation span,
  as in `vault_batch_read`.
- **Non-regular files and symlinks** are classified exactly as the lister
  classifies them today. Nothing new is opened through a link, and a FIFO
  never hangs a page (`NonRegularFileReadTests`).
- **Optional members** (`content`, `encoding`) carry a C# default and
  `JsonIgnore(WhenWritingNull)`. With `includeContent: false` the response is
  byte-for-byte what it is today (`ToolResponseConformanceTests`).
- **Description:** the byte bound and "pass `nextCursor` back" go near the
  FRONT of the text. The composed string stays under
  `ToolSchemaContract.ClientTextBudget`.

## 8. What re-opens this

Any one of:

- vault-wide or folder-wide projects become routine (monthly or more often);
- transcripts show turns with chains of `vault_batch_read` calls whose items
  were copied from a `vault_files` listing, or batches near the 50-item cap;
- the response budget ships. Its pagination rules are the hard part of §7,
  and building this after them costs much less.

**Keep the analysis local.** Re-mining uses transcripts that contain vault
content; only aggregate counts belong in this repository.
