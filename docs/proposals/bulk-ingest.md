# Proposal: bulk ingest (getting large files into the vault)

**Status: proposed, not built. Written 2026-09-03.** Nothing here describes
shipped behavior. The recommendation is **build nothing yet** — §3 is a
workflow that uses only tools that already exist. §4 onward exists so that
if the frequency ever justifies a tool, the shape is decided before anyone
touches `ToolSurface.All`, because a tool name is a locked contract from the
moment it ships.

Triggered by a real case: a few 400–600 KB CSVs for an ongoing project,
which an agent proposed to land via chunked `vault_append`.

## 1. The problem, measured

Knapper has no upload-from-path. Every write tool takes its content as a
JSON string argument — `vault_create(path, text)`,
`vault_append(path, expectSha256, text)` — and every write path ends in
`Encoding.UTF8.GetBytes(text)` (`VaultMutationService.cs:128,146,158`). So
content that is not already inside the vault can only enter it by passing
through a model's output tokens.

For a 19 KB CSV that was measured at ~19K tokens of output — roughly one
token per character, which is what dense delimited numeric data costs. Prose
-like CSV would run lower, call it 0.3–0.5 tok/char. The remaining 1.28 MB
therefore sits somewhere in the 400K–1.28M token range **as output alone**.

The agent that proposed chunking estimated ">1M tokens of context churn".
The direction is right and the magnitude is understated, because the cost is
not linear. Each append's arguments stay in the conversation, and every
subsequent request re-sends the whole prefix. For `B` bytes in `N` chunks,
cumulative input from the payload alone is:

```
B · (N − 1) / 2
```

At B ≈ 1.28M tokens and the ~64K-token output cap forcing N ≈ 20, that is
**~12M tokens of re-sent CSV**, not 1M. Prompt caching discounts the price
of the re-sent prefix but not its existence.

Two consequences worth stating plainly, because they change the conclusion
from "expensive" to "wrong shape":

- **More chunks make it worse.** `N` is in the numerator. The only value of
  `N` that avoids the quadratic is 1, which the output cap forbids. There is
  no chunk size that fixes this.
- **It may not fit at all.** 1.28 MB of CSV at the observed rate is ~1.28M
  tokens of payload against a 1M context window. The plan would likely have
  died of context exhaustion partway through, having already written half a
  file, with the remaining half needing a fresh session that no longer holds
  the source.

### The quieter cost: fidelity

`MutationResult.Verified` means the bytes reached disk exactly as sent —
reopened and byte-compared, which is a genuinely strong guarantee. It covers
**sent → disk**. It does not cover **source → sent**.

Routing a CSV through chunked appends is asking a model to transcribe
several thousand rows verbatim across ~20 responses. That is the workload
where rows get silently dropped, reordered, or subtly altered, and Knapper's
receipt is structurally blind to it: nothing local is wrong. The vault ends
up holding a file that is atomically written, hash-preconditioned, verified,
audited, and *not the file you had*.

This is the argument that survives even if the token budget were free.

## 2. Where the bytes are determines the answer

The problem decomposes by origin, and the three cases have different
answers. Conflating them is what makes "Knapper needs an upload tool" sound
more true than it is.

| Origin | Example | Right answer |
|---|---|---|
| **(a) On Dan's Mac** | The CSVs in this case | A transport already exists — §3 |
| **(b) Already on CT 106** | A script's output, a cron artifact | Server-side; §4 if ever wanted |
| **(c) Only in the agent's context** | A file the agent composed | Chunking, legitimately |
| **(d) On the public internet** | A product photo for a research note | Agent fetches to disk — §3b |

Case (c) is the only one where content genuinely must pass through model
output, and it is self-limiting: anything an agent composed is already
bounded by what it can hold. Cases (a), (b) and (d) should never touch model
output at all.

Case (d) was missing from the first draft and is the one that most changes
the conclusion — see §3b. It is not case (c): an agent that *fetches* an
asset never holds it as tokens.

For **images** the split has a fourth branch that dominates the others — a
graphic Claude authors should be emitted as SVG or mermaid, which is text and
needs no ingest at all. See §7a-bis before reading §4 as the answer to
"Claude adds an image to a note", because for most of that case it is not.

## 3. Recommended: Sync inbox + `vault_move`. Zero code.

The architecture already splits the world: **humans edit via Obsidian apps
and Obsidian Sync; agents go through Knapper.** A human dropping a file into
the vault is not a workaround — it is the sanctioned channel, and it moves
bytes without a model observing any of them.

1. Dan copies the CSV into `~/Documents/Helios/_inbox/` in Finder.
2. Obsidian Sync carries it to CT 106.
3. The agent calls `vault_move` to place it at its final path.

Step 3 gets every invariant the mutation layer offers, unchanged: path locks
in sorted order, conflict and sync gates asserted twice, containment proved
on both sides of the commit, destination published before the source is
captured, `LinkPublishCapture`, reopen-and-verify, and an audit entry. The
agent never sees a byte of content, so the fidelity gap in §1 does not
exist — nothing was transcribed.

It can also be skipped entirely: if the file is dropped straight at its
final path, there is nothing for an agent to do. Step 3 is for when the
agent should decide the placement, or rename by convention.

### What it does not give

- **No audit record of the arrival.** Only the move is audited. The file's
  appearance in `_inbox/` is a human write outside Knapper, and is invisible
  to the trail by design.
- **No `expect_sha256` provenance.** Same reason.
- **The inbox is live vault content while it sits there** — visible to
  `vault_search`, `vault_files`, `vault_lint`, and committed by
  `GitCommitJob` on the next tick. It is not a quarantine.

### Preconditions to check once

- **Obsidian Sync file types.** Sync does not necessarily carry
  non-markdown files; if "all other file types" is off, the CSV stays on the
  Mac and never reaches CT 106. Knapper then answers a clean, exhaustive
  "not found" — this is the known download-half gap in the completeness
  envelope, and nothing flags it.
- **Case sensitivity.** The Mac replica is case-insensitive by default; the
  CT's vault filesystem is case-sensitive by hard requirement. Two CSVs
  differing only by case are one file on one side and two on the other.

## 3b. Agent-fetched assets — the case that most wants §4

Worked example, and the one that prompted this section: *research a product,
pull its photo, write a note.*

The fetch half is already free, and this is the fact the whole section turns
on:

```sh
curl -fsSL -o /tmp/widget.jpg https://example.com/widget.jpg
```

Bytes land on disk **byte-exact, at zero token cost, never entering the
model's context.** Neither of §1's objections applies here. There is no
quadratic re-send, because nothing is re-sent. There is no transcription
gap, because `curl` copies rather than retypes — and the agent can still
`Read` the file to confirm it is the right product photo before committing
to it, which costs vision tokens once and requires no re-emission.

The note half is also already solved: `vault_create` for the note,
`![[widget.jpg]]` for the reference, both text through existing tools.

**Everything except one hop works today.** The gap is purely transport:
getting a file that is on the Mac's disk into the vault. That is a much
narrower problem than "Knapper needs uploads", and it has three answers.

| | How | Cost |
|---|---|---|
| **(i)** Dan drags it into the vault | Zero code, works now | A human step in the middle of an agent workflow |
| **(ii)** Agent writes to a sanctioned `_inbox/` in the Sync replica, then `vault_move` | Zero code | Requires amending the global "never write the replica" rule — Dan's call, §7d |
| **(iii)** Agent `scp`s to CT 106 staging, then `vault_ingest` | Needs §4 | Fully automated, no rule change |

(iii) is viable because CT 106 takes a direct `scp` today — the runbook
already uses `scp … root@<ct-address>:/opt/knapper/` for artifact upload
(§10) — and SSH authentication goes through the 1Password SSH agent, so no
key material is handled. **Verify the agent actually has that path before
relying on it**; the runbook's usage is operator-initiated, which is not the
same as an agent having it.

### This invalidates §4's main objection, for this case only

§4 argues against building `vault_ingest` because it *relocates* a manual
step rather than removing one — true for §2's case (a), where Dan already
has the file and Finder is no harder than `scp`. **It is false for case
(d).** Here the agent holds the bytes on the Mac and can perform the
transfer itself, so `vault_ingest` removes the human from the loop entirely
rather than moving where they stand. If agent-driven research notes with
assets become a real workflow, that is the strongest argument in this
document for building §4.

## 4. If it is ever wanted: `vault_ingest`

The shape, decided now so it is not designed under pressure later.

```
vault_ingest(stagingName: string, path: string, expectSha256: string?)
```

The server reads `Ingest:StagingPath/<stagingName>` — a directory **outside**
the vault, populated by scp — and writes those bytes to `path` through the
existing mutation machinery. No content crosses the MCP boundary in either
direction.

### Invariants it must satisfy

Most of these are not new work; they are the existing rules applying to a new
entry point. The first three are the ones a naive implementation would miss.

- **The staging directory is outside the vault, forced at boot.** Same
  treatment as `LockDirectory`, `AuditLogPath`, `CommitStampPath` and
  `MetricsPath`: a `GetRequiredService` singleton factory in `Program.cs`
  that refuses startup, plus a `knapper doctor` check. A staging directory
  inside the vault would make ingest a vault-to-vault copy with no lock on
  the source.
- **The source is confined with the resolver's own rule.**
  `VaultPathResolver.RejectSymlinkComponents` over the staging chain, plus a
  post-open `RealPath` containment check. Without it, a symlink placed in
  staging turns this tool into "read any file on CT 106 into the vault" —
  and the destination then syncs to every one of Dan's devices. This is the
  `.trash/` chain problem exactly, and it gets the `.trash/` chain's answer.
- **Non-regular files are refused.** `Posix.LStat` classify-first, as
  `ReadExisting` and `CapturedIsOurs` already do. A FIFO in staging would
  hang the read while the call holds its path locks, wedging every writer.
- `stagingName` is a **single path segment**, validated, never joined raw.
- The write goes through `Mutate`/`Create` **unchanged**: conflict and sync
  gates twice, `RequireSyncable` against post-transform bytes, containment
  proved both sides, `AtomicFile`, `VerifyOnDisk`, audit.
- **Create is no-clobber; replacing an existing file demands
  `expect_sha256`.** No unconditional write, not even here, not even
  "because the source is trusted".
- **The staged file is left in place, never consumed.** Deleting a pathname
  this tool did not create is the banned check-then-`unlink` shape. Staging
  cleanup is Dan's, outside Knapper.
- Ship-side: `--minor` bump, the three lists (`ToolSurface.All`, the
  `[McpServerTool(Name=…)]` attribute, `ToolNames.All`) in lockstep, a
  concrete return type, `UseStructuredContent = true`, `ToolSupport.Run`.

### The argument against building it now

**For §2's case (a) it does not remove a manual step; it relocates one.**
The file starts on Dan's Mac, so something must still carry it to CT 106 —
scp instead of Finder. (This does **not** hold for case (d); see §3b, where
the agent can do the transfer itself and `vault_ingest` removes the human
entirely. If that workflow materializes, weigh §3b, not this paragraph.) Against §3 it buys an audit record of the arrival and an
agent-initiated trigger, and costs a new tool on a locked surface plus the
confinement machinery above. At the observed frequency that trade is not
worth making. It becomes worth it when ingest is routine enough that the
audit gap is a real question, or when case (b) — files produced *on* CT 106
— starts happening.

## 5. Rejected: an HTTP ingest endpoint

`POST /ingest` behind Cloudflare Access would collapse the transport into
one hop: Dan curls from wherever he is, no scp, no staging directory.

Rejected for now. It puts a **write** endpoint on the public surface, which
is a categorically larger security question than a tool on an already-
authenticated MCP session: it interacts with the Access audience handler,
with `HostGuard.IsLocalRequest` (production is `cloudflared → 127.0.0.1`,
so every tunneled request is a loopback peer), and it would need its own
`knapper verify` ingress probes, which must assert a named refusal status
rather than "not 200". That is a lot of new surface to maintain for a file
that arrives every few weeks. Revisit only if ingest becomes routine **and**
scp is the friction that matters.

## 6. Rejected: server-side fetch

`vault_fetch(url, path)` — the server retrieves and writes it. This is the
tempting answer to §3b, because it collapses fetch and ingest into one call
and needs no `scp`. **Reject it**, and the reason is stronger than the first
draft stated.

It is not merely "CT 106 gains outbound egress". CT 106 sits on the homelab
LAN alongside Proxmox, the Synology, and Home Assistant, and it is a trusted
host behind Cloudflare Access. A tool that makes it issue an
agent-supplied-URL request is a **server-side request forgery primitive
aimed at the internal network** — `vault_fetch("http://192.168.x.x/…")` —
driven by an agent whose whole job is reading untrusted content. Vault notes
are the user's data, web pages doubly so; an agent researching a product is
reading attacker-influenceable text at exactly the moment it is deciding
what to fetch. The blast radius is not "a bad file in the vault".

**The distinction that matters, and it is not about the bytes:** an agent
fetching to its own machine (§3b) and CT 106 fetching on the agent's behalf
produce an identical file by an identical protocol. What differs is *which
host makes the request*. The agent's machine already browses the web and is
already exposed to whatever it reads; CT 106 does not and is not. Keeping
the fetch on the client side preserves that boundary at the cost of one
`scp`, which is the trade to make. `vault_ingest` reading a local staging
directory is deliberately a **filesystem** operation, never a network one —
if it ever grows a URL parameter, this section is why it must not.

## 7. Decisions any of this forces

### 7a. Binary — yes, and the invariant it seems to break is already broken

**Revised 2026-09-03**, prompted by the case "Claude adds an image to a
note". The first draft of this section recommended refusing non-UTF-8 at
ingest to preserve the property *everything in the vault is readable through
the tool surface*. **That property does not exist.** Measured against Helios
the same day:

```
Home/Mayapple/Projects/Kids Bathroom/Bathroom.jpg              515,432 bytes
Home/Mayapple/Projects/…/81281C74-…_1_105_c.jpeg               304,793 bytes
```

Both arrived through the human Sync channel, both predate the git era, and
Obsidian renders them in the notes that reference them. Refusing binary at
ingest would not have protected an invariant; it would have made Knapper's
own channel *stricter than the human one* for content the vault already
holds — an asymmetry with a cost (an agent can never add an attachment) and
no corresponding benefit.

**Revised recommendation: `vault_ingest` moves bytes and does not inspect
encoding.** It reads from staging and writes through `AtomicFile`; there is
no `Encoding.UTF8.GetBytes` anywhere on that path, which is the entire point
of it existing.

The mutation loop stays closed for binary, which is the fact that makes this
safe to allow. `vault_read` refuses non-UTF-8 (`NotUtf8`), but `vault_stat`
returns size, mtime, encoding status and **sha256, explicitly valid as a
mutation precondition**. So an agent can replace an image — `vault_stat` for
the precondition, `vault_ingest` for the bytes — even though it can never
read one back. Allowing binary does not create an unmutatable object class.
What it does create is an object class an agent writes without being able to
verify by reading; `VerifyOnDisk` still proves the bytes landed, so the
verification-by-content invariant holds. That is a real asymmetry, and it is
the honest cost of the decision.

### 7a-bis. "Claude adds an image to a note" decomposes further

The image case splits four ways, and only one of them is a tool gap. Getting
this wrong means building §4 for a case §4 does not serve.

1. **Claude authors the graphic** (a diagram, a chart, an architecture
   sketch). **Make it text.** SVG goes through `vault_create` today with no
   new machinery; a mermaid fence is rendered natively by Obsidian. This is
   strictly better than a raster attachment — diffable, git-friendly,
   searchable, editable in place, theme-aware — and it is the dominant case.
   Reaching for an image tool here is the mistake, not the solution.
2. **Dan has the image.** §3. This is what the two JPEGs above did.
3. **Claude produced raster bytes by running code locally** — matplotlib to
   a PNG on the Mac, a rendered screenshot. The file exists on disk, so it is
   §2's case (a), but the agent cannot finish the job: writing to
   `~/Documents/Helios` is barred by the global rule, for the good reason
   that it forks the vault with no precondition, no audit and no
   serialization. **This is the real gap, and it is the case `vault_ingest`
   closes.**
4. **An image that exists only as pixels the model perceives** — something
   it was shown rather than something it made. This cannot be emitted at any
   token budget: a model does not hold a serializable copy of an image it
   perceives, so there is nothing to re-emit. No tool fixes this. It is worth
   stating because it is the case people assume a tool fixes.
5. **Claude fetched it from the web** — a product photo for a research note.
   Added 2026-09-03; this is §2's case (d) and it is the one with a real,
   automatable answer. See **§3b**, and note that it is emphatically not
   branch 4: a fetched image never enters the model's context, so nothing is
   lost and nothing is re-emitted.

**Referencing** an image that is already in the vault needs nothing new —
`vault_edit` writes `![[Bathroom.jpg]]` into a note today. The gap is only
ever *introducing new bytes*.

Binary sharpens two items elsewhere in this document. `Sync__MaxFileBytes`
(5,000,000) is a live constraint for images rather than a theoretical one — a
phone photo routinely exceeds it, and `RequireSyncable` will refuse the write
loudly, which is correct but will be met more often than with CSV. And §7c's
git-weight point is worse for rasters, which do not delta-compress at all.

### 7b. The secret scanner meets data files

`SecretScanner`'s `api-key-like` pattern
(`src/Knapper.Core/Git/SecretScanner.cs:39`) matches
`(api_key|secret|token|password)` followed by `:` or `=` and 20+ characters.
A CSV carrying a URL with a query parameter, or an ID column formatted that
way, will trip it. `GitCommitJob` then refuses the **whole** commit, and
refuses again every tick for as long as the file is in the vault.

This is **not** silent, and an earlier reading in this discussion that said
otherwise was wrong: the commit stamp is touched only on successful runs, so
`ops/monitor/knapper-monitor.sh:203` alerts on stamp age past
`MAX_STAMP_AGE` (3900s) — and its message already names the secret scan as a
suspect. Detection is within ~65 minutes, via the host monitor rather than
`/health`.

Two sub-decisions, and the second is worth answering regardless of whether
anything in this document is ever built:

1. Whether a designated data folder gets a scanner exemption. **Recommend
   not** — precision over recall is the scanner's stated doctrine, false
   positives are rare, and an exemption path is a hole that outlives its
   reason. Handle a trip by fixing the data.
2. Whether `/health` should carry git-snapshot freshness at all. Today the
   only consumer of that signal is the external monitor. That is a defensible
   split — `/health` is about the vault, the monitor is about the deployment —
   but it is worth being deliberate about rather than incidental.

### 7c. Git weight

Every revision of a 600 KB CSV is a whole new blob; git stores no delta at
add time. The repo is local-only (no remote until the credential sweep
closes), so there is no push cost, but an ongoing feed of large data files
grows `.git` monotonically. Not a blocker at this frequency. Stated here so
it is not a surprise later, and so that "the CSVs are versioned in git" is
understood as a real cost rather than a free benefit.

### 7d. Should agents be allowed to write ONE folder in the Sync replica?

Raised by §3b option (ii), and it is a question about the global rule in
`~/.claude/CLAUDE.md`, not about this codebase. **Dan's call; recorded here
because §3b is where the cost of the rule shows up.**

The rule bars agents from writing `~/Documents/Helios` at all, on four
grounds: no sha256 precondition, no audit record, no attribution, no
serialization against the other replicas. For an agent **creating a new file
at a fresh path in a dedicated folder**, two of the four are at their
weakest — there is no existing content to clobber, so no precondition is
being skipped, and a unique filename has nothing to serialize against. The
file is inert until an agent moves it into place through Knapper, which is
audited normally.

Two survive intact: **the arrival is unaudited and unattributed**, and two
agents choosing the same filename still collide.

The real argument against is none of those. **The rule's value is that it is
absolute.** A carve-out means every future agent must decide whether its
write falls inside the exception, and that judgement gets made in exactly
the sessions where judgement is least reliable — a long one, late, under a
deadline, with a plausible reason to hand. A bright line that costs one
manual drag occasionally may simply be worth more than the drag.

If Dan takes the exception, scope it so the judgement is not required:
**one named folder, create-only, never edit, never overwrite, unique
filenames, and the note itself always written through Knapper.** Anything
looser and the line stops being bright.

## 8. Recommendation

**Use §3. Build nothing.** Chunked appends should not be used for case (a)
or (b) content at any size that needs more than one call — not primarily for
the token cost, but because the verification receipt does not cover
transcription, and a silently-altered CSV that reports `Verified: true` is
exactly the class of failure this codebase is otherwise built to make
impossible.

### What re-opens this

- Ingest frequency crosses roughly weekly, making the missing audit record
  of arrivals a real question rather than a theoretical one.
- Case (b) starts happening — files produced *on* CT 106 that should enter
  the vault. §3 does not cover that at all.
- **Case (d) becomes a workflow** — agent-driven research notes that pull
  assets (§3b). This is now the strongest argument in this document for
  building §4, because it is the only case where `vault_ingest` removes the
  human step rather than relocating it. It arrived as a question the day
  this was written, which is a reasonable sign it is coming.
- An agent hits case (c) at a size that does not fit its context. That is a
  different problem than this document solves, and chunking is still the
  answer to it.

Owner: Dan.
