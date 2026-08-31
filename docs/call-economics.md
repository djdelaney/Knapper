# Call economics

Why vault work feels slow, what can actually be done about it, and how to tell
whether a change helped.

## The finding

**Knapper's own work is not the cost, and no amount of server-side tuning can
touch what is.** Measured 2026-08-24 against CT 106:

| | |
|---|---|
| Server-side work per tool call | **12.2ms mean**, 999ms worst case over 817 calls |
| Client-observed round trip | **~2.9–3.6s** |
| Share of user-visible latency that is relay, not Knapper | **>99%** |

The two figures were reconciled against each other rather than assumed. The
same `vault_files` call logged `9ms` server-side and `3,135ms` client-side; the
same `vault_batch_read` logged `0.8ms` against `2,949ms`. A locally-configured
MCP client (`knapper-SMOKE`, §8b) reaching this server through the *same*
Cloudflare Tunnel and Access app measured ~120ms, so the gap is not the tunnel
either — it is the claude.ai connector relay, upstream of this box.

Two details rule out the obvious explanations: `ConnectionId` stays identical
across calls minutes apart, so it is not per-call connection setup; and every
`tools/call` is preceded by a separate `server/discover` round trip from the
relay, which costs ~0.1ms here but doubles the traversals per tool call.

**Consequence: call COUNT is the only lever, and it belongs to the client.**
That is why the intervention is a paragraph in `Program.cs`'s
`ServerInstructions` (CALL ECONOMICS) and not code. Instructions reach every
surface — Cowork, Desktop, mobile, claude.ai — which matters because most of
those cannot be pointed at a directly-configured MCP server the way Claude Code
can.

## Measuring

```sh
pct exec 106 -- sh /opt/knapper/ops/call-economics.sh [DAYS] [--all-clients]
                                                             [--daily] [--audit PATH]
```

The journal is the **only** telemetry covering every client. Cowork, Desktop,
mobile and claude.ai leave nothing on the operator's disk, so client-side
transcript mining sees Claude Code and nothing else. `Mcp:LogToolCalls`
defaults to true and the production unit does not override it.

Read **WORK PER ROUND TRIP** first — it counts files, not call shapes, and the
two disagree badly (below). The RATIOS block beneath it is supporting detail,
and the counts are context only: two windows never carry the same amount of
work, so "fewer calls" measures how busy the week was. The script excludes
`knapper verify --url` traffic by default — it spends a fixed 4 calls per run
from a service-token identity, so leaving it in dilutes every ratio by however
often someone happened to run verify.

`--daily` adds a per-day series of files-per-mutation-call with its standard
deviation — the variance a two-window comparison structurally cannot supply
(see "What the follow-up actually showed" below, where every headline moved
the right way and none of them cleared significance).

The **client application** breakdown (`claude-ai`, `claude-code`, …) comes
from `clientInfo`, captured per call by the tools/call filter in
`Program.cs`. Five values are not names: `unrecorded` (logged before the
field existed), `unfiltered` (the filter did not run — a Knapper bug, not a
client one), `no-server`, `no-client-info` (a session exists, the client sent
no name), and `no-session` (no completed `initialize` reached this server
instance). It is the axis
Access identity cannot supply: that identity is per-USER, so every surface
collapses into one email, while the round-trip cost this report measures is a
property of the SURFACE — a locally-configured client measured ~120ms against
the relay at ~3s. Without it, a window whose surface MIX moved is
indistinguishable from one whose agents changed behaviour. Rows reading
`unrecorded` predate the field.

Client identities in the breakdown are **masked** (`8926bb31… (service
token)`, `own…@… (user)`). This report is written to be pasted into an issue or
a commit message, and reading it should not be what discloses an owner email
or an Access service-token client id; a prefix and a kind tag answer every
question the block exists for. `--show-identities` prints them verbatim, for
the one case that needs it — an unrecognised client.

## Observed — 2026-08-24, 7-day window

Produced by `ops/call-economics.sh 7` on CT 106 immediately after deploying
`knapper 0.5.5`. It still measures **pre-change** behaviour: server
instructions reach a client at `initialize`, so no session in this window had
seen the CALL ECONOMICS text. Running it right after the restart — rather than
before, from a hand-copied script — is what makes this baseline and its
follow-up the output of the same tool.

| Metric | Baseline |
|---|---|
| Calls (human client) | 809 of 837 (28 verify-probe calls excluded) |
| Server mean / max | 12.2ms / 999ms |
| Batched-read share | **9.9%** (25 `vault_batch_read` vs 228 `vault_read`) |
| Batched-mutation share | **13.3%** (23 `vault_batch` vs 150 single edit/append/create) |
| Reads per mutation | **1.46** |
| Concurrent (<1s apart) | 11.6% (94 calls) |
| Sequential hops (1–20s) | 49.1% (397 calls), mean **7.3s** apart |
| Relay wait in window | **48.4 min** |
| Server work in window | 9.9s — **0.34%** of the wait |

Tool mix: `vault_search` 295, `vault_read` 228, `vault_edit` 144, `vault_stat`
46, `vault_files` 33, `vault_batch_read` 25, `vault_batch` 23, `vault_create`
6, `vault_move` 5, `vault_delete` 2, `vault_mkdir` 2.

Outcomes were healthy: 792 `ok`, 11 `NotFound`, 2 `AnchorMismatch` (~1.4% of
144 edits, in the same range as the 0.3% the §8b transcript mining predicted),
2 `GuardViolation`, 1 `InvalidArgument`, 1 `MutationBlocked`.

**Cross-checked against an independent derivation.** Before this script
existed the same window was computed by hand from an ad-hoc `journalctl | jq |
awk` pipeline: 9.2% batched-read, 13.4% batched-mutation, 1.45 reads per
mutation, 48% sequential hops at 7.2s, ~47 min of relay wait. Two independent
derivations agreeing to within a percent is the evidence that neither carries
an arithmetic bug — worth repeating if the script is ever substantially
rewritten.

**What the baseline says the intervention should move.** ~90% of reads and
~87% of mutations were still one-per-call. Because every write needs a fresh
read first, each of those single edits costs two round trips; one
`vault_batch_read` plus one `vault_batch` does N edits in two.

⚠️ **This paragraph originally nominated batched-mutation share as the number
to watch, with reads-per-mutation corroborating it. Both nominations were
wrong**, and the follow-up below is what showed it. They are corrected here
rather than deleted, because the next person to design a metric for this will
reach for the same two.

- **Reads per mutation is not a batching signal, in either direction.** The
  floor argument ("cannot fall below ~1.00 while the fresh-read rule stands")
  is about FILES; the metric counts CALLS. Ten files edited fully unbatched is
  10 reads + 10 edits = 1.00, and fully batched is 1 `vault_batch_read` + 1
  `vault_batch` = 1.00. It is *invariant* to batching applied evenly, and
  batch-reading alone drives it BELOW 1.00 with the rule intact. What it
  actually measures is reading that is not driving a write. It also excludes
  `vault_stat`, which returns a sha256 and is a perfectly good precondition
  source — so an agent switching to `vault_stat` moves it with no behaviour
  change at all.
- **A share of calls is not a measure of work.** `vault_batch` of 2 items and
  of 20 count the same. The share can rise purely because the window carried
  more mutation work, which is what happened.

**The metric that replaces both: files per mutation call.** The journal
records one line per call and nothing about its contents, but the audit log
already has it — one line per PATH, tagged with the RequestId of the call
(`AuditLog.Entry`). Distinct `(RequestId, Path)` pairs are the files, distinct
RequestIds are the calls. It subsumes both levers (batch more often, batch
bigger), it is immune to the invariance above, and it cannot be moved by call
shape alone. `ops/call-economics.sh` computes it and prints it first.

⚠️ Count PAIRS, not lines. Every applied item writes two audit records —
`attempt` before the write, outcome after — so a line count reports every
batch at twice its size. That is exactly how mean batch size was first
mis-reported as 2.2 against a true 2.08.

⚠️ And divide by the JOURNAL's call count, not by the RequestIds the audit
saw. A mutation refused before the audited region — a gate, a path that never
resolves, a batch rejected at validate — writes no per-item record but still
spends a round trip. Dividing by audited calls omits exactly those, which
gives the metric a perverse property: **it improves as more mutations fail.**
Shipped that way once; on the 2026-08-31 window 4 of 220 calls (1.8%) bought
no files, reading 1.208 against a true 1.186 and inflating mean batch size to
2.25 against a true 2.08. The script now takes files from the audit, calls
from the journal, prints the unaudited remainder as its own line, and refuses
to ratio at all if the audit somehow saw MORE calls than the journal — which
would mean the two are describing different windows.

⚠️ **Do not compare a window that straddles the deploy** — but the reason is
narrower than it looks, and it does not apply to the surface carrying most of
the traffic. Server instructions are delivered at `initialize`, so sessions
already open keep the old text until they reconnect. Start the "after" window from the first reconnect after the
restart, not from the restart. With 0.5.5 deployed 2026-08-24, the first
`ops/call-economics.sh 7` whose window carries no pre-change traffic is on or
after **2026-08-31**; running it earlier averages the two states together and
understates whatever the change did.

## What the follow-up actually showed — 2026-08-31, 7-day window

The first window carrying no pre-change traffic, run per the warning above.

| Metric | 08-17→24 | 08-24→31 | |
|---|---|---|---|
| Calls (human client) | 809 | 859 | |
| batched-read share | 9.9% | 15.1% | |
| batched-mutation share | 13.3% | 17.9% | |
| batch calls / files (audit) | 26 / 53 | 39 / 81 | |
| single calls / files (audit) | 147 / 147 | 189 / 189 | |
| **mean batch size** | **2.04** | **2.00** | flat |
| batched share by FILE | 26.5% | 30.1% | +3.6pp |
| **files per mutation call** | **1.156** | **1.177** | **+1.8%** |

⚠️ The baseline column was derived by hand, before the denominator was
corrected and with windows matched by date rather than by instant. It is
NOT strictly comparable to the post column, which the script now produces on
the corrected definition. Treat this table as indicative and the 14-day
daily series below — one pipeline, one window definition — as the evidence.
| server work share | 0.34% | 0.28% | thesis reconfirmed |

**The call shares moved ~35% relative; the work per round trip moved ~2%.**
The shares rose because the window carried 35% more mutation work (200 → 270
files) and batch calls grew slightly faster than singles — composition, not
discipline.

**And nothing clears significance.** Two-proportion z-tests across the two
windows: batched-read share z=1.76 (p≈0.08), batched-mutation share z=1.24
(p≈0.22), batched share by file z=0.83 (p≈0.41). These are
*anti-conservative* — calls cluster within sessions and tasks, so the
effective n is well below the call count and the true p-values are larger.

⚠️ The honest verdict is **no detectable effect**, which is not the same as
no effect: this design cannot resolve anything smaller than roughly ±10pp on
a share. Underpowered ≠ null. But nothing here licenses the conclusion that
the instructions worked. `--daily` exists so the next comparison has a
variance estimate to be judged against.

### Two counter-signals that are not counter-signals

Sequential hops rose 49.1% → 52.6% and concurrent dispatch fell 11.6% →
7.8%. Both are **mechanical shadows of batching**, in the direction that
reads as a regression. Batching destroys concurrent gaps by construction:
five `vault_read` calls dispatched in one message produce four sub-second
gaps, and the `vault_batch_read` that replaces them produces none. Sequential
share rises because it shares that denominator. Idle share held flat at ~39%,
which is what you would expect if session structure did not change.

The mean gap widening 7.3s → 7.7s is confounded the same way: that interval
is mostly model inference, and batched reads return more content to generate
against, so a wider gap is a plausible *consequence* of batching. It is not
evidence about client discipline and this telemetry cannot make it so.

### The batching lever is close to exhausted

**Mean batch size is 2.04 before and 2.08 after** — pinned across the
intervention. The instructions' pitch ("N separate edits cost 2N round trips,
one `vault_batch_read` plus one `vault_batch` does it in 2") is compelling at
N=10 and nearly pointless at N=2, where it saves two round trips. Batch size
did not respond to the instruction in either window, which suggests it is set
by the shape of the work rather than by anything an agent can be told. The
37:39 `vault_batch_read`:`vault_batch` ratio says agents ARE following the
pattern literally; there is just not more to put in each batch.

What batching did buy, in round trips: 81 files in 39 batches cost 78 round
trips against 162 unbatched — **84 saved, ~15.6% of the mutation side, ≈11
minutes of relay wait in the window**. The realistic ceiling for the whole
remaining lever, every one of the 270 files batched at the natural size of
~2, is ~186 round trips out of 859.

### Where the remaining round trips are

`vault_search` is 324 of 859 calls (37.7%, flat against the baseline's
36.5%). It has no batch form, so no batching discipline reaches it, and
"before spending a second call, widen the first" is the only instruction that
applies. Whether that sentence is landing is now measurable: the
CONSECUTIVE RUNS block counts calls that a single wider call could have
replaced, straight from the journal's tool names and timestamps. Its number
is a **ceiling, not a saving** — a search that genuinely needed the previous
answer is counted in it too.

⛔ Do not iterate the instruction text on the evidence above. It would react
to a 2.4% move indistinguishable from a busier week, and it would spend the
clean before/after boundary the baseline paid for. Measure the run lengths
first.

## What settled it — 2026-08-31, 14-day daily series

One pipeline, one window definition, spanning both sides of the deploy.
Strictly better evidence than two separately-windowed runs, and it is what
`--daily` exists for.

| | pre (08-17→23) | post (08-25→31) |
|---|---|---|
| files per mutation call (aggregate) | 1.130 | 1.177 |
| daily mean | 1.127 | 1.304 |
| daily sd | 0.135 | **0.262** |

Welch's t on the daily ratios: **t=1.59, df=9, p≈0.15.** Not significant.
The aggregate step is 0.047 against a 2σ threshold of 0.215 for two 7-day
windows — **4.6× inside the noise floor**.

**What actually moved is dispersion, not level.** The post-period sd is
nearly double the pre-period's. And the relationship between volume and ratio
is *negative*: 08-27 ran 66 mutation calls at exactly 1.000 — 30% of the
window's mutation work, entirely one file per call — while the 7- and
10-call days sit at 1.57 and 1.70. A weekly aggregate is dominated by
whichever large single-edit day it happens to contain, which is the whole
reason two adjacent weeks could never have answered this.

⛔ **The verdict on the CALL ECONOMICS instructions is: no detectable effect.**
Not "a pass with caveats". The instructions may still be doing something
below the resolution of this design, but nothing in three separate analyses
licenses the claim that they worked.

### Where the round trips actually are

Over the same 14 days, the CONSECUTIVE RUNS block counts **349 excess calls —
20.7% of all traffic, ≈44 of the 106 minutes of relay wait.**

| tool | calls | excess | longest run |
|---|---|---|---|
| `vault_search` | 628 | **198** | 9 |
| `vault_read` | 439 | 73 | 5 |
| `vault_edit` | 318 | 38 | 6 |
| `vault_files` | 60 | 15 | 4 |
| `vault_stat` | 89 | 13 | 3 |
| everything else | — | 12 | 5 |

`vault_search` is **57% of the entire addressable ceiling**, and more than
twice `vault_edit`'s — while batching, which does not reach search at all,
has bought ~84 round trips total. Runs of 9 consecutive searches are the
shape "before spending a second call, widen the first" was written against.

That is where an instruction change should aim, and the run counts are the
before-measurement for it. Remember what the number is: a **ceiling**. A
search that genuinely needed the previous answer is inside it.

## How the relay actually talks to this server — 2026-08-31

Measured after 0.6.3 split the client-attribution states. Every JSON-RPC
method over 14 days, by the client the SDK's own logger names:

| client | initialize | server/discover | tools/list | tools/call |
|---|---|---|---|---|
| `Anthropic/ClaudeAI` | **1** | 613 | 68 | 539 |
| *(no client identity)* | — | — | 75 | **1143** |
| `claude-code` | **64** | 23 | 21 | 18 |
| `knapper` / `knapper-verify` (probe) | 11 | 11 | 11 | 44 |

**The claude.ai relay does not use `initialize`.** It sent one, on
2026-08-17 — a week *before* the release that shipped the CALL ECONOMICS
paragraph — and never again, across ~1,680 tool calls and 11 service
restarts. Claude Code, which connects directly, initialises per session (64
times).

That looked briefly like the intervention had never been delivered to the
surface generating most of the traffic, which would have been an alternative
explanation for every null result above. It is not: the 2026-07-28 revision's
`server/discover` carries `instructions` in its result, the relay issues one
roughly per tool call, and Knapper answers it with the full text.
`ToolManifestTests.Server_discover_carries_the_instructions_not_just_initialize`
pins that, because it is now the ONLY channel the instructions reach the
majority surface through — and if it ever stopped carrying them, tools would
still list, tools would still call, health would stay green, and every
before/after window would be measuring a change that never arrived.

**Consequences for the deploy caveat above.** For the relay, instruction
propagation is prompt: it re-discovers continuously, so it picks up new text
within about one tool call of a restart. The "wait for sessions to reconnect"
rule applies to `initialize`-based clients — Claude Code — not to claude.ai.
The 08-24 → 08-31 window was therefore a cleaner "after" than it was given
credit for, which strengthens rather than weakens the null result.

**Consequences for client attribution.** Under 2026-07-28 `clientInfo` is
carried per-REQUEST in `_meta` rather than fixed at `initialize` — which is
why `ToolSupport` reads it per call and must never cache it. The relay
supplies it inconsistently: ~32% of its tool calls arrive identified and
~68% with no client identity at all. So the surface axis is partial, not
absent, and the split states (`no-session` vs `no-client-info`) are what will
quantify it in the next window. Do not build a surface-mix comparison on it
until that share is known.
