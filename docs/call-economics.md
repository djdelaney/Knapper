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

## Observed — 2026-08-24, 7-day window

Taken **before** the CALL ECONOMICS instructions were deployed; CT 106 was
running `knapper 0.5.3+g9a7c6fd`. Figures are probe-adjusted (six `verify`
runs = 24 calls removed); the timing rows below come from the ad-hoc command
that preceded the script and still include the probe, which moves them by
well under a percent.

| Metric | Baseline |
|---|---|
| Calls (human client) | 793 of 817 |
| Server mean / max | 12.2ms / 999ms |
| Batched-read share | **9.2%** (23 `vault_batch_read` vs 226 `vault_read`) |
| Batched-mutation share | **13.4%** (23 `vault_batch` vs 149 single edit/append/create) |
| Reads per mutation | **1.45** |
| Concurrent (<1s apart) | 13% (106 calls) |
| Sequential hops (1–20s) | 48% (390 calls), mean **7.2s** apart |
| Relay wait in window | **~47 min** |
| Server work in window | ~10s — **0.36%** of the wait |

Tool mix: `vault_search` 285, `vault_read` 226, `vault_edit` 143, `vault_stat`
46, `vault_files` 32, `vault_batch` 23, `vault_batch_read` 23, `vault_create`
6, `vault_move` 5, `vault_delete` 2, `vault_mkdir` 2.

Outcomes were healthy: 795 `ok`, 16 `NotFound`, 2 `AnchorMismatch` (~1.3% of
149 edits, consistent with the 0.3% the §8b transcript mining predicted), 2
`GuardViolation`, 1 `InvalidArgument`, 1 `MutationBlocked`.

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
restart, not from the restart.
