# Usage

Running Knapper, configuring it, connecting clients, and the tool surface
agents see. Deployment to the production LXC is `ops/ct106-runbook.md`.

## Running locally (dev)

```sh
export Vault__RootPath=$HOME/dev-vault          # any directory of notes
export Vault__LockDirectory=/tmp/knapper/locks  # OUTSIDE the vault
export Vault__AuditLogPath=/tmp/knapper/audit.jsonl
export Sync__Mode=open                          # EXPLICIT dev opt-out of the sync gate
dotnet run --project src/Knapper.Mcp            # http://127.0.0.1:3535
curl -s 127.0.0.1:3535/health | jq .status      # "ok"
```

Prereqs: .NET 10 SDK, `ripgrep`, `git` on PATH. The sync gate DEFAULTS to
`heartbeat` (fail closed: without a heartbeat path the server refuses to
start) — `Sync__Mode=open` is the deliberate dev opt-out, and the server
logs a warning while it's active. The CLI reads the same settings:

```sh
dotnet run --project src/Knapper.Cli -- doctor
```

## Configuration reference

Sources, in precedence order: environment variables (`Section__Key=…`) →
`appsettings.json` next to the binary. All settings:

### `Vault:*` — the vault and its budgets

| Key | Default | Meaning |
|---|---|---|
| `RootPath` | — (required) | Absolute vault path. Startup fails without it. |
| `LockDirectory` | — (required) | flock lock files. MUST be outside the vault (enforced at startup). |
| `AuditLogPath` | — (required by Mcp) | Append-only JSONL. MUST be outside the vault (enforced). |
| `CommitStampPath` | "" (off) | Fsync-touched by every successful `knapper commit` run, including "nothing to commit" — the external monitor's git-freshness signal. Outside the vault (enforced). |
| `MetricsPath` | "" (memory-only) | Bounded cumulative counters (tool outcomes, timeouts, stale rejections, truncation, generation-changed, audit-append failures) snapshotted as one JSON line for the external monitor. Outside the vault (enforced). |
| `RipgrepPath` | `rg` | The search engine binary. **Must be ripgrep 15+** — older builds report `"searches": 0` for a query with no matches, emptying the `scanned_files` evidence behind every "no match". `knapper doctor` fails on anything older and names the absolute path it resolved (or, on a miss, the `PATH` it searched — the service's `PATH` is systemd's, not the operator's shell's); Debian's apt package is still 14.x. |
| `QueryTimeoutMs` | 10000 | Wall-clock budget per query. |
| `MaxResultsPerPage` | 200 | Hard page-size ceiling (per-query `maxResults` is clamped to it). |
| `MaxOutputBytes` | 1000000 | Match-text byte budget per search page. |
| `MaxReadBytes` | 4000000 | Whole-file read cap; beyond it reads fail `TooLarge` explicitly. |
| `MaxBatchItems` | 50 | Cap for batch read and batch mutation. |
| `LockTimeoutMs` | 10000 | How long a mutation waits for its locks. |

### `Mcp:*` — the HTTP surface

| Key | Default | Meaning |
|---|---|---|
| `BindAddress` | `127.0.0.1` | IP literal (never "localhost"). Loopback in production too — cloudflared is the only ingress. |
| `Port` | 3535 | |
| `AllowedHosts` | `[]` | Extra Host-header names for HostGuard (the public hostname). Loopback names always allowed. |
| `DisabledTools` | `[]` | Tools removed from list AND call. Unknown names fail startup. E.g. disable the mutation tools for a read-only deployment. |
| `RestrictHealthToLoopback` | true | `/health` (detailed) 404s for non-loopback callers. |
| `LogToolCalls` | true | One Information log line per tool call. |
| `Access:Enabled` | false | Cloudflare Access assertion validation at the origin. |
| `Access:TeamDomain` | — | `https://TEAM.cloudflareaccess.com` (with scheme; compared to `iss`). |
| `Access:Audience` | — | The Access app's AUD tag. Required when enabled. |
| `Access:MonitoringAudience` | — | Second AUD accepted on `/up` only. Must differ from `Access:Audience` — equal values would give the monitoring credential the whole vault surface, so startup **refuses**. Empty is the **single-app setup**: `/up` falls back to accepting the owner audience, so the monitor authenticates with the vault's own token and the credential in its config file on another machine carries every note. Supported, but a downgrade, not a neutral default — and note the asymmetry: an equal AUD refuses startup outright, while an empty one boots clean with `doctor` all-ok, `/health` and `/up` green, and `knapper verify` *skipping* the check that would catch it (no `CF_MONITOR_*` pair to test with). A startup **warning** is the only signal, so two apps is the default (runbook §6.2 creates both before the unit is edited). |
| `Access:AllowLoopback` | true | Same-box health checks need no assertion. "Same-box" requires a loopback TCP peer AND a loopback Host header — tunneled requests proxied by cloudflared arrive from 127.0.0.1 but keep their public hostname, so they are always validated. |

### `Sync:*` — the mutation gate

| Key | Default | Meaning |
|---|---|---|
| `Mode` | `heartbeat` | `heartbeat` (production default — refuses startup without `HeartbeatPath`, so a forgotten env line fails closed) or `open` (dev-only explicit opt-out, logged warning). |
| `HeartbeatPath` | — | File the sync probe touches; mutations require it fresh. |
| `MaxAgeSeconds` | 300 | Staleness threshold. Missing file = blocked (fail closed). |
| `MaxFileBytes` | 5000000 | Largest file Obsidian Sync will carry; a write producing more is refused `TooLargeToSync`. A property of your **Sync plan**, not of Knapper — set it to match. The default is the conservative reading of `ob`'s ambiguous "max 5.00 MB" (5,000,000, not 5,242,880; unbisected as of 2026-08-13). Errors are asymmetric: too low refuses writes loudly, too high strands them **silently**. Applies in every `Mode` — a guard with a mode-shaped hole is a bypass. |

## Connecting clients

- **Claude Code (local dev)**: `claude mcp add --transport http knapper http://127.0.0.1:3535/`
- **Production** (per `ops/ct106-runbook.md`): all clients connect to
  `https://mcp.example.com/` through the Cloudflare tunnel; Access
  Managed OAuth handles claude.ai/Desktop/iOS, a service token handles
  Claude Code. The server's `initialize` response carries the routing
  instruction and trust model, so connected agents get the ground rules
  without per-surface prompt plumbing.

## The tool surface

Thirteen tools, all responses structured content. List/search responses
wear the completeness envelope (`items`, `truncated`, `nextCursor`,
`scannedFiles`, `totalMatches`, `generationStart/End`,
`changedDuringQuery`); `truncated: false` means the scope was exhaustively
searched, and `totalMatches` is null rather than guessed.

### Read / query

| Tool | Essentials |
|---|---|
| `vault_read` | `path`, optional `startLine`/`endLine` (1-based inclusive; end clamps, echoed back). Returns content + **whole-file** `sha256` — the mutation precondition — plus the generation span (freshness signal only). |
| `vault_batch_read` | `items: [{path, startLine?, endLine?}]`. Envelope with a batch-wide generation span; per-item results (each with its own span); one bad file never hides the rest. |
| `vault_stat` | Existence/type/size/mtime/encoding/lines/sha (streamed past the read cap — large attachments still hash) without the body; generation span included. |
| `vault_files` | `pathPrefix`, `glob`, `extensions`, `kind`, `mtimeAfter/Before`, `minSize/maxSize`, `includeSha`, paging. |
| `vault_search` | `pattern` (+`literal`), `caseMode` smart/sensitive/insensitive, `wholeWord`, `multiline`, `pathPrefixes` (max 64), `includeGlobs`/`excludeGlobs` (raw rg semantics, case-sensitive), `extensions` (sugar, case-INsensitive on both surfaces), `contextBefore/After`, `mode` matches/files/counts, paging. |
| `vault_search_frontmatter` | `field`, `op` exists/equals/contains, `value`, `pathPrefix`. Response lists `unparseableFiles` — check it before trusting "no match". |

### Mutations (all conditional; no unconditional write exists)

| Tool | Essentials |
|---|---|
| `vault_edit` | `path`, `expectSha256`, `edits: [{old, new, count=1}]` (exact-count anchors, applied sequentially), `guards` (must exist before AND survive after). |
| `vault_append` | `path`, `expectSha256`, `text`. Same lock+hash discipline. |
| `vault_create` | `path`, `text`. Atomic no-clobber; parent must exist. |
| `vault_mkdir` | One directory level; parent must exist. Deliberate act. |
| `vault_move` | `sourcePath`, `destinationPath`, `expectSourceSha256`. Destination must be absent. |
| `vault_delete` | `path`, `expectSha256`. SOFT — to `.trash/`, structure preserved, collisions timestamped. |
| `vault_batch` | `items: [{kind: edit\|append\|create, …}]`. Everything validated under the locks before the first write; apply phase reports applied/failed/notAttempted per item (not cross-file atomic). |

**The agent write loop**: `vault_read` (fresh) → build edit against that
exact content → `vault_edit` with the returned `sha256`. On
`[PreconditionFailed]`, the file changed under you: re-read and rebuild —
never retry the old base.

## Error codes

Tool errors are structured MCP errors whose message leads with the code:
`[PreconditionFailed] precondition failed: Notes/x.md changed since your read…`

| Code | Meaning / agent action |
|---|---|
| `InvalidPath`, `PathOutsideVault`, `SymlinkRejected`, `BannedPath` | Bad path argument. Fix the path; ALL dot-entries (`.git`, `.obsidian`, `.trash`, `.env`, any hidden segment at any depth) are unaddressable — hidden means invisible on both surfaces. |
| `NotFound` | Missing file, or missing parent for create/move. |
| `AlreadyExists` | No-clobber create/move found the target present. |
| `PreconditionFailed` | Stale `expect_sha256`. Re-read, rebuild, retry with the new hash. |
| `AnchorMismatch` | An edit's `old` matched ≠ `count` times. File untouched. Re-read and re-anchor. |
| `GuardViolation` | Guard absent before, or wouldn't survive after. File untouched. |
| `NotUtf8` | Binary/non-UTF-8 file; text operations refuse it (`vault_stat` still works). |
| `VerifyFailed` | Post-write reopen/byte-compare failed — surface to the user; do not retry blindly. |
| `LockTimeout` | Couldn't get the lock in time (long snapshot or contention). Retry later. |
| `MutationBlocked` | Conflict file or unhealthy sync. Report to the user; never work around it. |
| `InvalidArgument`, `InvalidCursor` | Malformed request; cursors only work with the query that made them. |
| `QueryTimeout` | Budget elapsed with zero progress — narrow the scope. |
| `TooLarge` | File exceeds the read cap; never silently truncated. |
| `TooLargeToSync` | The write would produce a file Obsidian Sync refuses to carry (`Sync__MaxFileBytes`). **TERMINAL — do not retry**, unlike `MutationBlocked`: nothing about the vault's state will change. Split the note. Measured post-transform, so a small insert into a note near the ceiling hits it. |
| `IoError` | Filesystem/OS failure (or rg/git missing). |
| `Internal` | Unexpected server error (a bug, not your request). Details are in the server log, never on the wire. |
| `QueryCancelled` | The request was cancelled at the transport before completion. |

## Health & monitoring

- `GET /health` — detailed (vault root, generation, conflict file names,
  sync age, rg version, audit path). Loopback-only by default.
- `GET /up` — booleans only, for the external monitor. Same status codes as
  `/health` (200 ok / 503 degraded). Degrades on: vault unreachable, sync
  unhealthy, ripgrep missing, audit unwritable, conflict files present, or a
  vault walk that could not COMPLETE (unreadable directory, or the scan's
  wall-clock budget expiring).
- **A stale sync heartbeat alone takes `/up` to 503.** `/up`'s `sync.ok` *is*
  the sync gate's own answer, so the instant mutations start being refused
  `[MutationBlocked]` for a heartbeat older than `Sync:MaxAgeSeconds`, `/up`
  degrades — there is no window in which writes are blocked and the monitor is
  silent. That is the property worth having, and it means every fail-closed
  outage past the budget pages whoever owns `MAILTO`. A blip shorter than the
  budget produces no alert at all (the heartbeat never goes stale), and a
  sustained one mails once, then at most once per `RENOTIFY_SECONDS`, then once
  on recovery. Note the converse does not hold: `sync.ok` true does not mean
  every write will succeed — see `sync.mutationsAllowed` below.
- **Oversized files are the one warning that rides inside a 200.** Nothing is
  blocked by a file Sync will not carry, so the monitor reads
  `.oversized.ok` from the body rather than the status code. A scan that could
  not complete is the other case and *does* degrade — the two are distinct on
  the wire: files found is `200` + `oversized.ok: false`; could-not-tell is
  `503`, and `/health` names which walk failed (`oversized.scanned`,
  `vault.conflictScanComplete`) and why (`oversized.scanError`,
  `vault.conflictScanError`, prefixed `io:` or `timeout:` — a timeout means
  the vault outgrew `OversizedFiles.DefaultBudget` and will not fix itself).
  Both also log a warning. `knapper doctor` prints the reason too. Every `/up`
  boolean means "probed, and fine"; an unknown is never a true.
- The monitor keys check 1 on the status code and check **1b** on
  `.oversized.ok`, guarded on `HTTP 200` — so stranded files alert as
  "vault contains file(s) Sync will NOT carry", a failed scan alerts as check
  1, and neither is silent. Alert *frequency* is the cadence layer's job, not
  the status code's: one mail per failure-set change, a reminder at most once
  per `RENOTIFY_SECONDS`, one on recovery.
- `sync.mutationsAllowed` on `/health` is the **sync gate only**. It is true
  while a note with an unresolved conflict sibling is still refused
  `[MutationBlocked]` — that gate is per-file and reports under
  `vault.conflictFiles`. Read both before concluding writes are unblocked.
- `vault.generation` is **per-process** and restarts at zero with the service.
  It answers "did the vault move during this query"; comparing it across a
  restart is meaningless.
- `knapper status` / `knapper doctor` — one-screen summary / checks with
  exit codes for scripting.
- `knapper version` — the build identity of that binary, alone on stdout:
  `0.2.0+g1f5ff1c`, or `0.2.0+g1f5ff1c.dirty` when built from a tree with
  uncommitted changes. The same string `/health`, `/up` and
  `initialize.serverInfo.version` report, so comparing the deployed service
  against the CLI on the box is a real check. Releases are cut with
  `ops/release.sh` (see `docs/extending.md`).
- `knapper verify --url <url>` — checks a DEPLOYED server from the outside,
  as a real MCP client over the same transport agents use. **Read-only, and
  it must stay that way**: it is pointed at the production vault, where a
  stray write syncs to the user's devices. Checks `tools/list` against the
  locked 13 names (a partially-registered surface answers without
  complaint), the routing instruction, scan evidence on a no-match search
  (the live ripgrep-15 check), the completeness envelope, whole-file SHAs,
  and a typed `[NotFound]` from the mutation surface. The tool line reports
  the count it saw (`ok tools/list is EXACTLY the locked surface (13 tools)`)
  — the set comparison is the stronger check, but a deployment checklist that
  asks for a number should not have to go get it from a second call. Through a tunnel it
  also checks the ingress contract: unauthenticated callers refused,
  `/health` 404 from outside, `/up` disclosing booleans only, and the
  monitoring token refused at the vault surface. Against a loopback URL
  those ingress checks print `skip` — the same-box exemption is the thing
  they would be testing. Exit 0 = all passed.

  It also always checks that `/up` and the MCP endpoint report the SAME
  build — one URL reaching two processes (a stale unit still bound to the
  port) is otherwise invisible, and every other green check would be
  describing whichever one answered.

  ```sh
  CF_ACCESS_CLIENT_ID=… CF_ACCESS_CLIENT_SECRET=… \
  CF_MONITOR_CLIENT_ID=… CF_MONITOR_CLIENT_SECRET=… \
    knapper verify --url https://mcp.example.com/
  ```

  `--expect-version X.Y.Z` adds the post-upgrade check: **is the service
  running the build that was just installed?** A restart onto the old binary
  passes every other check here, because the old build satisfies the same
  contract. A bare `X.Y.Z` matches any build of that release except a
  `.dirty` one; pass the full `X.Y.Z+g<ref>` to demand one exact build.
  `--expect-this-version` uses the version of the `knapper` binary being run,
  so invoking the CLI out of the freshly-unpacked tarball needs nothing typed
  by hand (runbook §10.3).
- The audit log (`Vault:AuditLogPath`) is JSONL, one entry per mutation
  attempt including rejections: timestamp, client, request id, op, path,
  outcome, before/after SHA. `knapper audit-tail 50` shows the recent end.

### Logs

The server writes **no log file**. It logs structured JSON to stdout and
systemd routes that to journald, which owns rotation, size caps and
retention (`SystemMaxUse` etc., configured in runbook §3b). Under
`ProtectSystem=strict` the service could not write `/var/log` even if it
wanted to. The audit log above is a separate, deliberate artifact — it is
the mutation record, not the diagnostic trail.

This matters for support: several tool errors deliberately withhold detail
from the client (`[Internal] unexpected server error — see server logs`,
`filesystem failure — … details in the server log`), so the journal is the
other half of those messages.

Because the records are JSON, the fields each call already logs are
queryable rather than greppable — placeholders land under `State`:

```sh
journalctl -u knapper -o cat | jq -r 'select(.LogLevel=="Error") | .Message'
journalctl -u knapper -o cat | jq -r 'select(.State.Tool=="edit_note") | .State.Outcome'
journalctl -u knapper --since -1h -p warning     # warnings and worse
journalctl -t knapper --boot=-1                  # the boot before this one
```

A tool call logs `Tool`, `Outcome`, `ElapsedMs` and `Client`; the ASP.NET
trace identifier rides in a log scope and also appears in the audit entry,
which is what lets one request be followed across both.
