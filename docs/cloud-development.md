# Developing in Claude cloud sessions

Knapper can be developed from [Claude Code cloud sessions](https://code.claude.com/docs/en/claude-code-on-the-web)
(Anthropic-hosted Linux VMs) — build, the full test suite, and changes to
the mutation layer included. This page is how to set one up, how to check
that it is sound, and what stays on a developer's own machine.

Like the runbook, it describes how to VERIFY the environment rather than
asserting what it is: anything observed is dated, and each has a command
that re-checks it.

## Setting up the environment

An environment can serve Knapper alone or alongside other repositories in
the same session. The setup script installs what Knapper needs, plus Mailvec's
sqlite-vec library for any checkout that carries Mailvec's fetch script, so
one environment can serve both. In the claude.ai environment settings:

1. **Network access.** Setup and build need nuget.org, Microsoft's
   `builds.dotnet.microsoft.com` (the SDK's release metadata and tarball),
   and GitHub release downloads (ripgrep, and Mailvec's sqlite-vec). A
   refused host fails setup at the step that needed it; add it under a
   custom allowlist.
2. **Environment variables:**
   ```
   DOTNET_NOLOGO=1
   DOTNET_CLI_TELEMETRY_OPTOUT=1
   VSTEST_ARTIFACTS_BASE_DIRECTORY=/tmp
   ```
   The last one is for `dotnet test` as root in a container: the SDK wants
   a private directory under `/run/user/0`, which does not exist here, and
   without the variable every run prints a warning that blames an outdated
   test package.
3. **Setup script:** paste the whole of
   [`ops/claude-cloud-setup.sh`](../ops/claude-cloud-setup.sh). It installs
   the latest .NET 11 SDK from Microsoft's tarball, checked against
   Microsoft's published SHA-512; the pinned ripgrep release, removing
   Ubuntu's older one; the `sqlite3` CLI; and, for any checkout carrying
   Mailvec's `ops/fetch-sqlite-vec.sh`, the sqlite-vec library Mailvec's
   tests load. It fails the setup on anything it cannot verify.

The setup script runs as root after the session's repositories are cloned,
starting in the directory that holds the checkouts, and only when a
container is built fresh. Its result may be cached and reused by later
sessions.

**The file in this repo is the master copy; the settings field holds a
paste.** Mailvec's cloud guide points here instead of keeping its own copy.
Change the script through a PR here, then re-paste it. The field holds at
most 100 lines of at most 200 bytes, and the script is at the limit, so an
addition means trimming elsewhere. `tests/shell/test_ripgrep_pin.sh` fails
CI if its ripgrep version or hash drifts from `ci.yml`'s or the runbook's,
and checks that it parses. Nothing in CI can see the pasted copy. A session
whose agent has the environment tools can read the stored script and
compare it with this file; with no paste pending they are byte-identical.

Setup output is written live and to `/var/log/dotnet-env-setup.log`, which
a session can read afterwards.

## Checking a new environment

Run these in a session (or ask the session's agent to) after creating the
environment or changing the setup script:

```sh
# 1. Setup finished and nothing complained.
tail -1 /var/log/dotnet-env-setup.log                 # setup complete
grep -nE '^(E|W):|WARNING' /var/log/dotnet-env-setup.log   # expect no output

# 2. Exactly one ripgrep, the pinned one.
type -a rg                                         # only /usr/local/bin/rg

# 3. The same checks the server runs at startup, over a throwaway vault.
mkdir -p /tmp/kdev/vault /tmp/kdev/locks
Vault__RootPath=/tmp/kdev/vault Vault__LockDirectory=/tmp/kdev/locks \
Vault__AuditLogPath=/tmp/kdev/audit.jsonl Sync__Mode=open \
dotnet run --project src/Knapper.Cli -- doctor     # all ok; one warn: sync gate OPEN

# 4. The suite.
tests/shell/run.sh
dotnet test Knapper.slnx
```

**Expected `dotnet test` result: 0 failed, and the skipped tests are exactly
the `[PermissionDenialFact]` ones** (see [Running as root](#running-as-root)):
12 as of 2026-11-23. Count them rather than trusting that number:

```sh
grep -rE '^\s*\[PermissionDenialFact' tests --include=*.cs | wc -l
```

Any failure is real, and so is a skip count that differs from that count, or
a skipped test without the attribute.

`rg --version` typed into the agent's shell is not a reliable check: Claude
Code can put its own bundled `rg` there, which the .NET process never sees.
Doctor's ripgrep line resolves `rg` the way the server does.

## What the machine is

Observed 2026-09-24 — re-check with the commands in the right-hand column.

| Property | Observed | Re-check |
|---|---|---|
| Runtime | Firecracker microVM, real Linux kernel (6.18) | `uname -a`; `dmesg \| head` shows a `--firecracker-init` kernel command line |
| Filesystem | ext4 on `/dev/vda`, for both `/tmp` and the checkout | `df -T /tmp .` |
| OS | Ubuntu 24.04, x86_64 | `cat /etc/os-release`; `uname -m` |
| User | root (uid 0) | `id -u` |

Why it matters: the mutation contract stands on real kernel behavior —
`renameat2(RENAME_EXCHANGE)`, `flock`, `linkat` without following,
`statx` — and fails loudly without it (`Posix.Exchange` refuses rather than
falling back to an overwriting rename). A syscall-emulating sandbox could
lack any of these. On a real kernel over ext4 the atomic-swap, lock, crash
and acceptance tiers all pass, and the filesystem is case-sensitive like
production, which a default macOS volume is not.

If a future environment reports something else here — gVisor, a different
filesystem — run the full suite before trusting it with mutation-layer work.

## Running as root

Cloud sessions run as root, and root ignores file permissions. Tests that
take a permission away and assert what Knapper does when the read or write
is refused are tagged `[PermissionDenialFact]`: they skip when a probe
measures that refusal does not happen, and CI (unprivileged, with
`KNAPPER_REQUIRE_PERMISSION_DENIAL=1`) always runs them. So CI stays the
authority for those tests; everything else runs the same in both places.
New tests of this kind use the same attribute — see the testing conventions
in [extending.md](extending.md).

## What stays on a developer's machine

| Task | Why not from a cloud session |
|---|---|
| `ops/release.sh --ship` | It pushes to `main`. Cloud sessions can push only to their own working branch; they open PRs, and releases are cut locally. |
| `ops/publish.sh` | It must run from a clean checkout of the release tag, and its output is installed on the production host. |
| Deployment, runbook §10, `knapper verify --url` | They need the production network and its credentials, which cloud sessions do not have and should not be given. |

## Running a dev server

To drive the real tools by hand — or let the session's agent try them —
start a server over a generated vault (details in
[usage.md](usage.md#running-locally-dev)):

```sh
tools/dev-vault.sh /tmp/kdev
. /tmp/kdev/env.sh && dotnet run --project src/Knapper.Mcp &
curl -s 127.0.0.1:3535/health | jq .status      # "ok"
```

It exercises what the unit fixtures cannot show together: the manifest and
tool descriptions as a client receives them, pagination over real content,
and a vault shaped like the ones Knapper serves.

## Keep dev sessions away from the production vault

A cloud session can have claude.ai connectors enabled, including the
production Knapper connector — which reads and writes the real vault, synced
to real devices. Leave it off in development sessions. Everything
development needs runs against throwaway vaults: the test suite builds its
own in temp directories, and a server run by hand uses a scratch directory
with `Sync__Mode=open` (the dev-only opt-out of the sync gate; see
[usage.md](usage.md#running-locally-dev)).

## Why the setup script looks the way it does

Each of these was a real failure while the environment was first set up
(2026-09-24), or while it moved to .NET 11 and became shared (2026-11):

- **It stops on the first error.** An earlier version ended in `exit 0`,
  which reported success whatever happened — harmless for a missing SDK
  (the build fails loudly) but not for ripgrep: a failed download left
  Ubuntu's rg 14 in place, and the suite runs green on it while "no match"
  quietly stops proving anything was searched.
- **`apt-get update` may fail; the install is judged by its result.** The
  image shipped two third-party package sources (Python and PHP archives)
  that answered 403 from the sandbox, and `apt-get update` exits 100 on any
  unreachable source even when Ubuntu's own archive updated fine. The
  script removes those two sources, still tolerates an update failure in
  case another breaks later, and then checks that an 11.x SDK is actually
  installed.
- **The SDK comes from Microsoft's tarball, not apt or `dotnet-install.sh`.**
  Ubuntu 24.04's archive stops at .NET 10, packages.microsoft.com carries no
  .NET SDKs for 24.04, and the `dotnet/backports` PPA is refused from the
  sandbox. `dotnet-install.sh` verifies nothing it downloads, so the script
  reads Microsoft's release metadata and checks the tarball against the
  SHA-512 published there. Verified 2026-11-23: SDK 11.0.100, and Knapper and
  Mailvec built and passed their suites.
- **Mailvec's sqlite-vec is installed here, not only by its hook.** A
  session carrying several repositories never loads Mailvec's
  `.claude/settings.json`, so its SessionStart hook does not run, and about
  650 Mailvec tests fail without the library (observed 2026-11-23). The
  setup script finds the checkout by file, not folder name, and runs that
  repository's own fetch script, so the library's SHA-256 pin stays in
  Mailvec.
- **Ubuntu's ripgrep is removed, not just shadowed.** It is 14.x, and
  `/usr/local/bin` coming first on `PATH` protects only processes whose
  `PATH` has it. The script fails if `/usr/bin/rg` survives.
- **Output is teed to a log.** Setup output scrolls away once the
  environment is built, and a failed setup leaves no session to inspect.
