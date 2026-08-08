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

## Common commands

```sh
dotnet build Knapper.slnx
dotnet test Knapper.slnx
dotnet test tests/Knapper.Core.Tests --filter "FullyQualifiedName~CrossProcessLockTests"
```

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
