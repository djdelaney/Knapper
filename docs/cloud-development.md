# Developing in Claude cloud sessions

Knapper can be developed from [Claude Code cloud sessions](https://code.claude.com/docs/en/claude-code-on-the-web)
(Anthropic-hosted Linux VMs) — build, the full test suite, and changes to
the mutation layer included. This page is how to set one up, how to check
that it is sound, and what stays on a developer's own machine.

Like the runbook, it describes how to VERIFY the environment rather than
asserting what it is: anything observed is dated, and each has a command
that re-checks it.

## Setting up the environment

In the claude.ai environment settings for this repository:

1. **Network access: Trusted** (the default). The build needs nuget.org,
   the Microsoft package servers, and GitHub release downloads; all are on
   the default allowlist.
2. **Environment variables:**
   ```
   DOTNET_NOLOGO=1
   DOTNET_CLI_TELEMETRY_OPTOUT=1
   ```
3. **Setup script:** paste the whole of
   [`ops/claude-cloud-setup.sh`](../ops/claude-cloud-setup.sh). It installs
   the .NET 10 SDK from Ubuntu's archive and the pinned ripgrep release,
   removes Ubuntu's older ripgrep, and fails the setup on anything it cannot
   verify.

**The file in the repo is the master copy; the settings field holds a
paste.** Change the script through a PR like any other file, then re-paste
it. `tests/shell/test_ripgrep_pin.sh` fails CI if its ripgrep version or
hash drifts from `ci.yml`'s or the runbook's, and checks that it parses —
but nothing can check that the pasted copy matches the file. After a change
to the script, re-paste it.

Setup output is written live and to `/var/log/knapper-setup.log`, which a
session can read afterwards.

## Checking a new environment

Run these in a session (or ask the session's agent to) after creating the
environment or changing the setup script:

```sh
# 1. Setup finished and nothing complained.
tail -1 /var/log/knapper-setup.log                 # setup complete
grep -nE '^(E|W):|WARNING' /var/log/knapper-setup.log   # expect no output

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

**Expected `dotnet test` result: 11 skipped, 0 failed.** The skips are the
`[PermissionDenialFact]` tests (see [Running as root](#running-as-root)).
Any failure is real, and so is a skip count other than 11 in those tests.

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
(2026-09-24):

- **It stops on the first error.** An earlier version ended in `exit 0`,
  which reported success whatever happened — harmless for a missing SDK
  (the build fails loudly) but not for ripgrep: a failed download left
  Ubuntu's rg 14 in place, and the suite runs green on it while "no match"
  quietly stops proving anything was searched.
- **`apt-get update` may fail; the install is judged by its result.** The
  image shipped two third-party package sources (Python and PHP archives)
  that answered 403 from the sandbox, and `apt-get update` exits 100 on any
  unreachable source even when Ubuntu's own archive, which carries .NET,
  updated fine. The script removes those two sources, still tolerates an
  update failure in case another breaks later, and then checks that a 10.x
  SDK is actually installed.
- **Ubuntu's ripgrep is removed, not just shadowed.** It is 14.x, and
  `/usr/local/bin` coming first on `PATH` protects only processes whose
  `PATH` has it. The script fails if `/usr/bin/rg` survives.
- **Output is teed to a log.** Setup output scrolls away once the
  environment is built, and a failed setup leaves no session to inspect.
