# Repository guidance for coding agents

Read by Claude Code as `CLAUDE.md` and by other agents as `AGENTS.md` — two
byte-identical files, because some agent tooling refuses to read symlinked
instructions (see "Editing this file" at the bottom). Stays focused on
invariants whose violation is **silent** (silent data corruption, silent
contract break). Loud failures don't earn a spot.

## What this is

**Knapper** — an always-on MCP server that is the single authoritative
read/write interface to Dan's Obsidian vault ("Helios") for every AI agent.
Humans edit via Obsidian apps + Obsidian Sync; agents go only through this
service, which turns distributed agent concurrency into one server-side
transaction problem.

## Source of truth

- **`obsidian-mcp-implementation-brief.md`** is the requirements document:
  read/query contract (§6), mutation contract (§7), enforcement rules (§8),
  hard prohibitions (§15). Decisions marked "made" there are not relitigated
  here — with two sanctioned deviations: this is a from-scratch .NET build
  (not a fork of the Python `obsidian-web-mcp`), and ingress is
  Cloudflare Access (the brief's B2) first, not server-native OAuth (B1).
  Both confirmed by Dan 2026-08-08.
- **`vault-edit.reference.py`** is the semantic reference for the mutation
  contract: lock → fresh read → SHA precondition → anchored edits → guards →
  hidden temp + fsync → atomic commit → reopen and verify. Core ports these
  semantics; when in doubt about edge-case behavior, that file is the answer.
- Style/architecture reference: [Mailvec](https://github.com/djdelaney/Mailvec)
  (Dan's other MCP server). Conventions carried over: CPM, warnings-as-errors,
  locked tool surface, typed errors, `/health` vs `/up` split, silent-invariant
  CLAUDE.md.

Developer docs (read on demand; this file stays invariants-only):

- [`docs/architecture.md`](docs/architecture.md) — project/library structure,
  the two layers, the locking model, gates, security model.
- [`docs/usage.md`](docs/usage.md) — running it, the full configuration
  reference, the 13-tool surface with the agent write loop, error-code table,
  health/monitoring.
- [`docs/extending.md`](docs/extending.md) — how to add a tool / query
  capability / mutation / error code / config knob without breaking the
  contracts; testing and build conventions; scoped-but-unbuilt ideas; the
  OPEN DECISIONS list (and the closed ones — check it before re-litigating
  anything a review already settled).
- [`ops/ct106-runbook.md`](ops/ct106-runbook.md) — production deployment.
  Runbooks describe how to VERIFY live state, never what it was (house rule):
  date and mark anything observed.

## Common commands

```sh
dotnet build Knapper.slnx
dotnet test Knapper.slnx                            # includes the black-box acceptance tier
dotnet test tests/Knapper.AcceptanceTests           # REAL server processes over real HTTP (brief §13)
dotnet test tests/Knapper.Core.Tests --filter "FullyQualifiedName~CrossProcessLockTests"
dotnet run --project src/Knapper.Cli -- doctor      # config/dependency checks (env: Vault__RootPath etc.)
ops/release.sh --patch --ship                       # bump + commit + tag on green CI (see below)
ops/publish.sh                                      # linux-x64 tarball for CT 106
```

Deployment: `ops/ct106-runbook.md` (condensed from the brief; the brief's
§11 corrections are mandatory reading before building the CT).

## Cutting a release

Land the work first — `ops/release.sh` bumps a version, it does not ship
code. Then, in order:

```sh
ops/release.sh --minor --ship   # bump <Version> → commit → push main →
                                # wait for CI on THAT commit → tag v0.2.0 only if green
git checkout v0.2.0
git status --porcelain          # MUST print nothing before publishing
ops/publish.sh                  # → artifacts/knapper-0.2.0+g<sha>-linux-x64.tar.gz
```

Then runbook §10: snapshot, record the running version, install, and prove the
restart took with `knapper verify --url … --expect-version 0.2.0`.

- `--patch` is the default. `--minor` for anything client-facing — a tool name
  or shape, a new error code, a config knob deployments must set. Tool names
  are a client contract; a rename is a version bump, not a refactor.
- Without `--ship` it bumps and commits only, then prints the `git tag`/`git
  push` commands to run once CI is green. Never tag a commit CI has not passed.
- It refuses to run with uncommitted edits to `Directory.Build.props` (so the
  bump commit carries nothing else), refuses a bump whose tag already exists,
  and `--ship` refuses to run off main.
- **Publish from the clean tagged tree.** A dirty tree stamps the artifact and
  the running service `.dirty`, and `--expect-version 0.2.0` refuses it — a
  build carrying uncommitted edits cannot be reproduced from the tag, so the
  tag stops describing production. Full rationale: `docs/extending.md`.

`Knapper.slnx` (not `.sln`) is the solution file — .NET 10 emits the new XML
format by default.

## Build conventions

- **Central Package Management is on.** All NuGet versions live in
  `Directory.Packages.props`; csproj files use `<PackageReference>` without
  `Version=`. Adding a dependency means editing both files.
- **`TreatWarningsAsErrors=true`** in `Directory.Build.props`.
- **Unix-only by design** (`SupportedOSPlatform` linux+macos, asserted
  repo-wide in `Directory.Build.props`): the mutation contract stands on
  flock(2), link(2), rename(2), and Unix file modes. Don't add Windows guards
  or a Windows code path.
- **`<Version>` in `Directory.Build.props` is the ONE version carrier, and
  `Knapper.Core.BuildInfo` is the ONE read of it.** Bump it with
  `ops/release.sh`, never by hand. Everything downstream is derived:
  `ops/version.sh` appends the git revision, the `KnapperStampRevision` target
  stamps `AssemblyInformationalVersion`, and `BuildInfo` feeds
  `serverInfo.version`, `/health`, `/up`, `knapper version` and the artifact
  filename. Three ways to break this silently, all of which still produce a
  version-shaped string: reading `Assembly.GetName().Version` (that is
  `AssemblyVersion` — four numeric parts, so the revision and any prerelease
  suffix vanish and an off-tag build reports itself as the release);
  `GetEntryAssembly()` (the test host, under `dotnet test`); or adding a
  second carrier for the two to disagree about. The `.dirty` suffix is
  load-bearing — it is the only thing distinguishing a build off uncommitted
  edits from the tagged release, and `knapper verify --expect-version` refuses
  it. Pinned by `VersionSurfaceTests` and
  `HealthAndGuardTests.Every_surface_reports_the_same_build`.
- **A case-SENSITIVE vault filesystem is a hard production requirement.**
  Per-path lock identity is SHA-256 of the path STRING; batch duplicate
  rejection, move same-path checks, and search prefixes are string compares —
  on a case-insensitive FS (macOS dev default) two spellings alias one file
  and per-path serialization silently voids. `knapper doctor` FAILS on it;
  the server startup only warns (dev vaults are fixtures). Do NOT "fix" this
  by case-folding: ext4 legitimately hosts names differing only by case, and
  folding would falsely reject valid batches in production.

## Core invariants (silent-corruption-prone)

- **`AtomicFile` is the only code that writes vault bytes.** Hidden
  same-directory temp (`.knapper-tmp-`) → fsync → last-instant SHA re-check →
  rename/hard-link → directory fsync, temps cleaned on every failure path.
  A write path that bypasses it loses atomicity, mode preservation, or the
  no-clobber guarantee without any test necessarily noticing. The
  `KNAPPER_FAULT_SHORT_WRITE` env hook inside it is a fault INJECTOR for the
  acceptance suite (it can only break a write so `VerifyOnDisk` must catch
  it) — it is not, and must never become, a way to land unverified bytes.
  ONE sanctioned exception: `CaseSensitivityProbe` creates+deletes a
  zero-byte temp-prefixed probe in the vault root — raw create/delete IS
  its measurement, so it cannot go through AtomicFile. Nothing else may
  join it.
- **`VaultPathResolver` is the only gate between agent-supplied path strings
  and the filesystem.** Nothing else may combine user input with the vault
  root. `VaultPath`'s constructor is internal so an API taking one is stating
  validation already happened — don't add public construction.
- **Lock ordering is global-shared → per-path-exclusive, always**, and the
  commit job takes only the global lock exclusively — that's what makes
  deadlock structurally impossible. A new lock kind must slot into this
  hierarchy, not beside it.
- **Lock files are opened via raw `open(2)`/`creat(2)` interop
  (`Posix.OpenLockFile`), never `FileStream`.** The .NET Unix runtime takes
  its own flock locks during FileStream open, which contend with a real
  lock holder and turn "wait for the lock" into an IOException at open time
  (`FileShare.ReadWrite | Delete` does NOT disable it — measured here). And
  never reintroduce a 3-arg `open(2)` P/Invoke: its variadic mode parameter
  mis-passed on Apple's arm64 ABI here, creating lock files with garbage
  permissions. `creat(2)` is the non-variadic create.
- **flock releases on process death — that's why it was chosen** over lock
  files whose stale presence outlives a crash. Pinned by
  `CrossProcessLockTests.A_dead_lock_holder_does_not_wedge_the_vault`.
  Don't "improve" the design with PID files or lock-file existence checks.
- **Cross-process lock behavior is tested with real second processes**
  (`Knapper.LockProbe` spawned via `dotnet exec`). The brief forbids trusting
  the lock design on in-process tests alone; keep it that way when extending.
- **Verification is by content, never by receipt.** Every mutation ends in
  `AtomicFile.VerifyOnDisk` (reopen + byte-compare). This vault has a
  documented history of writes that reported success without landing.

## MCP-layer invariants (silent-corruption-prone)

- **`ToolSurface.Resolve` must stay typed `IEnumerable<Type>`.** The SDK has
  both `WithTools(IEnumerable<Type>)` and a generic
  `WithTools<TToolType>(TToolType singleToolInstance)`; for an argument
  statically typed `IReadOnlyList<Type>` C# picks the GENERIC overload
  (identity beats implicit conversion), which registers the list itself as
  one tool object with zero `[McpServerTool]` methods — and the server
  silently exposes NO tools while every test that doesn't hit the wire stays
  green. Happened here; the wire tests are what caught it.
- **Every tool attribute sets `UseStructuredContent = true`.** The SDK's
  default puts results only in text content; structured content is the
  client contract the tests pin. A new tool that forgets the flag ships
  differently-shaped responses without any compile-time complaint.
- **Every tool method returns a CONCRETE type, and the published manifest is
  checked at the wire.** The declared return type is what becomes
  `outputSchema`; an `object` return publishes the permissive `true`, which
  is legal draft 2020-12 and useless — strict clients (Claude Code) reject
  the tool list and discard ALL THIRTEEN tools, while the server logs
  nothing and every test that merely CALLS a tool stays green. Shipped in
  0.3.2 on `vault_search` (three result shapes, spelled as `object`); it
  blocked the CT 106 cutover. A multi-shape result expresses its union in
  the DATA — one record with optional members (`SearchResultItem`) — never
  in the signature. Two traps in the check itself: the SDK client does NOT
  hand back what the server sent (for a scalar return the wire carries the
  wrapped `{"properties":{"result":…}}` while `ProtocolTool.OutputSchema`
  reports the unwrapped inner schema), so schema checks read RAW JSON-RPC
  (`RawMcp`, and `verify`'s own raw tools/list) — a check through the client
  inspects a document no client receives; and `additionalProperties: false`
  is a legal boolean in a non-subschema position, so the walk must not
  flag it. `ToolSchemaContract` is the ONE definition, shared by
  `ToolManifestTests` and `knapper verify` for the same reason `ToolNames`
  is shared: a build gate and a deployment gate that disagree about what a
  loadable manifest is are worse than one gate.
- **A response must carry every property its own schema marks `required`.**
  The SDK's serializer omits nulls while the schema exporter marks every
  member without a C# default `required` — opposite defaults, so a null
  `nextCursor` silently vanished from responses that still advertised it,
  and any client validating structured content (the spec says clients
  SHOULD) rejects a correct answer as malformed. `ToolSerialization` writes
  nulls to close it; a member may be omitted only when it is genuinely
  optional, which means it carries a C# default AND
  `[JsonIgnore(Condition = WhenWritingNull)]`. The SDK's own client does
  not validate, so nothing else here would ever notice
  (`ToolResponseConformanceTests` compares every tool's real response
  against its published schema — schema and payload read at the same
  layer, both off the wire).
- **Tool errors lead with the bracketed code** (`[PreconditionFailed] …`) via
  `ToolSupport.Run` — agents parse that prefix to decide "re-read and
  rebuild" vs "give up". New tools go through `Run`; a bare exception would
  reach clients as an unstructured message with no code.
- **The locked tool table (`ToolSurface.All`), the `[McpServerTool(Name=…)]`
  attributes, and `Knapper.Core.ToolNames.All` are held in lockstep by
  tests.** Tool names are a client-facing contract; renames are version
  bumps, not refactors. There is no unconditional-write tool in the table
  and never will be. The third list exists because `knapper verify --url`
  asserts a DEPLOYED server's surface against it from another assembly — if
  it drifted, the one check standing between a partially-registered server
  and production would assert the wrong contract, in the green.
- **`knapper verify` is READ-ONLY and must stay read-only.** The runbook
  points it at the live service, where the vault IS Helios over Obsidian
  Sync — a write there lands real notes on Dan's devices. Its one mutation
  call targets a path that cannot exist, so it dies at the fresh read
  before any temp file; `VerifyCommandTests` byte-compares the whole vault
  across a run. Write-side races belong to the runbook's §8b
  disposable-vault session, never here.
- **`knapper verify`'s ingress probes never follow redirects, and assert a
  NAMED refusal status rather than "not 200".** A refusal has two shapes and
  which one arrives is set by the Access application's POLICY TYPE, not by
  the caller: an identity policy sends an unauthenticated caller to log in
  (302 → `…cloudflareaccess.com/cdn-cgi/access/login/…` → **200 HTML**),
  while a Service-Auth-only application refuses flat (403). Follow the
  redirect and every refusal probe reads the login page's 200 as the vault
  surface answering — shipped here, and on 2026-08-14 it called CT 106
  EXPOSED and a tunnel came down over it. It survived because the flat-403
  application has nothing to follow, so the twin check covering the same
  property passed. The inverse is just as bad and quieter: a probe whose
  pass condition is anything-but-200 passes on a 500, a misrouted tunnel, or
  DNS failure. Both halves are pinned by
  `VerifyCommandTests.A_deployment_behind_two_Access_applications_passes_every_check`
  (a fake Access edge, both policy types) and its exposed-surface twin.
- **The Access loopback exemption requires loopback peer AND loopback
  Host** (`HostGuard.IsLocalRequest` — the one definition, used by both the
  audience handler and /health's filter). Production is
  `cloudflared → 127.0.0.1`, so every tunneled internet request arrives
  from a loopback PEER; a peer-only check silently disables origin
  validation for the whole public surface (shipped here once, caught in
  review 2026-08-09; `AccessTopologyTests` pins the topology). Never point
  the tunnel's `httpHostHeader` at a loopback name.
- **Startup fail-closed checks live in Program.cs singleton factories** and
  are forced at boot (`GetRequiredService` before Run): missing vault root /
  lock dir / audit path, lock dir or audit path INSIDE the vault — all
  refuse startup rather than surfacing on the first tool call. Access
  misconfiguration refuses startup; signing keys are fetched at boot (lazy
  retrieval fails into an EventSource nobody hears while the server 401s
  everyone — Mailvec shipped that once).
- **/health is loopback-only and detailed; /up is boolean-only.** /up's body
  discloses no paths, no conflict filenames, no generation counter — a test
  pins the exact property set. The status-code parity between the two is
  what monitors alert on.

## Mutation-layer invariants (silent-corruption-prone)

- **`VaultMutationService` is the only mutation surface, and every operation
  on an existing file demands `expect_sha256`.** There is no unconditional
  write anywhere in the codebase and none may ever be added — not even as an
  internal helper "for tests" (a safe wrapper BESIDE an unsafe original is an
  exposed bypass; brief §7 forbids exactly that).
- **The critical section order is fixed**: lock → fresh read → SHA check →
  transform → validate guards → hidden temp + fsync → final SHA check →
  atomic replace → reopen and byte-compare → unlock. `Mutate()` embodies it;
  new operations go THROUGH it or replicate it exactly (move/delete do).
- **Move and delete never remove a pathname another writer could own.** The
  only names they delete are their own hidden temps, under fresh GUIDs no
  agent can address and no other writer can know; everything public is either
  linked (no-clobber — the kernel refuses rather than replacing) or captured
  by `rename(2)` and examined afterwards. A check-then-`unlink` cannot be made
  safe: every check has expired by the next syscall and POSIX has no
  inode-conditional `unlink(2)`, so the version that re-verified the source
  and then deleted it destroyed an external writer's replacement while
  reporting SUCCESS (`SourceCaptureRaceTests`). `LinkPublishCapture` is the
  ONE implementation, shared by both operations.
- **A PUBLIC pathname holds the content at every instant, so the destination
  is published BEFORE the source is captured.** This is a crash property, not
  a race property, and it is the reason the order cannot be flipped back: an
  ordering that captured first put an fsynced `rename` between the source's
  disappearance and the destination's creation, so a kill -9, an OOM, or
  systemd restarting the unit mid-deploy left the note reachable only through
  `.knapper-tmp-*` names — gitignored, skipped by every walk, unaddressable
  through the resolver — while Sync propagated the visible deletion to every
  device. Pinned by `CrashDurabilityTests`, which kills REAL processes at each
  boundary, because a try/finally proves nothing about a machine that lost
  power. The corollary is what makes crash residue safe to reason about: it is
  always a hidden DUPLICATE of content that is also at a normal pathname,
  never the only copy — so no journal, no startup recovery pass, and no
  sweeper is needed.
- **A published destination is never retracted.** If the source turns out to
  have been replaced in the window before the capture, the operation fails
  with the destination still there — a visible duplicate of the previous
  content, named in the error and audited. That is the deliberate price of
  publishing first: retracting a pathname other writers can already see means
  deleting something that may not be ours, which is the defect this design
  exists to prevent. `RequireStillOurBytes` survives ONLY as a courtesy check
  that keeps the duplicate rare — nothing destructive may ever be gated on it
  again.
- **Containment is proved on BOTH sides of the commit.** The pre-commit proof
  describes the directory as it was one syscall ago; a directory can be moved
  out of the vault and replaced with a symlink in between, and then the commit
  and its verification both succeed through the symlink — a delete reported
  success with the note outside the vault (review, 2026-08-19). The
  post-commit `RealPath` check catches it while the source is still untouched,
  which is the whole point of it landing before the capture. It does NOT
  remove the escaped link: unlinking a path on the strength of having created
  it a syscall earlier is the exact shape banned above, and descriptor-relative
  `linkat` would not have prevented this either — a directory MOVED out of the
  vault is followed by any handle to it (`PostCommitFailureTests`).
- **Byte equality is not ownership and not continued existence.** An earlier
  fix decided rollback by comparing content and pinned a test asserting a
  byte-identical replacement gets deleted "because no distinct content is
  lost". Wrong twice: the pathname belongs to whoever created it, and after an
  external rename that pathname can be the only place the note still exists.
  Nothing may delete a publicly-named file on the strength of a content
  comparison. Content comparison decides only whether one of Knapper's OWN
  temps may go, and only ever in the KEEP direction when uncertain
  (`HiddenLinkIsTheLastCopy`): unreadable answers "keep".
- **Every exception at the post-commit verification is caught, not just
  `IOException`.** `File.ReadAllBytes` answers `UnauthorizedAccessException`
  when the destination has become a directory or an unreadable file — not an
  `IOException`, so it escaped a handler that listed only
  `KnapperException or IOException`, skipped the recovery block, and took the
  last links to the original out with it through `finally` while the caller
  saw a typed `IoError` (review, 2026-08-19). A handler at a boundary where
  the wrong exception type costs a note must be exhaustive by construction,
  not by enumeration.
- **The `.trash/` chain is held to the resolver's symlink rule, and the link
  is proved to have landed inside the vault.** Soft delete is the one place
  Knapper builds a vault path itself — `.trash/` + an already-validated
  relative path — because `.trash` is deliberately unaddressable and can
  never come back out of `Resolve`. That left the whole chain unchecked: a
  symlink at `.trash` or any directory under it sends `link(2)` outside the
  vault, so the note leaves the vault, leaves git, leaves every backup, and
  the receipt still says `.trash/...`. The chain is walked with
  `VaultPathResolver`'s own rule (ONE definition — `RejectSymlinkComponents`)
  before AND after `Directory.CreateDirectory`, which follows an existing
  directory symlink. Both checks are TOCTOU against the link that follows,
  which is why the post-link `RealPath` containment check exists: it cannot
  stop a link from briefly escaping, but it catches the escape before the
  source is captured, and that is the part that matters (`TrashChainTests`).
  Do not "simplify" this by routing `.trash` through the public resolver — it
  stays unaddressable.
- **The conflict and sync gates are asserted TWICE: before the locks, and
  again with them held.** A mutation can wait up to `Vault:LockTimeoutMs`
  for a lock and a batch adds its whole validate phase, so a pre-lock answer
  can be that stale by the time it is acted on. This NARROWS a window rather
  than closing one — the locks bind cooperating Knapper processes only, so
  Sync can materialize a conflict sibling the instant after any check — and
  it must be described that way; a fail-closed claim it cannot support is
  worse than the window. Batch re-asserts after VALIDATE, the last point at
  which nothing has been written (`GateRecheckTests`).
- **Batch validates EVERYTHING under the locks before the first write.** A
  bad hash/anchor/guard anywhere fails the whole batch untouched. The apply
  phase is not cross-file atomic (documented; git history is recovery), and
  duplicate paths are rejected because flock is per-descriptor — a second
  acquisition of the same path would self-deadlock.
- **Every write path checks `Sync__MaxFileBytes` against the POST-TRANSFORM
  bytes** (`RequireSyncable`, from `Mutate`, `Create`, and each batch plan).
  Obsidian Sync silently refuses any file over its per-file ceiling: it logs
  the rejection and prints "Fully synced" in the SAME millisecond, so an
  oversized note verifies on disk, commits to git, returns a success receipt,
  leaves every health signal green — and reaches no device. Content
  verification is structurally blind to it, because nothing local is wrong.
  Two silent ways to break it: check the INPUT size instead (the real case is
  a small anchored insert into a note already near the ceiling), or "tidy"
  the 5,000,000 default up to 5*1024*1024 — `ob` reports an ambiguous
  "max 5.00 MB" that nobody has bisected, and the errors are not symmetric:
  too low refuses writes loudly, too high strands them silently. Batch checks
  during VALIDATE, so an oversized item fails the batch untouched. Pinned by
  `SyncSizeLimitTests`. `/health` and `knapper doctor` are the backstop for
  oversized files PRESENT on the box (a shell wrote one, or it predates the
  guard), and they share ONE scanner (`OversizedFiles.Scan`) so they cannot
  drift into disagreeing; oversized files FOUND never degrade `/up` to 503 —
  nothing is blocked, and a permanent alert nobody can clear is how a monitor
  gets ignored (`OversizedBackstopTests`).
- **The size ceiling is SYMMETRIC, and the download half is an OPEN hole.**
  A >5MB file created elsewhere never reaches CT 106 at all — ABSENT, not
  oversized-and-present — so the scanner cannot see it and `truncated:
  false` can claim exhaustiveness over a vault silently missing a note.
  The one known way the completeness envelope lies; the oversized backstop
  does NOT cover it. Detail: `docs/extending.md`.
- **A vault walk that could not COMPLETE is a third state, and every health
  surface must carry it.** `OversizedFiles.Scan` throws — unreadable
  directory, or the `DefaultBudget` wall clock expiring — rather than
  returning the short list, so "could not tell" can never arrive looking like
  "scanned, none found". `/health` reports the incomplete state
  (`oversized.scanned` / `vault.conflictScanComplete`, plus a `scanError`
  labelled `io:` vs `timeout:` — the causes need opposite responses);
  `/up`'s `ok` booleans mean *probed and fine*, and unknown DEGRADES on BOTH
  walks. The line is FINDING vs BROKEN INSTRUMENT: a finding is information
  about the vault (conflicts block writes → 503; oversized files block
  nothing → 200, the monitor reads the body), a failed walk is no
  measurement at all — it persists exactly as long as a conflict file does,
  and alert fatigue is the monitor cadence rules' job, not the status
  code's. Never cache the unknown, and never let a walk failure reach the
  endpoint as an exception (both shipped here once).
- **Every vault walk filters `FileAttributes.ReparsePoint`.** Symlinks are
  rejected everywhere else (resolver, lock manager) and skipped by every
  lister; the oversized scan was the one exception, so a directory-symlink
  cycle made it non-terminating — on the request path of `/health` and `/up`,
  which the host monitor polls every 5 minutes. A new walk gets the filter AND
  a wall-clock bound whose expiry degrades health rather than hanging it (the
  same contract as `HealthService.RipgrepTimeoutMs`) — both vault walks on
  that request path now have one (`OversizedFiles.Scan`,
  `ConflictDetector.ScanAll`), and the conflict walk is UNCACHED on every
  poll, so its budget is what stands between a slow tree and a hung /up.
  A budget added without teaching the caller to catch its `TimeoutException`
  turns a bounded walk into a 500, which is the contract it was added to
  protect. Dot-entries stay skipped at every depth, which is also why
  `.trash/` is invisible: deletes are soft,
  so an over-ceiling file deleted through `vault_delete` still sits there, and
  alerting on it would be an alert about a file the human already dealt with
  and cannot clear through any tool.
- **Multi-path locks acquire in sorted order** (`AcquirePathLocks`), global
  shared first — the fixed order is the deadlock-freedom proof. New lock
  users slot into this hierarchy.
- **Rejections are audited, not just successes** — from the resolver gate
  onward: a path that never resolves to a vault path (`InvalidPath`,
  `BannedPath`) is refused before the audited region, deliberately — there
  is no vault object for the entry to be about. A stale-write rejection is
  signal (someone raced, or an agent is retrying a stale base). Audit writes
  are fsynced and live OUTSIDE the vault; vault content must never reach the
  audit path — which is why the audit `Detail` field never carries an
  exception message: anchor/guard failure text IS note content (the error
  CODE is the audit signal; rich diagnostics stay on the MCP response).
- **Conflict gate: agents never resolve Sync conflict files.** A
  `* (Conflicted copy ...)*` sibling blocks mutations to both the original
  and the sibling until a human reconciles. The sync gate (`ISyncGate`)
  fails mutations closed when continuous sync is unhealthy — no local
  fallback, ever.
- **The heartbeat TICK is a term in the fail-closed budget, so
  `knapper-heartbeat.timer` pins `AccuracySec=1s`.** Withholding the touch is
  the only way Knapper learns sync is unhealthy, so total exposure ≈ ob's
  detection latency + the inter-tick gap + `Sync__MaxAgeSeconds` (300s,
  sized against a 60s tick). systemd's DEFAULT accuracy is 1min — on a 60s
  period that silently nearly halves the margin (measured 116s gaps on CT
  106, presenting as a two-minute blip blocking every mutation and reading
  like a Knapper bug). Dropping `AccuracySec=1s`, or changing the period
  without moving `MaxAgeSeconds`, moves the budget silently. And
  `ops/sync-heartbeat.sh` LOGS every withheld touch in our own words (never
  text lifted from ob's log, which interleaves vault filenames): `journalctl
  -u knapper-heartbeat | grep withheld` is the deployment's only durable
  record of exposure — ob's log prints `Disconnected from server` on clean
  shutdown too, so it cannot answer it.
- **`GitCommitJob` is the vault's only committer, and it snapshots under
  the vault-wide commit lock** — so a prepared-but-unverified batch write
  can never be captured. Local-only repo; NO remote until the credential
  sweep closes (brief §10), and the staged-content secret scan
  (`SecretScanner`) refuses commits containing credential-shaped strings —
  scanning the STAGED blob (`git show :file`), not the working tree, because
  the staged bytes are what would enter history. Findings are masked in the
  error; never echo a whole secret into logs or exceptions.

## Query-layer invariants (silent-corruption-prone)

- **`RipgrepRunner.BaselineArgs` are load-bearing, every one.** `--no-config`
  (a user config adding `--hidden` silently changes the visibility contract),
  `--no-ignore` (vault CONTENT must not steer search — a synced note shipping
  a `.rgignore`/`.gitignore` would hide files from scopes that claim
  "exhaustively searched"), `--no-follow` (symlinks are rejected everywhere),
  `--sort=path` (deterministic page order; single-threaded is the point, the
  vault is small by design). Structured `ArgumentList` only — never a shell,
  never string-concatenated commands.
- **Hidden means invisible on BOTH surfaces.** The native lister skips
  dot-entries at every depth; rg's default does the same during searches.
  `VaultFileListerTests.Agrees_with_ripgrep_about_what_exists` is the
  differential test holding the two implementations together — adding
  `--hidden` to one side or relaxing the lister's filter breaks the
  equivalence silently.
- **The completeness envelope never guesses.** `truncated: false` claims the
  scope was exhaustively searched; `total_matches` is null when not computed
  (rg's `begin` events fire only for files WITH matches — the honest
  scanned-files count comes from the end-of-stream `summary` stats, and a
  killed stream reports the lower bound, not an invention). Budget hits
  surface as `truncated` + cursor; a time budget that produced nothing is a
  typed `QueryTimeout`, never an empty "no match".
- **rg is always handed an explicit search path (`.`).** Given none, rg
  decides between recursing the working directory and reading STDIN by
  inspecting stdin — and a server under systemd has no terminal. The stdin
  branch returns zero matches over an empty stream and it is reported as an
  ordinary exhaustive "no match". Paths then come back `./note.md`;
  `NormalizeRgPath` strips the prefix, because a vault path is the identity
  behind cursor fingerprints, prefix scoping, and the lister differential.
  EVERY stream that parses rg output normalizes — matches, `-l`, and
  `--count-matches`. Only the match stream did until 0.3.3, so unscoped
  files/counts searches answered `./Notes/Daily.md`: a string no other
  surface agrees with, that `vault_read` refuses, and that rode inside
  `nextCursor` as the resume position. Prefixed searches hid it (rg echoes
  those verbatim) and so did every test, which all passed `pathPrefixes`.
- **ripgrep 15+ is part of the query contract, not a packaging choice.**
  rg 14 and earlier report `"searches": 0` in the JSON summary for a query
  that matched nothing, so `scanned_files` — the evidence that "no match"
  means exhaustively searched — collapses to zero while the envelope still
  claims `truncated: false`. Nothing errors. `knapper doctor` FAILS below 15
  (`RipgrepVersion`), the server WARNS at boot and keeps serving — the same
  gate/warn split as the case-sensitivity probe, for the same reason — CI
  pins a release build, and the runbook installs one: Debian's apt package
  is still 14.x. Both callers go through `RipgrepVersion.Read`, so doctor and
  startup can never disagree about what a usable ripgrep is.
- **Cursors are bound to their query** (fingerprint of the filter fields).
  Honoring a cursor against different filters would omit or duplicate
  records across pages — that's why the mismatch is a typed `InvalidCursor`,
  not a best-effort resume.
- **Ranged reads return the WHOLE file's SHA-256.** The hash is the mutation
  precondition currency; a range-scoped hash would let an agent build a
  precondition that can never match and, worse, look like it should.
- **The generation counter must move when knowledge is lost.** The
  filesystem watcher's Error event (buffer overflow = events dropped)
  increments — "unknown" is never reported as "unchanged". Control-dir
  events (.git/.obsidian/.trash, temp files) are filtered because queries
  cannot see those paths; without the filter every git commit and every
  workspace.json save Sync delivers would flip `changed_during_query`.
- **Frontmatter search reports what it could not examine.** Broken YAML and
  non-UTF-8 .md files land in `UnparseableFiles` — a skipped file could be
  hiding a match, and "no match" must mean the scope was exhaustively
  searched.

## Editing this file

`CLAUDE.md` and `AGENTS.md` must stay byte-identical. They are two REAL
files, deliberately not a symlink: some agent tooling refuses to read
symlinked instructions and silently gets no guidance at all. Do not
"tidy" the duplication away with `ln -s` — that breaks a tool in use here.

Edit `CLAUDE.md`, then mirror it:

```sh
cp CLAUDE.md AGENTS.md
```

Editing one and not the other is the silent failure this guards against:
each agent then reads plausible-looking invariants while neither notices
the other's differ. `AgentGuidanceTests` fails on any drift, and CI aborts
before it can merge.
