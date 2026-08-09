# CLAUDE.md

Guidance for Claude Code in this repository. Stays focused on invariants whose
violation is **silent** (silent data corruption, silent contract break). Loud
failures don't earn a spot.

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
  contracts; testing and build conventions; scoped-but-unbuilt ideas.
- [`ops/ct106-runbook.md`](ops/ct106-runbook.md) — production deployment.
  Runbooks describe how to VERIFY live state, never what it was (house rule):
  date and mark anything observed.

## Common commands

```sh
dotnet build Knapper.slnx
dotnet test Knapper.slnx
dotnet test tests/Knapper.Core.Tests --filter "FullyQualifiedName~CrossProcessLockTests"
dotnet run --project src/Knapper.Cli -- doctor      # config/dependency checks (env: Vault__RootPath etc.)
ops/publish.sh                                      # linux-x64 tarball for CT 106
```

Deployment: `ops/ct106-runbook.md` (condensed from the brief; the brief's
§11 corrections are mandatory reading before building the CT).

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

## Core invariants (silent-corruption-prone)

- **`AtomicFile` is the only code that writes vault bytes.** Hidden
  same-directory temp (`.knapper-tmp-`) → fsync → last-instant SHA re-check →
  rename/hard-link → directory fsync, temps cleaned on every failure path.
  A write path that bypasses it loses atomicity, mode preservation, or the
  no-clobber guarantee without any test necessarily noticing.
- **`VaultPathResolver` is the only gate between agent-supplied path strings
  and the filesystem.** Nothing else may combine user input with the vault
  root. `VaultPath`'s constructor is internal so an API taking one is stating
  validation already happened — don't add public construction.
- **Lock ordering is global-shared → per-path-exclusive, always**, and the
  commit job takes only the global lock exclusively — that's what makes
  deadlock structurally impossible. A new lock kind must slot into this
  hierarchy, not beside it.
- **Lock files are opened via raw `open(2)`/`creat(2)` interop
  (`Posix.OpenLockFile`), never `FileStream`.** The .NET Unix runtime
  emulates FileShare with flock locks of its own during open, which contend
  with a real lock holder and turn "wait for the lock" into an IOException at
  open time (`FileShare.ReadWrite | Delete` does NOT disable it — measured
  here). And the 3-arg `open(2)` must never come back: its mode parameter is
  variadic, and Apple's arm64 ABI passes variadics on the stack, so a fixed
  3-arg P/Invoke creates lock files with garbage permissions (errno 13 on the
  next open). `creat(2)` is the non-variadic create.
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
- **Tool errors lead with the bracketed code** (`[PreconditionFailed] …`) via
  `ToolSupport.Run` — agents parse that prefix to decide "re-read and
  rebuild" vs "give up". New tools go through `Run`; a bare exception would
  reach clients as an unstructured message with no code.
- **The locked tool table (`ToolSurface.All`) and the
  `[McpServerTool(Name=…)]` attributes are held in lockstep by a test.**
  Tool names are a client-facing contract; renames are version bumps, not
  refactors. There is no unconditional-write tool in the table and never
  will be.
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
- **Move and delete are link-then-unlink, not rename.** rename(2) silently
  replaces an existing destination; link(2) cannot. Same inode means no data
  copy and no window where content could diverge. Deletes are SOFT — into
  `.trash/` with structure preserved, collisions timestamped, never
  overwriting an earlier trash copy. A failure after the link and before
  the unlink rolls the new link back (a failed operation leaves no new
  pathname), and the source is re-verified by content immediately before
  the unlink — an external writer's replacement landing mid-operation must
  never be silently destroyed (`ExternalWriterRaceTests`).
- **Batch validates EVERYTHING under the locks before the first write.** A
  bad hash/anchor/guard anywhere fails the whole batch untouched. The apply
  phase is not cross-file atomic (documented; git history is recovery), and
  duplicate paths are rejected because flock is per-descriptor — a second
  acquisition of the same path would self-deadlock.
- **Multi-path locks acquire in sorted order** (`AcquirePathLocks`), global
  shared first — the fixed order is the deadlock-freedom proof. New lock
  users slot into this hierarchy.
- **Rejections are audited, not just successes.** A stale-write rejection is
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
