# Knapper

*A [knapper](https://en.wikipedia.org/wiki/Knapping) shapes obsidian into
tools. This one shapes an Obsidian vault into tools for AI agents.*

Knapper is an always-on MCP server that acts as the single authoritative
read/write interface to an Obsidian vault for AI agents (Claude web/desktop/
mobile, Claude Code, and future automation). Humans keep editing through
normal Obsidian apps + Obsidian Sync; agents go only through this service —
which turns a distributed agent-concurrency problem into one server-side
transaction problem.

Design pillars (see `obsidian-mcp-implementation-brief.md` for the full
contract):

- **Conditional writes only.** Every mutation requires the file's current
  SHA-256 and runs under a cross-process advisory lock: fresh read → hash
  check → anchored edits → guard validation → atomic same-directory commit →
  reopen and byte-compare. No last-write-wins tool exists to expose.
- **A real query surface**, not a search box: constrained ripgrep semantics
  (structured args, never a shell), explicit completeness envelopes
  (`truncated` + cursor — "no match" means the scope was exhaustively
  searched), and a vault generation counter.
- **Fail closed.** Sync conflict files block mutations until a human
  reconciles; unhealthy sync blocks writes; there is no local-filesystem
  fallback for agents.
- **Ingress** via Cloudflare Tunnel + Cloudflare Access (origin-validated),
  never a LAN port.

## Layout

| Path | What |
|---|---|
| `src/Knapper.Core` | Safety primitives (path containment, SHA-256 preconditions, atomic commits, cross-process flock locks), the query layer (ripgrep search, file listing, reads/stat, frontmatter queries, generation counter), and the transaction layer (anchored edits, append, no-clobber create, move, soft delete, batch — with conflict/sync gates, a write-ahead JSONL audit log, and a durable metrics snapshot for the external monitor) |
| `src/Knapper.Mcp` | ASP.NET Core MCP server: 13 locked tools over Streamable HTTP, Cloudflare Access origin validation (loopback exemption requires loopback peer AND Host), DNS-rebinding guard, `/health` (loopback, detailed) + `/up` (monitor, booleans only) |
| `src/Knapper.Cli` | `knapper` admin binary: `git-init` / `commit` (vault-wide lock + staged secret scan + monitor freshness stamp) / `status` / `doctor` / `audit-tail` |
| `ops/` | systemd units (MCP, obsidian-headless sync, heartbeat + commit timers), the Proxmox-host monitor kit (`ops/monitor/`: silent-on-success alerting over `/up` + commit-stamp age + metrics deltas), self-verifying publish script, CT 106 deployment runbook |
| `tools/Knapper.LockProbe` | child-process probe for genuine two-process lock tests |
| `tools/Knapper.MutationProbe` | child-process probe for two-process stale-edit / simultaneous-create races |
| `tests/` | three tiers: `Knapper.Core.Tests` (semantics, including real multi-process lock and mutation races), `Knapper.Mcp.Tests` (the wire envelope in-process, including the Cloudflare Access topology), and `Knapper.AcceptanceTests` (the brief §13 black box: REAL server processes spawned over real HTTP — two-process transport races, fault injection, ripgrep-oracle equivalence) |

## Build

```sh
dotnet build Knapper.slnx
dotnet test Knapper.slnx                    # all three tiers, incl. black-box acceptance
dotnet test tests/Knapper.AcceptanceTests   # just the real-process acceptance tier
```

Requires .NET 10 SDK, plus `ripgrep` and `git` on PATH. Linux/macOS only —
the locking and atomic-commit semantics are POSIX by design.

## Status

Code-complete through the brief's §13 definition of done as far as a repo
can take it: five review rounds are closed (implementation → remediation →
pre-deployment → security → independent verification), every finding fixed,
refuted, or recorded as a deliberate non-fix; the black-box acceptance tier
(real server processes, real HTTP, two-process races, fault injection)
passes; and transcript mining of real local sessions validated the
client-fit half of the contract. The full per-finding review record lives
in git history (the review documents and their dated remediation banners
were committed before being retired — search the log for "review"). Open
decisions and deliberately-closed questions are consolidated in
[docs/extending.md](docs/extending.md). Helios cutover is gated on the
live CT 106 sequence — the §8b behavioral smoke test, tunnel fail-closed
checks, alert-path exercises, backup acceptance, and explicit sign-off:
[ops/ct106-runbook.md](ops/ct106-runbook.md) §§8b–9. This paragraph
intentionally stays vague enough not to rot — trust those, not this.

## Docs

| Doc | What |
|---|---|
| [docs/architecture.md](docs/architecture.md) | Project structure, the query + transaction layers, locking model, gates, security model |
| [docs/usage.md](docs/usage.md) | Running/configuring, connecting clients, the 13-tool reference, error codes, monitoring |
| [docs/extending.md](docs/extending.md) | Adding tools/queries/mutations without breaking the contracts; test + build conventions |
| [ops/ct106-runbook.md](ops/ct106-runbook.md) | Production deployment on the Proxmox LXC |
| [CLAUDE.md](CLAUDE.md) (= [AGENTS.md](AGENTS.md)) | The silent invariants — what a change must not break even though nothing would fail loudly. Two byte-identical files, one per agent convention (not a symlink — some tooling refuses those); edit `CLAUDE.md` then `cp CLAUDE.md AGENTS.md`. CI aborts on drift |
