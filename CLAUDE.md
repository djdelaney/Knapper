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
  reference, the 14-tool surface with the agent write loop, error-code table,
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
  flock(2), link(2), linkat(2) with no-follow, rename(2), the atomic pathname
  exchange (renameat2 `RENAME_EXCHANGE` on Linux, renamex_np `RENAME_SWAP` on
  macOS — ext4 and APFS both support it), non-following stat (statx(2) on
  Linux — its layout is arch-independent, unlike struct stat; lstat(2) on
  macOS arm64, the layouts pinned by `PosixStatTests` on the running
  platform), and Unix file modes. Don't add
  Windows guards or a Windows code path, and never "handle" a filesystem
  without the exchange by falling back to an overwriting rename — that
  fallback IS the destroyed-external-write defect the exchange replaced.
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
  atomic exchange (replace) / hard-link (create) → directory fsync, temps
  cleaned on every failure path. A write path that bypasses it loses
  atomicity, mode preservation, or the no-clobber guarantee without any test
  necessarily noticing. The
  `KNAPPER_FAULT_SHORT_WRITE` env hook inside it is a fault INJECTOR for the
  acceptance suite (it can only break a write so `VerifyOnDisk` must catch
  it) — it is not, and must never become, a way to land unverified bytes.
  ONE sanctioned exception: `CaseSensitivityProbe` creates+deletes a
  zero-byte temp-prefixed probe in the vault root — raw create/delete IS
  its measurement, so it cannot go through AtomicFile. Nothing else may
  join it.
- **`Replace` commits by ATOMIC EXCHANGE, never by overwriting rename — and a
  raced commit is exchanged BACK.** The SHA re-check expires a syscall before
  the commit, and `File.Move(..., overwrite: true)` destroys whatever the
  target holds at THAT instant — an external replacement landing in the gap
  was silently lost while edit, append, and every non-create batch item
  reported success (the review P0 this closed). The exchange swaps the
  target's bytes to the hidden temp and judges them there — classified
  NON-FOLLOWING, then by BYTES against the EXPECTED BASE, never by metadata:
  an equal dev/inode/size/mtime tuple is NOT proof of unchanged content (an
  in-place writer preserves all four — mtime-faithful sync tooling restores
  mtime as a matter of course, and timestamp granularity aliases
  back-to-back writes unaided — while ctime, the one field that would
  notice, is bumped by the exchange itself, so NO stat tuple can ever carry
  this proof; a stamp fast path shipped here and silently destroyed a raced
  in-place write while reporting success — round four, and the reason
  `ReplaceCommitRaceTests` pins the restored-mtime overwrite). A hash match
  is the ordinary success; a displaced base that cannot be READ can no
  longer be proven to be the base, so it routes to the swap-back — a
  spurious but safe rejection over a net-unchanged file, never an unverified
  success. A hash mismatch means the commit raced an external write,
  and brief §7 is explicit that stale input rejects WITHOUT MUTATING, so the
  swap is undone and the external bytes return to the canonical pathname (a
  first remediation that kept them hidden while our stale bytes stayed
  canonical matched NEITHER serialization, and hidden means invisible to
  Sync, git, and every query — data loss wearing a failure receipt; review
  follow-up 2026-08-20). The instant the exchange lands, the temp's default
  flips to RETAIN: every branch — including exceptions nobody anticipated —
  keeps the displaced version unless a step positively proves it safe to
  discard (an inspection that threw used to ride the false-by-default flag
  into the cleanup and delete the only copy; round three). Only when a third
  write or delete lands between the two exchanges does the fallback engage:
  every surviving version is made VISIBLE — a `(Knapper displaced …)`
  conflict sibling, published NO-FOLLOW, that the conflict walk lists, the
  audit detail names, the generation counter reflects, and the conflict gate
  blocks on until a human reconciles. Hidden-only survival is not an
  acceptable success OR failure state. A target deleted externally in the
  gap stays deleted (the old rename silently resurrected it). The honest
  residual: a crash between the two exchanges leaves the raced state as
  crash residue with no receipt — a window one lstat plus one page-cached
  read-and-hash of the displaced bytes (bounded by `Sync__MaxFileBytes`)
  wide, plus the undo's syscalls when raced. It is deliberately NOT narrowed
  back to a metadata compare: the precondition that prevents silent loss
  outranks the window's width, and narrowing it needs durable recovery
  metadata, never a stat proxy for content. Closing
  it needs a journal and a startup pass this design deliberately lacks;
  `CrashDurabilityTests` DEMONSTRATES the state with a real killed process,
  and accepting it is the vault owner's recorded decision, not an
  implementation default. Pinned by `ReplaceCommitRaceTests`.
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
- **Nothing may reach a tool method as a parameter that is not a tool
  ARGUMENT.** The SDK documents `McpServer`, `RequestContext<T>` and
  `IServiceProvider` parameters as bound from the request and excluded from
  the generated schema — but the exclusion is CONDITIONAL (it consults
  `IServiceProviderIsService`), and when it does not fire the exporter walks
  the parameter's whole object graph and publishes it as required input.
  Measured here on SDK 2.1.0 with an `McpServer` parameter added for client
  attribution: `inputSchema.properties.server` appeared carrying the
  permissive `true` at four nested points, which is the 0.3.2 outage exactly
  — a manifest a strict client rejects WHOLE, taking all fourteen tools down
  over a parameter nobody meant to publish, while every test that merely
  CALLS a tool stays green. Request-scoped data reaches a tool body through a
  FILTER instead (`Program.cs`'s `AddCallToolFilter` → `CallingClient`), which
  is the only seam that is both per-request and invisible to the manifest.
  Two nearby traps: `HttpContext.RequestServices` does NOT resolve
  `McpServer` (it yields "unknown" on every line, indistinguishable from
  clients that decline to name themselves), and the value must never be
  cached on `ToolSupport` — that is a singleton shared by every concurrent
  session, and from protocol revision 2026-07-28 clientInfo is per-REQUEST,
  so a cached read misattributes every call after the first with no signal
  that it is wrong. Pinned by
  `ToolManifestTests.No_tool_advertises_the_request_scoped_server_as_an_argument`
  and `ClientAppLoggingTests`.
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
  NAMED refusal status rather than "not 200".** A refusal has three shapes
  and which one arrives is set by the Access application's CONFIGURATION,
  not by the caller: an identity policy sends an unauthenticated caller to
  log in (302 → `…cloudflareaccess.com/cdn-cgi/access/login/…` → **200
  HTML**); the SAME application with Managed OAuth on refuses
  machine-readably instead (401 + `WWW-Authenticate: Bearer …,
  resource_metadata="…"`); a Service-Auth-only application refuses flat
  (403). Follow the redirect and every refusal probe reads the login page's
  200 as the vault surface answering — shipped here, and on 2026-08-14 it called CT 106
  EXPOSED and a tunnel came down over it. It survived because the flat-403
  application has nothing to follow, so the twin check covering the same
  property passed. The inverse is just as bad and quieter: a probe whose
  pass condition is anything-but-200 passes on a 500, a misrouted tunnel, or
  DNS failure. Both halves are pinned by
  `VerifyCommandTests.A_deployment_behind_two_Access_applications_passes_every_check`
  (a fake Access edge, both policy types) and its exposed-surface twin.
  **And the explanation printed beside a refusal is read off the RESPONSE,
  never mapped from the status code.** Enabling Managed OAuth flipped CT
  106's root app from 302 to 401 with NO policy change, so a status→policy
  mapping spent three releases telling operators that "a service-auth-only
  policy has no login to offer" about the one application whose whole job is
  offering an OAuth login to MCP clients — a correct verdict wearing a false
  reason, read at exactly the moment someone is deciding whether ingress
  broke. The discriminator is the RFC 9728 `resource_metadata` PARAMETER,
  never the `Bearer` scheme (Knapper's own origin challenges with a bare
  `Bearer`, so keying on the scheme would report an origin refusal as an
  edge one) and never the pointer's PATH (Cloudflare spells it
  `/.well-known/cloudflare-access-protected-resource/`, not the RFC's
  canonical name). A flat refusal names no layer and the string must not
  claim one. Pinned by
  `VerifyCommandTests.A_Managed_OAuth_refusal_is_described_by_what_it_carries_not_by_its_status_code`.
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
  sweeper is needed. Both claims are about Knapper's OWN actions, and the
  COMMIT BOUNDARY is where they end: once the destination is published and
  VERIFIED, the operation is committed, and an external writer removing or
  replacing that destination afterwards — even before the capture and cleanup
  finish — has deleted or overwritten the note exactly as if it had acted
  after the receipt; the operation still reports success (the linearizable
  reading; `DestinationRaceTests` pins both sides of the boundary). Do not
  "strengthen" this with a post-capture destination re-check deciding whether
  the hidden links may be kept — that is check-then-act over a pathname other
  writers own, the exact shape this design removed.
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
  vault is followed by any handle to it (`PostCommitFailureTests`). The same
  both-sides rule now covers EVERY mutation, not just the move/delete
  destination: edit/append/create/batch prove the target's parent resolves
  inside the vault before the write (tolerant of a missing parent — that is
  create's own NotFound) and again after it, so a parent swapped mid-write
  becomes a typed `PathOutsideVault` instead of a success receipt for bytes
  that landed elsewhere; and move/delete prove the SOURCE before linking and
  the CAPTURED name after the capture, so a swapped source parent can never
  end with an out-of-vault file quietly captured and deleted
  (`ParentSwapTests`). EVERY means mkdir too: it sat outside the pattern
  until 2026-08-22 with no lock, no conflict gate and no containment proof on
  either side, so a parent swapped after `Resolve` had
  `Directory.CreateDirectory` FOLLOW it and build the directory outside the
  vault under a receipt naming a vault path. A mutation surface that creates
  no content is still a mutation surface. The window itself stays open —
  only the consequence, a lying success receipt or an outside deletion, is
  closed.
- **The final component is linked NO-FOLLOW and judged under the private
  name, never through a symlink.** `Resolve` rejects symlink components, but
  the note itself can be swapped for an equal-content symlink one syscall
  later, and plain `link(2)` diverges exactly there: macOS FOLLOWS it — the
  move/delete link then hard-links the OUT-OF-VAULT target into the vault
  (published note aliased to an external file, external edits silently
  becoming vault content; reproduced 2026-08-20) — while Linux links the
  symlink inode and publishes a symlink into a vault that bans them. So the
  source→temp link is `linkat(…, 0)` (`Posix.LinkNoFollow`, both platforms
  link the symlink ITSELF), the private temp is inspected with non-following
  metadata and refused `[SymlinkRejected]` before anything is published, and
  the same non-following rule governs every judgement over a captured or
  displaced name: `CapturedIsOurs` calls a captured symlink not-ours (restore,
  don't read through), `TryRestoreSource` restores it AS a symlink, and
  `Replace`'s displaced-bytes check routes a symlink to the swap-back. A
  content comparison that reads THROUGH a link is comparing some other
  file's bytes (`SymlinkSwapRaceTests` — equal content everywhere, so only
  the non-following checks can be what passes them). The RECOVERY branches
  are held to the same rule: `Replace`'s restore-to-canonical and its
  visible-sibling publication both link no-follow — plain macOS link(2)
  there "restored" a displaced symlink as a hard link to its out-of-vault
  target, recreating inside the rollback the exact alias the main path had
  been cured of (round three). And symlinks are not the only non-regular
  shape: a FIFO swapped in at any judged pathname would HANG the read while
  the operation holds its path locks, so every such read classifies with
  `Posix.LStat` first and refuses non-regular files — `ReadExisting` (the
  first read of every mutation), the private-name inspection,
  `CapturedIsOurs`, `RequireStillOurBytes`, and the rollback's `Holds`.
- **Byte equality is not ownership and not continued existence.** An earlier
  fix decided rollback by comparing content and pinned a test asserting a
  byte-identical replacement gets deleted "because no distinct content is
  lost". Wrong twice: the pathname belongs to whoever created it, and after an
  external rename that pathname can be the only place the note still exists.
  Nothing may delete a publicly-named file on the strength of a content
  comparison. Content comparison decides only whether one of Knapper's OWN
  temps may go, and only ever in the KEEP direction when uncertain
  (`HiddenLinkIsTheLastCopy`): unreadable answers "keep". The raced-replace
  reclaim demands BOTH proofs before discarding — device+inode identity AND
  the exact bytes this call wrote: bytes alone blessed a byte-identical
  third-party inode for deletion (round three), and identity alone would
  trust ext4 handing a just-freed inode number straight back to a stranger.
  Either proof failing is a KEEP, and the kept object is published visibly,
  never left hidden-only.
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
  endpoint as an exception (both shipped here once). A scan's list and its
  error are ONE value (`ScanOutcome`), and a cached success is one immutable
  snapshot: /health and /up run concurrently, and when the error lived in a
  singleton field beside the returned list, overlapping requests could pair
  a COMPLETED scan with another request's error — or lose their own
  (`A_reports_scan_error_always_belongs_to_its_own_scan`).
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
  signal (someone raced, or an agent is retrying a stale base). Batch-WIDE
  rejections — either gate pass, duplicate paths, a lock timeout — land one
  entry per resolved path through `BatchWideStage`: the single-item
  operations audit the same failures via their outer catch, and a batch
  refused on a stale gate answer must not be invisible to the trail
  (`BatchRejectionAuditTests`); per-item validate/apply failures keep their
  own entries and never double-audit through it. Audit writes
  are fsynced and live OUTSIDE the vault; vault content must never reach the
  audit path — which is why the audit `Detail` field never carries an
  exception message: anchor/guard failure text IS note content (the error
  CODE is the audit signal; rich diagnostics stay on the MCP response).
- **Conflict gate: agents never resolve conflict files, and there are TWO
  families.** A Sync `* (Conflicted copy ...)*` sibling and a Knapper
  `* (Knapper displaced ...)*` sibling (a raced replace's displaced external
  version, republished visibly by `AtomicFile`) both block mutations to the
  original and the sibling until a human reconciles — the gate, the sibling
  check, and the health walk treat them identically (`ConflictDetector` is
  the ONE matcher). The Knapper family deliberately does NOT forge Sync's
  marker: same operational meaning, honest attribution. Because the sibling
  is published no-follow it can itself BE a symlink; the conflict walk
  therefore judges by NAME before its reparse-point skip (recognition never
  follows the entry, recursion still refuses every directory symlink), and a
  symlink-shaped recovery object is a filesystem-visible artifact for the
  human — not ordinary queryable or syncable vault content
  (`ConflictMarkerTests`). The sync gate (`ISyncGate`) fails mutations
  closed when continuous sync is unhealthy — no local fallback, ever.
- **The heartbeat TICK is a term in the fail-closed budget, so
  `knapper-heartbeat.timer` pins `AccuracySec=1s`.** Withholding the touch is
  the only way Knapper learns sync is unhealthy, so total exposure ≈ ob's
  detection latency + the inter-tick gap + `Sync__MaxAgeSeconds` (300s,
  sized against a 60s tick). systemd's DEFAULT accuracy is 1min — on a 60s
  period that silently nearly halves the margin (measured 116s gaps on CT
  106, presenting as a two-minute blip blocking every mutation and reading
  like a Knapper bug). Dropping `AccuracySec=1s`, or changing the period
  without moving `MaxAgeSeconds`, moves the budget silently. The gate fails
  closed in BOTH directions: a heartbeat mtime in the FUTURE (a stepped
  clock, a CT restored from snapshot — the runbook's own procedure) proves
  nothing about the watchdog, and under `age > max` alone it read as "fresh"
  for the entire skew — past a 30s tolerance
  (`FileAgeSyncGate.FutureToleranceSeconds`, deliberately far below the 60s
  tick so a withheld touch can never hide inside it) mutations block
  (`FileAgeSyncGateTests`). And
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
  error; never echo a whole secret into logs or exceptions. **Every git
  invocation drains both pipes CONCURRENTLY and waits with a BOUND**
  (`GitTimeoutMs`): that lock is exclusive and every mutation needs it
  shared, so anything blocking in `Run` blocks all vault writes with no
  caller in a position to time it out. Draining stdout to EOF before
  starting stderr deadlocks the moment git puts more than a pipe buffer on
  the stream nobody is reading, and an unbounded `WaitForExit` wedges the
  vault just as hard on a git that hangs without filling any pipe. A commit
  is a background job — failing it loudly costs one cycle and the next tick
  picks the work up, which is why the bound is the safe direction
  (`A_git_flooding_stderr_does_not_wedge_the_commit_lock`,
  `A_git_that_never_exits_is_killed_rather_than_holding_the_lock_forever`).

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
- **UTF-8 byte order is THE path order, and that includes the ORDER THE
  PREFIXES ARE HANDED TO rg.** `QueryCursor.ComparePathUtf8` is the one
  comparer; `StringComparer.Ordinal` is UTF-16 code units and the two
  diverge exactly where a non-BMP name (emoji — ordinary in Obsidian) meets
  U+E000..U+FFFF, surrogates sorting low in UTF-16 and high in UTF-8. rg
  does NOT sort globally across multiple search roots — it emits one
  internally-sorted group per root IN ARGV ORDER — so the prefix list is not
  a bookkeeping detail, it IS the emission order the cursor is compared
  against. Sorted with the wrong comparer, every record of the byte-earlier
  group compares `<=` the cursor and is dropped from page two onward while
  the final page still reports `truncated: false`: a silent omission on
  deterministic, ordinary input with no race involved (shipped through
  0.5.3 — the only two-prefix test in the suite was the overlap one, so
  nothing looked). Pinned by
  `Two_prefixes_paginate_in_the_cursors_own_order_without_omission`.
- **The prefix overlap check compares ALL PAIRS, never neighbours.** A
  parent and its child need not be adjacent once sorted: any byte below `/`
  (0x2F) sorts a sibling between them, and `-` (0x2D) and `.` (0x2E) are
  ordinary in folder names — `["Notes", "Notes-old", "Notes/Daily"]` puts
  `Notes-old` in the middle, so an adjacent-only walk compares neither pair
  that overlaps. rg then searches `Notes/Daily` under both roots and every
  file beneath it is reported TWICE, which is the duplicate the check exists
  to refuse. The cap is 64 prefixes, so the quadratic walk is free
  (`Overlapping_prefixes_are_refused_even_when_a_sibling_sorts_between_them`).
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
