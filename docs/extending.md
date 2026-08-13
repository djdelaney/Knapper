# Extending Knapper

How to add capability without breaking the contracts. Read
[architecture.md](architecture.md) first; the silent invariants live in
[CLAUDE.md](../CLAUDE.md) and are the things a reviewer will hold you to.

## Ground rules (from the brief, non-negotiable)

- **No unconditional write may ever exist** — not exposed, not internal,
  not "for tests". Every mutation of an existing file takes
  `expect_sha256`; a safe wrapper beside an unsafe original is a bypass.
- **Caps have protocol semantics.** A new limit either returns
  `truncated: true` + a usable cursor, or a typed error. Silent partial
  success is forbidden; "no match" must mean exhaustively searched.
- **Fail closed.** New failure modes block the operation with a typed
  error; nothing ever falls back to a weaker path.
- **Verification is by content.** A new write path ends in
  reopen-and-byte-compare, no exceptions.

## Adding an MCP tool

1. **Core first.** Implement the behavior as a Core service method with a
   typed result record. Query-shaped results return
   `QueryEnvelope<T>`; mutations go THROUGH
   `VaultMutationService.Mutate()` (or replicate its critical section
   exactly, as move/delete do). Paths from callers go through
   `VaultPathResolver.Resolve` — nothing else may combine user input with
   the vault root.
2. **Tool class** in `src/Knapper.Mcp/Tools/` (one class per tool):
   ```csharp
   [McpServerToolType]
   public sealed class VaultFooTool(FooService foo, ToolSupport support)
   {
       [McpServerTool(Name = "vault_foo", UseStructuredContent = true, ReadOnly = …, OpenWorld = false)]
       [Description("…what it does, and the contract the agent must know…")]
       public FooResult Foo([Description("…")] string path, …) =>
           support.Run("vault_foo", () => foo.Do(path, support.Caller()));
   }
   ```
   Every attribute sets `UseStructuredContent = true` (the SDK default is
   text-only). Every body runs through `support.Run` so `KnapperException`
   reaches the wire as `[Code] message`. Mutating tools pass
   `support.Caller()` into Core for the audit trail.
3. **Register** in `ToolSurface.All`. The name is a locked client contract
   from the moment it ships — renames are breaking changes.
4. **Wire test** in `Knapper.Mcp.Tests`: the surface-lock test updates
   itself via `ToolSurface.All`, but add a round-trip through
   `McpSurfaceTests.ConnectAsync` — the wire tests are what catch SDK
   binding/registration traps the direct tests can't see (they caught two).
5. Wire DTOs over Core enums: tool parameters use strings/POCOs (see
   `EditOp`, `BatchOp`), parsed with a typed `InvalidArgument` on bad
   values — enum JSON binding is not part of the wire contract.

## Adding a query capability

Extend the query record (`QueryModels.cs`) with an optional-by-default
field, thread it through the service, and:

- include the new filter field in the **cursor fingerprint** (otherwise an
  old cursor silently replays against different filters);
- keep result ordering deterministic (path-ordinal; rg's `--sort=path` on
  the search side, the global ordinal sort on the lister side);
- if the field affects which files are VISIBLE, update both the lister and
  the search args, and extend the `Agrees_with_ripgrep` differential test —
  the two surfaces must never disagree about what exists.

New rg flags go into the args build in `VaultSearchService`; the baseline
(`--no-config --no-ignore --no-follow --sort=path`) is not negotiable.

## Adding a mutation

Model it on `Move`/`Delete`: resolve → conflict gate → sync gate → locks
(multi-path via `AcquirePathLocks`, sorted, global-shared first) → fresh
read → SHA check → do the work with `AtomicFile`/`Posix.Link` primitives →
`VerifyOnDisk` → `generation.Increment()` → audit (successes AND
rejections) → typed result. Add:

- unit tests proving every rejection leaves the file untouched;
- a two-process race via `Knapper.MutationProbe` (add a subcommand) if the
  operation has a race-shaped failure mode;
- a wire round-trip test.

## Adding an error code

Extend `VaultErrorCode` with an XML doc saying when it fires, throw it as
`KnapperException`, and document it in `docs/usage.md`'s table. Codes are
wire-stable once shipped — agents branch on them.

## Adding configuration

POCOs in `Core/Options/`, bound in `Knapper.Mcp/Program.cs` (and
`Knapper.Cli/Program.cs` if the CLI needs it), defaults in both
`appsettings.json` files, documented in `docs/usage.md`. A setting that is
security-relevant gets validated at startup in Program.cs's forced-singleton
block — misconfiguration refuses boot, it doesn't surface on first call.

## Testing conventions

- xunit + Shouldly; test names are sentences
  (`Stale_sha_rejects_untouched_and_the_rejection_is_audited`).
- Mutation tests build a fresh `MutationVault` per test (no shared state);
  query tests share a read-only `FixtureVault` per class.
- The MCP factory's server runs a REAL filesystem watcher over its vault, so
  any test pinning `changedDuringQuery`/`changedDuringRead` = false must use
  an ISOLATED `KnapperMcpFactory` that nothing mutates — a delayed watcher
  event from a sibling test's edit legitimately advances the generation and
  flips the flag (observed as a real one-in-many-runs flake, 2026-08-09).
- Cross-process claims need cross-process tests: the probe binaries are
  copied into the test output by project reference and spawned with
  `dotnet exec`. An in-process test of flock proves nothing.
- Wire behavior is tested through the SDK's `McpClient` against
  `WebApplicationFactory` — the same JSON-RPC path Claude uses.
  `RemoteIpStartupFilter` declares which caller the factory simulates
  (loopback vs off-box); without it every loopback-sensitive control fails
  closed on TestServer's null remote address.
- **The acceptance tier (`Knapper.AcceptanceTests`) is the black box** —
  brief §13's definition of done. `AcceptanceServer` spawns the REAL
  `Knapper.Mcp` binary as separate processes (`dotnet exec`, ephemeral
  ports, env-var config) and talks to them over real sockets; two servers
  over one vault + lock dir are the two-process topology. The project must
  never load Knapper types in-process — if a scenario needs server-side
  state, add a config knob or read the disk, don't reach into the process.
  Deterministic faults go through env-gated injectors
  (`KNAPPER_FAULT_SHORT_WRITE`): an injector may only BREAK an operation
  the contract must then catch; it must never create a path around a
  contract. What this tier cannot cover stays in the live CT 106 sequence
  (runbook §§8–9): cloudflared, alert delivery, vzdump/PBS, fail-closed
  service stops.

## Runbook conventions

`ops/ct106-runbook.md` is checked by `ops/runbook-lint.sh` in CI: every fenced
`sh` block must parse, every bare `§N` must resolve to a heading in that file
(brief references are written `brief §N`), every `<placeholder>` must be in the
script's declared list, and the smoke unit must keep pointing away from
`/vault`. Adding a placeholder means adding it there too — that is the cost of
the list being something a deployment can key on. What the lint cannot check is
whether a procedure is correct or in the right order; six review rounds' worth
of those findings are in git history.

## Build conventions

- Central Package Management: versions in `Directory.Packages.props` only;
  csproj `<PackageReference>` without `Version=`. Adding a dependency means
  editing both files.
- `TreatWarningsAsErrors` repo-wide. Unix-only (`SupportedOSPlatform`
  linux+macos in `Directory.Build.props`) — don't add Windows guards.

### Adding a file under `ops/`

`ops/publish.sh` stages what ships explicitly, so a new file is omitted by
default. That is the right default — nothing unreviewed slips into an artifact
— but it used to be silent, and `ops/logrotate/knapper-sync-log` shipped
missing in v0.2.0 because of it: committed, never staged, and the runbook's
`cp` from `/opt/knapper` on CT 106 was the first thing to notice.

Two gates now stand on either side of that:

- The **required-path list** (paths the runbook installs) fails the publish if
  a named file is absent from the archive — it catches a file DELETED from the
  repo while the runbook still tells an operator to install it.
- The **coverage gate** fails the publish if any file under `ops/` is neither
  in the archive nor on `NOT_SHIPPED` — it catches a file ADDED to the repo and
  never staged. `NOT_SHIPPED` is the runbook, `publish.sh`, `release.sh`,
  `runbook-lint.sh` and `version.sh`: repo-side tooling with no business on a
  deployed host.

So adding a file under `ops/` means deciding, in the script, which it is. That
is the whole point — writing the deliberate omissions down is what stops "not
shipped" and "forgotten" from being the same state. The enumeration is
`git ls-files -co --exclude-standard`, so the gate fires on an uncommitted new
file too, and never on ignored droppings.

## Versioning and releases

One carrier: `<Version>` in `Directory.Build.props`. Everything downstream is
derived, and **adding a second carrier is the thing not to do** — a second
place to spell the version is a way for the two to disagree while both look
authoritative. (Mailvec has two, `manifest.json` and the props file, and pays
for it with a drift check that refuses to bump when they diverge.)

The chain:

```
Directory.Build.props <Version>          the one carrier
  → ops/version.sh                       + git revision → 0.2.0+g1f5ff1c[.dirty]
    → KnapperStampRevision (props)       → AssemblyInformationalVersion, all assemblies
      → Knapper.Core.BuildInfo           the one read
        → serverInfo.version, /health, /up, `knapper version`
    → ops/publish.sh                     the artifact filename
```

- **Bump with `ops/release.sh`** (`--patch` default, `--minor`, `--major`;
  `--ship` pushes, waits for green CI, and tags only on success). Never edit
  the property by hand: the script is what keeps the property, the commit and
  the tag describing one build, and it refuses a bump whose tag already exists.
- **`--minor` for any client-facing contract change**: a tool name or shape, a
  new error code, a config knob deployments must set. Tool names are a client
  contract — a rename is a version bump, not a refactor.
- **Read the version through `BuildInfo`, never `Assembly.GetName().Version`.**
  That is `AssemblyVersion`: four numeric parts, so it silently drops the
  revision and any prerelease suffix and reports an off-tag build as the
  release. Nor `GetEntryAssembly()` — under `dotnet test` that is the test host.
- **The `.dirty` suffix is load-bearing.** It is what makes a build off
  uncommitted edits distinguishable from the tagged release at the artifact
  filename and at every runtime surface, and what
  `knapper verify --expect-version 0.2.0` refuses. Publish releases from a
  clean tagged tree.
- `VersionSurfaceTests` (Core) pins the derivation and the single carrier;
  `HealthAndGuardTests.Every_surface_reports_the_same_build` pins that the
  three reporting surfaces agree. Both failure modes are otherwise silent —
  each surface keeps returning something version-shaped.

## Ideas already scoped (not yet built)

- **Live-server write races**: `knapper verify --url` now covers the
  deployed service READ-ONLY (runbook §5 pre-ingress and §6 through the
  tunnel), and that boundary is deliberate — it runs against Helios, where
  a stray write syncs to Dan's devices. The write-side scenarios
  (two-process stale edit, simultaneous create) live in §8b's
  disposable-vault session instead; automating them there against a
  deployed URL is the remaining piece, and it must never be folded into
  `verify`.
- **Read-only deployment profile**: `Mcp:DisabledTools` with the seven
  mutation tools listed, as a documented one-liner.
- **Per-client credentials** (brief §8 "where practical"): Access already
  distinguishes identities in the audit log; separate Access apps per agent
  surface would let Cloudflare policy differ per client.
- **Opt-in tool-argument logging** (`Mcp:LogToolArguments`, JSONL outside
  the vault): considered for client-behavior validation, superseded by
  transcript mining for now — revisit if the §8b smoke test needs
  finer-grained evidence than tool names + audit + metrics give.
- **`vault_search` context in files/counts modes** is intentionally absent;
  matches mode covers the need.
- **Obsidian-flavored queries** (backlinks, tags-as-index) — worth doing
  only if agents demonstrably need more than frontmatter + full-text.

## Open decisions (owners, and what re-opens them)

Consolidated from five review rounds so they are decided once, here — not
re-litigated per review.

- **Git remote for the vault repo** — Dan's call, blocked on the
  credential sweep closing (brief §10). Until then the repo is local-only;
  `knapper doctor` fails loud if a remote appears. Nothing in this repo
  should add push/remote support before that decision.
- **Monitor thresholds** (`MAX_QUERY_TIMEOUTS` etc. in
  `knapper-monitor.conf`) — shipped as educated guesses, explicitly to be
  tuned after observing real traffic post-cutover.
- **Cursor HMAC** — deferred twice (round 3 + security review agree):
  cursors are forgeable only within the same query and only self-harming.
  Re-opens ONLY if the threat model ever includes hostile MCP clients.
- **B1 server-native OAuth** — the brief's original ingress, superseded by
  B2 (Cloudflare Access) per the sanctioned deviation. Re-opens only if
  Access becomes a limitation (e.g. a client surface that can't do
  Managed OAuth or service tokens).
- **§8b re-run cadence** — the behavioral smoke test re-runs after major
  Claude model updates (steering drifts); failures fix descriptions and
  instructions, never the contract.

Decided and CLOSED (do not re-open without new evidence): no case-folding
of paths (ext4 legitimately distinguishes; the requirement is a
case-sensitive FS); dot-paths unaddressable on the mutation surface
(hidden on BOTH surfaces); multiline `-U` column semantics past the first
line (accuracy nit, documented); the batch-read worst-case memory ceiling
(designed scale); `CreateDirectory` holding no per-path lock (no content
involved); resolver-gate rejections staying unaudited (no vault object to
audit).

Also closed: **birth time is not preserved on the Linux CT, and nothing
here reads it.** `ob sync` restores mtime but not btime — the package ships
btime-setting binaries for darwin and win32 only, because Linux has no API
to set a file's creation time, so a synced note on CT 106 is born at
download time while its mtime stays correct. Verified 2026-08-12 during
deployment (birth 23:00:56 = the download, mtime 18:53:13 = the source
edit). Knapper never reads creation time on any surface: `read_file` and
`stat` report mtime, `list_files` filters on `mtimeAfter`/`mtimeBefore`,
and `VaultReadService`/`VaultFileLister`/`FileAgeSyncGate` all read
`LastWriteTimeUtc`. The sole `Created` in the codebase is a
`FileSystemWatcher` event kind, which is an event name, not a timestamp
read. Permanent, cosmetic, and outside every contract — re-opens only if a
tool ever starts exposing or filtering on creation time, which would mean
that tool reports a value that is wrong in production and right in dev.
