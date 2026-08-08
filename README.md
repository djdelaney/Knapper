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
| `src/Knapper.Core` | Safety primitives: path containment, SHA-256 preconditions, atomic file commits, cross-process flock advisory locks |
| `src/Knapper.Mcp` | (upcoming) ASP.NET Core MCP server — tools, auth, health |
| `src/Knapper.Cli` | (upcoming) admin binary: git commit job, status, doctor |
| `tools/Knapper.LockProbe` | child-process probe for genuine two-process lock tests |
| `tests/` | xunit suites, including real multi-process lock races |

## Build

```sh
dotnet build Knapper.slnx
dotnet test Knapper.slnx
```

Requires .NET 10 SDK. Linux/macOS only — the locking and atomic-commit
semantics are POSIX by design.
