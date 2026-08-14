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
- **The shell tier (`tests/shell/run.sh`, run in CI) is where `ops/*.sh`
  gets tested**, because the .NET suite cannot reach it. Stub every external
  binary onto `PATH` — for `knapper-monitor.sh` that means `curl`, `pct` and
  above all `sendmail`: a test that can really mail is a test that pages the
  operator.
- **A script that READS a health field needs a case for each value, including
  the alerting one.** `knapper-monitor.sh` read `jq -r '.oversized.ok //
  "absent"'`, and jq's `//` is falsy-triggered rather than absence-triggered —
  it fires on no value, on `null`, AND on `false`. The boolean `false` is the
  only thing that check exists to catch, so the alert branch was unreachable
  from the day it shipped while `true` passed through intact and every run
  read healthy. Nothing on the server side was wrong: `/up`'s payload,
  `OversizedBackstopTests` and the runbook prose were all correct and green
  throughout, which is why it took runbook §8 drill 4 on the live CT to find
  it (2026-08-14). Two rules came out of it: never use `//` as an
  absence-test on a field whose meaningful value can be `false` or `0`, and
  give the unreadable case its own loud branch — a monitor that cannot read
  its input must say so, never fall through as "all clear". Pinned by
  `tests/shell/test_knapper_monitor.sh`, whose case 1 fails against the old
  expression.

## Runbook conventions

`ops/ct106-runbook.md` is checked by `ops/runbook-lint.sh` in CI: every fenced
`sh` block must parse, every bare `§N` must resolve to a heading in that file
(brief references are written `brief §N`), every `<placeholder>` must be in the
script's declared list, no `pct exec` command line may carry an unwrapped glob,
`<smoke-hostname>` may not appear at or after §9, and the smoke unit must keep
pointing away from `/vault`. Adding a placeholder means adding it there too —
that is the cost of the list being something a deployment can key on. What the
lint cannot check is whether a procedure is correct or in the right order; six
review rounds' worth of those findings are in git history.

The last two checks were added after the first real §5 install and §10 upgrade
(2026-08-13), and both encode the same lesson the identity table already
states in prose — most of what goes wrong late in that document is a reference
resolving to the wrong member of a pair, or to a shell that is not there:

- **`pct exec` runs the command with NO shell in the container.** A glob is
  expanded by the CALLING shell against the Proxmox HOST's filesystem, so
  `pct exec 106 -- ls /opt/knapper/*.tar.gz` reports "No such file or
  directory" identically whether the artifact exists or not — and the natural
  reading is the opposite of the truth. Wrap it: `pct exec … -- sh -c '…'`.
  Pipes and redirects are *not* flagged; those legitimately belong to the host
  side.
- **`<smoke-hostname>` dies at §9.** The smoke instance, its tunnel route and
  its Access app are all torn down there, so a §10 command aimed at that name
  fails at connect — which reads like the upgrade having broken the service.
  §10 targets the production hostname.

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

**A file that lands in `/etc` needs the third gate too.** `ops/check-installed.sh`
is the deploy-time twin: it runs inside the CT from the unpacked artifact and
reports every shipped unit and drop-in as identical / DIFFERS / NOT INSTALLED,
exit 0/1/2. The two gates cover the two halves of one bug — a hand-maintained
list that must agree with the set of files that ship, with nothing enforcing it.
The build-time half closed in v0.2.1; the deploy-time half was still open when
§10 was first run for real (four of six shipped units were named in the
runbook's diff list, and the two omissions happened to be identical in that
release). `check-installed.sh` DERIVES its set from the artifact, so a new unit
needs nothing added to it — that is the design, and adding an enumeration back
is the thing not to do. Tested in the shell tier
(`tests/shell/test_check_installed.sh`), which asserts the derivation
specifically.

Deliberately, it reports and never copies: reconciling a diff means merging
this deployment's edits (AllowedHosts, the Access AUD, the two `Sync__` knobs)
with the release's, and `knapper.service` therefore DIFFERS forever and
legitimately. Exit 1 means "a human decides"; exit 2 — something shipped that
was never installed — is the one that is never expected.

Each DIFFERS carries a plain-language legend ABOVE the diff, because a correctly
oriented diff is still misread: `-`/`+` reads as removed/added to anyone not
parsing the file headers, and the expensive misreading is the mirror of the one
the state exists to catch — `+Environment=Mcp__AllowedHosts__0=mcp.example.com`
read as "the release wants this, apply it", which reverts the deployment's own
hostname to the shipped placeholder. So the report says which side is running,
which side is shipped, that a `+` is not an instruction, and how many differing
lines are known site config versus the release's actual change. `SITE_KEYS` in
the script drives that classification and is **allowed to be incomplete**: an
unknown key lands in "OUTSIDE known site config — read this", so incompleteness
costs attention, never safety. Never add a rule that moves a line the other way.

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
- **A config knob for `OversizedFiles.DefaultBudget`** (5s). Deliberately NOT
  built yet: a budget expiry degrades `/health` to 503, and unlike an
  unreadable directory an operator cannot clear it without a code change —
  so if one is ever observed, the knob is the escape hatch to add. It is not
  speculative to leave out, because the condition is now self-announcing:
  `oversized.scanError` carries a `timeout:` prefix and the walk logs a
  warning saying it does not clear on its own. Helios is ~250 files against
  a 5s budget, so the headroom is enormous; the reason this is written down
  is that the headroom is not the argument.
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
- **Data Protection's three startup warnings** (observed CT 106,
  2026-08-13): ASP.NET Core finds nowhere to persist a key ring under
  `ProtectHome=true` with no user profile, and logs in-memory repository /
  ephemeral keys / no XML encryptor on every start. Harmless here — no
  cookies, no sessions, and Access validation uses fetched public keys — but
  this deployment holds a **zero-warn baseline as policy**, and permanent
  benign warnings are what train an operator to stop reading the log.

  The three are `EventId` **50** (`…Repositories.EphemeralXmlRepository`,
  "Using an in-memory repository"), **59** and **35**
  (`…KeyManagement.XmlKeyManager`, "Neither user profile nor HKLM registry
  available" and "No XML encryptor configured") — measured 2026-08-13 by
  forcing the no-key-ring path, not read off a doc page. Note `env -u HOME`
  does NOT reproduce it: .NET falls back to the passwd entry, so the probe
  needs `HOME` pointed at an unwritable directory.

  Three options, and the cheapest-looking one is worse than it appears:

  1. **Persist the key ring** to `/var/lib/knapper` (already in the unit's
     `ReadWritePaths`). Silences 50 and 59 permanently; 35 fires once when
     the first key is created, then never again. Cost: a real key ring on
     disk for a feature nothing here uses.
  2. **Drop the whole `Microsoft.AspNetCore.DataProtection` category** to
     Error. One line, but 59 and 35 share `XmlKeyManager` with genuine
     warnings (key-element parse failures, decryption errors), so this
     really does blind us — the objection stands.
  3. **Filter exactly those three EventIds.** Correct in principle, but
     `ILoggingBuilder.AddFilter` cannot do it: filters key on
     (provider, category, level) and have no access to the `EventId`. It
     needs a custom `ILoggerProvider` decorator wrapping every registered
     provider — permanent machinery in the log path, which is more than
     either alternative costs.

  **Dan's call.** On the measurements, (1) is now the recommendation: it is
  the only one that removes the cause rather than hiding the symptom, and
  its downside is a file, not a blind spot. The objection to it — "that puts
  real keys on disk for a feature nothing uses" — is weak in exactly
  proportion to the "nothing uses": keys protecting nothing have no exposure
  value, and the missing XML encryptor (EventId 35) is therefore not an
  at-rest question today.

  ⚠️ **That argument expires the moment Data Protection gains a real
  consumer** — antiforgery tokens, cookie auth, `IDataProtector` called from
  anywhere in this codebase. The keys stop being inert on that day, the absent
  encryptor becomes a genuine at-rest question, and nothing about the key ring
  itself will have changed to announce it. So whoever makes that true owns
  re-opening this, and the trigger belongs in the same commit as the feature.
  This is the one entry here whose reasoning depends on a fact about the rest
  of the system rather than on a measurement.

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
- **Files Helios has that CT 106 does not** — OPEN, and the most serious
  thing on this list. Measured 2026-08-13: Obsidian Sync's ~5MB per-file
  ceiling is SYMMETRIC. A note created on one of Dan's Macs that exceeds it
  never downloads to the CT, so the vault Knapper serves is a strict subset
  of Helios and nothing local says so. `vault_read` answers `[NotFound]` for
  a note that plainly exists; worse, searches report `truncated: false`,
  which the query contract defines as "this scope was exhaustively
  searched". That is the one known way the completeness envelope lies, and
  it lies quietly.

  `Sync__MaxFileBytes` does not help — it guards Knapper's writes.
  `OversizedFiles.Scan` does not help — it finds oversized files that are
  PRESENT, and this one is absent. Detection needs evidence the filesystem
  does not carry.

  The only local candidate is ob's `sync.log`: the upload side logs
  `File too large to sync (… max 5.00 MB)` with the filename, so if the
  download side logs the same, the CT knows exactly which notes it could not
  fetch and a check could name them. **Unmeasured — measure this before
  designing anything.** If it logs nothing, the options get materially
  worse: a manifest diff against a Mac, or accepting and documenting that
  "exhaustive" means "exhaustive over what Sync delivered", which weakens a
  contract the whole query layer is built on.

  Whatever the answer, it belongs in the QUERY layer, not beside the
  mutation guard. Do not bolt it onto the oversized backstop: that scanner
  answers a different question and pairing them would make a partial answer
  look complete.

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
