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
- Cross-process claims need cross-process tests: the probe binaries are
  copied into the test output by project reference and spawned with
  `dotnet exec`. An in-process test of flock proves nothing.
- Wire behavior is tested through the SDK's `McpClient` against
  `WebApplicationFactory` — the same JSON-RPC path Claude uses.
  `RemoteIpStartupFilter` declares which caller the factory simulates
  (loopback vs off-box); without it every loopback-sensitive control fails
  closed on TestServer's null remote address.

## Build conventions

- Central Package Management: versions in `Directory.Packages.props` only;
  csproj `<PackageReference>` without `Version=`. Adding a dependency means
  editing both files.
- `TreatWarningsAsErrors` repo-wide. Unix-only (`SupportedOSPlatform`
  linux+macos in `Directory.Build.props`) — don't add Windows guards.
- One repo-wide `<Version>` in `Directory.Build.props`, surfaced through
  `initialize.serverInfo.version` and the CLI.

## Ideas already scoped (not yet built)

- **Transport-level §13 acceptance runner**: a `knapper verify` subcommand
  running the equivalence + race suites against a LIVE server URL, for the
  pre-cutover check on CT 106.
- **Read-only deployment profile**: `Mcp:DisabledTools` with the seven
  mutation tools listed, as a documented one-liner.
- **Per-client credentials** (brief §8 "where practical"): Access already
  distinguishes identities in the audit log; separate Access apps per agent
  surface would let Cloudflare policy differ per client.
- **`vault_search` context in files/counts modes** is intentionally absent;
  matches mode covers the need.
- **Obsidian-flavored queries** (backlinks, tags-as-index) — worth doing
  only if agents demonstrably need more than frontmatter + full-text.
