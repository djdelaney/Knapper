# Architecture

Knapper turns a distributed agent-concurrency problem (many agents editing
one Obsidian Sync vault from many machines) into one server-side transaction
problem: a single always-on MCP server is the only interface agents have,
and every write it performs is conditional, locked, atomic, and verified.

```
Human Obsidian clients ◄──► Obsidian Sync cloud ◄──► obsidian-headless (ob sync --continuous)
                                                              │
                                                              ▼
Agents (claude.ai / Desktop / Code) ──► cloudflared ──► Knapper.Mcp ──► /vault
                                        (Cloudflare        │ SHA preconditions + flock locks
                                         Access = auth)    ├── audit JSONL (outside /vault)
                                                           ├── metrics snapshot (outside /vault)
                                                           └── conflict + sync-health gates
                                                              │
                                        knapper commit  ◄─────┘  (systemd timer, vault-wide lock)
                                        = git history, local-only + fsynced success stamp

Proxmox host: knapper-monitor.sh ──► /up via tunnel + commit-stamp age + metrics
              deltas (pct exec) ──► silent-on-success mail to alerts@
```

## Projects

| Project | Kind | Purpose |
|---|---|---|
| `Knapper.Core` | library | Everything that touches vault bytes: path containment, hashing, atomic commits, locks, query services, mutation service, gates, audit, git job. No ASP.NET, no MCP types. |
| `Knapper.Mcp` | web app | The MCP host: 13 locked tools over Streamable HTTP, Cloudflare Access origin validation, HostGuard, `/health` + `/up`. Thin — tools map wire shapes to Core calls. |
| `Knapper.Cli` | exe (`knapper`) | Admin: `git-init`, `commit` (the snapshot job systemd runs), `status`, `doctor`, `audit-tail`. Shares Core, so the commit job uses the *same* lock implementation as mutations. |
| `tools/Knapper.LockProbe` | exe | Child process for genuine two-process lock tests. |
| `tools/Knapper.MutationProbe` | exe | Child process for two-process stale-edit / create races through the real `VaultMutationService`. |
| `tests/Knapper.Core.Tests` | tests | Unit + differential + multi-process race tests. |
| `tests/Knapper.Mcp.Tests` | tests | Wire-level tests through the SDK's own `McpClient` against an in-process host, incl. the Cloudflare Access topology. |
| `tests/Knapper.AcceptanceTests` | tests | The brief-§13 black box: REAL server processes spawned over real HTTP — two-process transport races, short-write fault injection, rg-oracle equivalence. Loads no Knapper types in-process by design. |

Dependency direction is strictly `Mcp`/`Cli` → `Core`. Core's only NuGet
dependency is YamlDotNet; the Mcp host adds the MCP SDK and JwtBearer.
Everything is Unix-only by design (`SupportedOSPlatform` linux+macos,
asserted repo-wide): the guarantees stand on flock(2), link(2), rename(2),
the atomic pathname exchange (renameat2 `RENAME_EXCHANGE` / renamex_np
`RENAME_SWAP`), and Unix file modes.

## Knapper.Core layout

```
Core/
  KnapperException.cs      VaultErrorCode + the one exception type crossing layers
  KnapperMetrics.cs        bounded counters → atomic JSON snapshot (the monitor's rate signals)
  Interop/Posix.cs         flock / link / linkat-nofollow / creat / exchange / fsync-dir / realpath (LibraryImport)
  Options/                 VaultOptions, McpOptions, AccessOptions, SyncOptions (POCOs)
  Vault/
    VaultPathResolver.cs   THE gate for agent-supplied paths (traversal/symlink/dot-segments)
    VaultPath.cs           proof-of-validation record (internal ctor)
    VaultHash.cs           SHA-256 lowercase hex — the precondition currency
    AtomicFile.cs          THE writer: temp+fsync → last-instant SHA check → exchange/link → verify
    PathContainment.cs     realpath-canonicalized "is this inside the vault" for startup checks
    CaseSensitivityProbe.cs  detects case-insensitive vault FS (doctor fails; server warns)
  Locking/
    VaultLockManager.cs    cross-process flock: per-path EX + vault-wide commit lock (SH/EX)
  Generation/
    VaultGenerationCounter.cs  monotonic counter + filesystem watcher (control dirs filtered)
  Query/
    RipgrepRunner.cs       rg subprocess, structured args only, budget/timeout enforcement
    VaultSearchService.cs  vault_search: matches / files-only / counts, streaming rg --json
    VaultFileLister.cs     vault_files: native walk, differential-tested against rg --files
    VaultReadService.cs    vault_read / vault_batch_read / vault_stat
    FrontmatterSearchService.cs  vault_search_frontmatter (YamlDotNet)
    Globbing.cs            rg/gitignore-style glob → regex (lister side of the equivalence)
    QueryCursor.cs         fingerprint-bound continuation cursors
    QueryModels.cs         QueryEnvelope<T> + all query/response records
  Mutation/
    VaultMutationService.cs  THE mutation surface: edit/append/create/mkdir/move/delete/batch
    ConflictDetector.cs      Sync conflict-file gate
    SyncGate.cs / FileAgeSyncGate.cs  ISyncGate: mutations fail closed on unhealthy sync
    AuditLog.cs              append-only fsynced JSONL, outside the vault
    MutationModels.cs        EditSpec, BatchItem, results, AuditContext
  Git/
    GitCommitJob.cs        the vault's only committer (vault-wide lock, staged secret scan)
    SecretScanner.cs       credential-shaped-content tripwire
```

## The two layers, and their contracts

**Query layer** (brief §6). Replaces the local `rg`/`find`/ranged-read
agents lose at cutover. Search shells out to ripgrep with `ArgumentList`
(never a shell) and always passes `--no-config --no-ignore --no-follow
--sort=path`; the file lister is a native walk so mtime/size filters don't
need a second stat pass. Hidden means invisible on BOTH surfaces: dotfiles
are skipped at every depth by queries AND unaddressable through the
resolver — a differential test against real `rg --files` keeps the two
query implementations agreeing, and path ordering is raw UTF-8 bytes
everywhere (rg's `--sort=path` order). Every list/search response wears
the **completeness envelope**: `items`, `truncated`, `nextCursor`
(fingerprint-bound to the query's filters), `scannedFiles`, `totalMatches`
(explicit null when unknown — never guessed), and the generation span
(`generationStart/End`, `changedDuringQuery`); read/stat/batch-read
responses carry the same span (freshness signal only — the SHA remains
the precondition).

**Transaction layer** (brief §7, semantics ported from
`vault-edit.reference.py`). Every mutation of an existing file requires
`expect_sha256` and runs the fixed critical section:

```
lock → fresh read → SHA check → transform → validate guards
     → AUDIT INTENT (fsynced, before the first byte)
     → hidden same-dir temp + fsync → final SHA check → atomic replace
     → reopen and byte-compare → unlock
```

The write-ahead audit intent means no change can exist that no audit line
explains: if the audit sink is down, the mutation is refused BEFORE any
write (fail closed); a post-write audit failure keeps the success receipt
and surfaces through the durable audit-failure metric instead. Anchored
edits demand exact occurrence counts; guards must exist before and survive
after; create is hard-link no-clobber; move and soft delete share one core with two
rules. It never removes a pathname another writer could own — the only names
it deletes are its own GUID temps, and the source is CAPTURED with `rename(2)`
rather than unlinked, then examined under a private name (a check-then-unlink
destroyed an external writer's replacement while reporting success, fixed
2026-08-19). And a public pathname holds the content at every instant: the
destination is committed with `link(2)` BEFORE the source is captured, so no
crash can leave the note reachable only through hidden temps. The order is:
private link + verify → containment → courtesy check that the source is
unchanged → commit → containment again (a directory can be swapped between the
check and the link) → verify the destination → capture the source → confirm
what was captured was ours, linking it back if not. A published destination is
never retracted; when the capture reveals a raced source the operation fails
with a visible duplicate, named in the error, rather than deleting a pathname
other writers can see. `.trash/` chains are checked for symlinked components
and proved by `realpath` to be inside the vault. Batch validates every item under sorted-order locks before the
first write. Replace commits by atomic exchange rather than overwriting
rename: whatever the target held at the instant of the commit ends up under
the hidden temp and is judged there by CONTENT — classified non-following,
then hashed against the expected base; metadata is never a content
precondition, because an equal dev/inode/size/mtime tuple does not prove
unchanged bytes (round four) — and from
that instant the temp is RETAINED by default until a branch proves it safe
to discard (ownership is device+inode plus the exact written bytes, never
byte equality alone). A raced commit is exchanged straight back, so the
rejection leaves the external bytes at the note's own pathname, and when a
second race makes exact rollback impossible the displaced version is
republished visibly (no-follow) as a `(Knapper displaced …)` conflict
sibling that blocks the note until a human reconciles. The source of a
move/delete is linked no-follow and inspected under the private name, so a
note swapped for a symlink or a FIFO mid-operation is refused rather than
followed or blocked on; every content read taken under the path locks
classifies the file first. Every mutation proves
its target's parent resolves inside the vault on both sides of the commit,
so a parent directory swapped for a symlink mid-operation surfaces as
`PathOutsideVault` instead of a success receipt for an out-of-vault write.
Verification is by content, never by receipt — the vault has
a documented history of writes that reported success without landing.

## Locking model

flock(2) advisory locks in a directory outside the vault (lock files must
never sync), chosen over lock-file-existence schemes because flock releases
on process death — a crashed holder can't wedge the vault.

- **Mutation**: vault-wide lock SHARED, then per-path lock EXCLUSIVE.
  Different paths proceed concurrently.
- **Git snapshot** (`knapper commit`): vault-wide lock EXCLUSIVE — drains
  in-flight mutations, blocks new ones, can never capture a
  prepared-but-unverified write.
- **Multi-path** (batch/move): global shared once, then per-path locks in
  sorted order. Fixed ordering + the commit job taking no path locks =
  no cycle in the lock graph = deadlock structurally impossible.

The locks are cross-PROCESS (the CLI committer is a separate process), and
proven by tests that spawn real second processes (`LockProbe`,
`MutationProbe`), not just tasks.

## Gates (fail closed, brief §8)

- **Conflict gate**: a `Name (Conflicted copy ...).md` sibling blocks
  mutations to both the original and the sibling until a human reconciles.
  Agents never resolve conflicts.
- **Sync gate**: in production (`Sync:Mode=heartbeat`) mutations require the
  heartbeat file — touched every minute by a probe that checks the
  obsidian-headless unit — to be fresh. Missing or stale = mutations
  blocked. Reads stay up.
- **When**: both gates run before the locks and again with them held (batch:
  after its validate phase, the last moment nothing has been written). The
  second pass exists because waiting for a lock takes real time; it narrows
  the staleness window rather than closing it, since the locks bind
  cooperating Knapper processes only and Sync honors none of them.
- **Startup**: missing vault root; a vault root the process cannot write;
  lock dir, audit path, or metrics path equal to or inside the vault
  (realpath-canonicalized, symlinked ancestors included); bad Access config
  — including a `MonitoringAudience` equal to `Audience`, which would hand
  the monitoring credential the whole vault surface; unfetchable signing
  keys; invalid sync mode — all refuse startup rather than degrade.
  `Sync:Mode` DEFAULTS to `heartbeat`, so a forgotten config line refuses
  startup instead of silently ungating mutations. A case-insensitive vault
  filesystem fails `knapper doctor` (production gate) and warns at server
  startup (dev) — but a probe that cannot write at all is a refusal, not a
  warning: booting would serve reads while every mutation failed. A ripgrep
  older than the supported major takes the same gate/warn split: doctor fails,
  startup warns and keeps serving, because the cost is a degraded
  `scanned_files` rather than a broken vault.
- **Monitoring** (brief §8, outside the CT): `KnapperMetrics` snapshots
  bounded counters — tool outcomes, query timeouts, stale-write
  rejections, truncation, generation-changed responses, audit-append
  failures — to an atomic JSON file; the Proxmox-host monitor evaluates
  per-window deltas alongside `/up` and the commit job's fsynced success
  stamp (which distinguishes a quiet vault from a dead timer), mailing
  silent-on-success alerts.

## Security model (brief §9, B2)

The Cloudflare tunnel is the only network path; Cloudflare Access is the
auth gate (Managed OAuth for claude.ai/Desktop/iOS, service token for
Claude Code). The origin additionally validates Access's
`Cf-Access-Jwt-Assertion` (issuer, audience, RS256-pinned signature
against the team's JWKS) so an edge misconfiguration can't silently expose
the vault. The local-caller exemption requires a loopback TCP peer AND a
loopback Host header — cloudflared delivers every tunneled internet
request from 127.0.0.1, so the peer alone proves nothing (this was the
P0 of the first review round; `AccessTopologyTests` pins it). A separate
path-scoped monitoring audience is accepted on `/up` only. HostGuard pins
Host/Origin headers against DNS-rebinding. The caller's identity (email /
service-token common name) flows into every audit entry, and Access being
disabled warns loudly at startup on every bind.
