# Usage

Running Knapper, configuring it, connecting clients, and the tool surface
agents see. Deployment to the production LXC is `ops/ct106-runbook.md`.

## Running locally (dev)

```sh
export Vault__RootPath=$HOME/dev-vault          # any directory of notes
export Vault__LockDirectory=/tmp/knapper/locks  # OUTSIDE the vault
export Vault__AuditLogPath=/tmp/knapper/audit.jsonl
dotnet run --project src/Knapper.Mcp            # http://127.0.0.1:3535
curl -s 127.0.0.1:3535/health | jq .status      # "ok"
```

Prereqs: .NET 10 SDK, `ripgrep`, `git` on PATH. The sync gate defaults to
`open` in dev (mutations not gated; the server logs a warning). The CLI
reads the same settings:

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
| `RipgrepPath` | `rg` | The search engine binary. |
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
| `Access:MonitoringAudience` | — | Optional second AUD accepted on `/up` only. |
| `Access:AllowLoopback` | true | Same-box health checks need no assertion. "Same-box" requires a loopback TCP peer AND a loopback Host header — tunneled requests proxied by cloudflared arrive from 127.0.0.1 but keep their public hostname, so they are always validated. |

### `Sync:*` — the mutation gate

| Key | Default | Meaning |
|---|---|---|
| `Mode` | `open` | `open` (dev: no gate, logged warning) or `heartbeat` (production). |
| `HeartbeatPath` | — | File the sync probe touches; mutations require it fresh. |
| `MaxAgeSeconds` | 300 | Staleness threshold. Missing file = blocked (fail closed). |

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
| `vault_read` | `path`, optional `startLine`/`endLine` (1-based inclusive; end clamps, echoed back). Returns content + **whole-file** `sha256` — the mutation precondition. |
| `vault_batch_read` | `items: [{path, startLine?, endLine?}]`. Per-item results; one bad file never hides the rest. |
| `vault_stat` | Existence/type/size/mtime/encoding/lines/sha without the body. |
| `vault_files` | `pathPrefix`, `glob`, `extensions`, `kind`, `mtimeAfter/Before`, `minSize/maxSize`, `includeSha`, paging. |
| `vault_search` | `pattern` (+`literal`), `caseMode` smart/sensitive/insensitive, `wholeWord`, `multiline`, `pathPrefixes`, `includeGlobs`/`excludeGlobs`, `extensions`, `contextBefore/After`, `mode` matches/files/counts, paging. |
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
| `InvalidPath`, `PathOutsideVault`, `SymlinkRejected`, `BannedPath` | Bad path argument. Fix the path; `.git`/`.obsidian`/`.trash` are never accessible. |
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
| `IoError` | Filesystem/OS failure (or rg/git missing). |
| `Internal` | Unexpected server error (a bug, not your request). Details are in the server log, never on the wire. |
| `QueryCancelled` | The request was cancelled at the transport before completion. |

## Health & monitoring

- `GET /health` — detailed (vault root, generation, conflict file names,
  sync age, rg version, audit path). Loopback-only by default.
- `GET /up` — booleans only, for the external monitor. Same status codes as
  `/health` (200 ok / 503 degraded). Degrades on: vault unreachable, sync
  unhealthy, ripgrep missing, audit unwritable, conflict files present.
- `knapper status` / `knapper doctor` — one-screen summary / checks with
  exit codes for scripting.
- The audit log (`Vault:AuditLogPath`) is JSONL, one entry per mutation
  attempt including rejections: timestamp, client, request id, op, path,
  outcome, before/after SHA. `knapper audit-tail 50` shows the recent end.
