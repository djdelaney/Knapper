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
```

The journal is the **only** telemetry covering every client. Cowork, Desktop,
mobile and claude.ai leave nothing on the operator's disk, so client-side
transcript mining sees Claude Code and nothing else. `Mcp:LogToolCalls`
defaults to true and the production unit does not override it.

Read the RATIOS block, not the counts: two windows never carry the same amount
of work, so "fewer calls" measures how busy the week was. The script excludes
`knapper verify --url` traffic by default — it spends a fixed 4 calls per run
from a service-token identity, so leaving it in dilutes every ratio by however
often someone happened to run verify.

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
`vault_batch_read` plus one `vault_batch` does N edits in two. The number to
watch is batched-mutation share, with reads-per-mutation as the corroborating
signal — it cannot fall below ~1.00 while the fresh-read rule stands, so a
drop toward 1.00 means batching, not skipped reads.

⚠️ **Do not compare a window that straddles the deploy.** Server instructions
are delivered at `initialize`, so sessions already open keep the old text until
they reconnect. Start the "after" window from the first reconnect after the
restart, not from the restart. With 0.5.5 deployed 2026-08-24, the first
`ops/call-economics.sh 7` whose window carries no pre-change traffic is on or
after **2026-08-31**; running it earlier averages the two states together and
understates whatever the change did.
