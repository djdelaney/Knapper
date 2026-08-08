# Obsidian Headless MCP — Implementation Brief

Prepared 2026-08-08 for an implementation agent with **no access to Dan's Obsidian vault**. This document is self-contained: every decision, environment fact, contract, and known trap needed to build the service is here. Decisions listed below are **made — do not relitigate them**. Where something is genuinely open or needs Dan, it is listed in "What you need from Dan."

## 1. Mission

Build an always-on, internet-reachable MCP server that becomes the **single authoritative read/write interface to Dan's Obsidian vault ("Helios") for every AI agent** (Claude web/desktop/mobile, Claude Code, Cowork, future automation). Humans keep editing via normal Obsidian apps + Obsidian Sync; agents go only through this service. The service turns today's distributed agent-concurrency problem into one server-side transaction problem.

The vault: a personal Obsidian vault (a few MB, a few hundred markdown notes plus a small set of canonical shell/python scripts), synced across a handful of desktop and mobile devices via **Obsidian Sync**. It holds sensitive personal material — **treat a compromise of this service as a compromise of everything in the vault**, not merely a technical incident. Treat every credential for this service accordingly.

## 2. Decisions already made (with dates)

1. **Host: a new native LXC on the Proxmox host, VMID 106** (decided 2026-08-01). Not a Docker container, not a VM. Plain systemd services in an unprivileged Debian 13 CT.
2. **Agent access model: MCP-only** (decided 2026-08-08). After cutover, no agent gets a local vault folder, mount, or shell path on any Mac. If the MCP is down, agent vault work **stops** — no fallback. (A "prefer local when mounted" model was explicitly rejected.)
3. **This LXC is the vault's only git committer** (decided 2026-08-01). Local-only repo; no remote (see gates, §10).
4. **Vault sync into the LXC: official `obsidian-headless` CLI against Obsidian Sync** (Option A, confirmed 2026-08-08). Not Syncthing — one sync topology only.
5. **Auth/ingress: try the server's native OAuth over a bare Cloudflare tunnel first (B1); fall back to Cloudflare Access in front with the app's OAuth disabled (B2)** if claude.ai's connector cannot complete the server's OAuth discovery/DCR handshake. Either way: tunnel-only, no LAN port, no port-forward.
6. **Endpoint: `https://mcp.example.com/`** — DNS zone `example.com` is already on Cloudflare.
7. The stock upstream server is a **foundation, not the finished product**. It must be forked/wrapped to add (a) an authoritative read/query surface and (b) a conditional-write transaction layer (§6, §7). Stock mutation tools are last-write-wins and **must not be exposed**.

## 3. Target environment

| Fact | Value |
|---|---|
| Hypervisor | A Proxmox VE host on the LAN (web UI on the standard `:8006`, root via Linux PAM) |
| Existing guests | Several unrelated VMs and LXCs already run on this host, including a Proxmox Backup Server VM |
| New guest | **CT 106**, Debian 13, unprivileged, rootfs on `local-zfs` (ZFS pool `rpool`, NVMe). Suggested shape (Dan confirms): 2 cores / 2–4 GB RAM / 16 GB rootfs |
| Network | Create a **DHCP reservation** for the CT (the operator assigns the IP). `onboot: 1`, `startup: order=` after the other guests |
| Backups | Nightly **PBS** (selection = All → CT 106 is auto-included) with offsite replication; plus a monthly vzdump to an NFS backup share on the LAN NAS |
| Mail relay pattern | msmtp → Fastmail SMTP with a scoped app password (see §11). Recipient tagging convention: routine/upgrade mail → `lab@example.com`; monitors that are silent-on-success → `alerts@example.com`; deliberate failure-injection tests → `failtest@example.com` |
| Vault path in CT | Local dataset/directory, e.g. `/vault`, **on the CT's local rpool-backed rootfs or a local mount — NEVER on NFS** (file watchers and locking misbehave on NFS; this rule is house policy) |

## 4. Architecture

```
Human Obsidian clients ◄──► Obsidian Sync cloud ◄──► obsidian-headless ──┐
                                                                         ▼
All agent clients ──► cloudflared ──► MCP transaction gateway ───────► /vault
                                      │ SHA preconditions + locks         │
                                      └── audit + conflict gate + git ◄───┘
```

Three systemd services in CT 106, sharing `/vault` under **one dedicated Unix account**:

1. **`obsidian-headless.service`** — official Obsidian CLI (`ob`) running `ob sync --continuous`. npm global package, requires **Node 22+** (NodeSource). Ships **no systemd unit** — you write it. Linux caveat: no birthtime preservation (cosmetic only).
2. **`obsidian-web-mcp.service`** — hardened fork/wrapper of [`jimprosser/obsidian-web-mcp`](https://github.com/jimprosser/obsidian-web-mcp) (Python 3.12 / FastMCP, run via `uv`). Stock features you keep: OAuth 2.0 + PKCE (fail-closed — refuses to start authorizing without `VAULT_OAUTH_PASSWORD`), path-traversal/symlink protection, soft deletes to `.trash/`, append-only JSONL audit log, atomic temp-then-rename target replacement. Stock gaps you must fix: **simultaneous writes are documented last-write-wins**; reads/searches have a ~50-match cap; no conditional writes. Ships systemd/launchd examples, **no Dockerfile** (that's why the LXC is the right host).
3. **`cloudflared.service`** — Cloudflare tunnel to `mcp.example.com` (apt repo from Cloudflare).

Run the sync client and the MCP under the **same Unix account** — the stock temp-replace write can otherwise leave files readable only by its own service user.

Pin everything: a specific git commit/tag of the MCP fork, a specific `obsidian-headless` npm version. Never float "latest."

## 5. Sync client configuration (exact)

```
ob login                       # interactive — needs Dan (Obsidian account credentials)
ob sync-setup --vault Helios --device-name obsidian-mcp
ob sync-config --conflict-strategy conflict --file-types image,audio,video,pdf,unsupported
ob sync-status --json          # verify before proceeding
```

Two settings are load-bearing:

- **`--conflict-strategy conflict`** — conflict *files*, never automatic merge. Dan's GUI clients are configured the same way. When a `*(Conflicted copy ...)*` file appears, the MCP must **alert and block mutations** to the original and its conflict sibling until a human reconciles. Agents never silently pick a canonical branch.
- **`--file-types ... unsupported`** — without the `unsupported` class, `.py`/`.sh`/`.json` canonical scripts silently never reach the server. (This exact omission caused a multi-day two-machines-disagree incident in this vault on 2026-08-03.)

Know the boundary: non-Markdown files under Obsidian Sync are **last-modified-wins**, not conflict-protected. Centralizing agent writes removes agent/agent loss; human/host edits to scripts elsewhere remain an external-writer risk.

## 6. Required read/query surface (contract)

Agents lose local `rg`/`find`/ranged-read when they lose vault access, so the MCP must replace those semantics. A friendly full-text search box is **not** sufficient. Implement as a **constrained query API, not remote shell**: build `rg` subprocess args from validated fields (no `shell=True`), reject path traversal, symlinks escaping `/vault`, raw shell fragments, arbitrary `find` expressions, and any access to `.git`, `.obsidian`, `.trash`, audit logs, or secrets. Enforce time/scanned-file/match/output-byte budgets; support cancellation; distinguish timeout/cancel/error from a true empty result; define binary and non-UTF-8 behavior explicitly.

| Operation | Required behavior |
|---|---|
| `vault_files` | Recursive `rg --files`/constrained-`find` equivalent. Filters: path prefix, name/path glob, extension/type, file-vs-dir, mtime before/after, size range. Stable path sort; optional size/mtime/SHA metadata; cursor pagination. Hidden control dirs excluded. |
| `vault_search` | Server-side ripgrep with structured args: literal/regex; case sensitive/insensitive/smart; whole-word; multiline; path prefixes; include/exclude globs and types; before/after context; line+column numbers; output modes = matches, filenames-only, per-file counts, total count. Structured records, not terminal text. |
| `vault_read` | Whole file or inclusive line range; optional line numbers. Always return path, content, SHA-256, size, mtime, encoding, total line count. If a limit prevents a full read: reject or paginate explicitly — never return a silently truncated "complete" file. |
| `vault_batch_read` | Multiple paths/ranges per request; per-path result + SHA; one bad file must not hide the others' results. |
| `vault_stat` | Existence, resolved vault-relative path, type, size, mtime, encoding/text status, SHA — without the body. |
| `vault_search_frontmatter` | Keep upstream's structured frontmatter query (field value / substring / existence) as a supplement to text search. |

**Completeness envelope on every list/search response:** `items`, `truncated`, `next_cursor`, `scanned_files`, `returned_items`, `total_matches` (or explicit `null` — never guessed). A safety cap is acceptable only with `truncated: true` plus a usable cursor. "No match" must mean the scope was exhaustively searched. Deterministic page order; pages never omit or duplicate records.

**Generation counter:** maintain a monotonic `vault_generation`, incremented on MCP mutations and on filesystem-watcher events (Sync/external writes). Read/list/search responses carry `generation_start`, `generation_end`, `changed_during_query`. Per-file SHA-256 remains the actual mutation precondition.

## 7. Required mutation contract (transaction layer)

The reference implementation for these semantics is `vault-edit.py` (delivered alongside this brief as `vault-edit.reference.py`) — the tool agents currently use for safe local vault writes. Carry its semantics into the MCP; hide or disable **every** unconditional upstream mutation tool. Safe wrappers *beside* unsafe originals are not sufficient — that leaves a bypass.

| Operation | Required behavior |
|---|---|
| `vault_edit` | Require `expect_sha256`. Under a **process-shared per-path lock**: re-read, compare hash, apply ordered exact-count anchored edits, validate guard strings, write. Reject stale input without mutating. |
| `vault_append` | Same `expect_sha256` + same lock. Never implemented as unlocked read-then-rewrite. |
| `vault_create` | Atomic no-clobber create (same-filesystem hard-link pattern, not `exists()` + `os.replace()`). Fails if the path appears concurrently. |
| Full-file write | **Disabled by default.** If retained: `expect_sha256` for replacement or explicit `expect_absent` for creation. No unconditional overwrite tool is ever exposed. |
| Move / delete | Expected source hash required; move also requires absent destination. Deletes are **soft** (to `.trash/`) and confirmed. |
| Batch mutation | Acquire path locks in sorted order; validate ALL hashes/anchors/guards before the first write. Document that this is not cross-file atomic; audit partial failures; rely on git history for recovery. |

**Critical section (exact order):** lock → fresh read → SHA check → transform → validate guards → write hidden same-directory temp → `fsync` → final SHA check → atomic replace → reopen and compare complete bytes → unlock. Preserve file mode; let mtime advance; reject symlink arguments and out-of-vault paths; clean hidden temps on every failure path.

The lock must work across every MCP worker **process**, not just one asyncio loop. Start with **one application worker** plus a filesystem advisory-lock design; do not add workers until the lock passes a genuine two-process race test.

**Verification is by content, never by receipt.** This vault has a documented history of writes that reported success without landing. Every write reopens and byte-compares; acceptance tests confirm through a second channel.

## 8. Enforcement, audit, and health

- **Fail closed.** MCP/tunnel/auth/sync-health failure blocks agent vault work. Never fall back to any local path.
- **Sync-health gate:** mutations require a healthy continuous-sync service; expose sync age/status in MCP health. (No status call can prove another offline device has no pending edit — accept that.)
- **Identify writers:** distinct credentials per client where practical. Append-only JSONL audit log **outside `/vault`**: request ID, client/token identity, operation, target, before/after checksums. Audit rejected stale writes too.
- **Resource caps have protocol semantics:** every cap produces a typed error or explicit `truncated` + continuation. Silent partial success is forbidden.
- **Monitoring lives OUTSIDE CT 106** (house rule: a check on the monitored machine cannot report that machine dead). On the Proxmox host, following the existing `pbs-backup-freshness.sh` precedent: MCP health, sync status/age, query timeouts/errors/truncation rate, generation-changed responses, stale-write rejections, conflict-file count, audit-write failures, and **git-commit freshness** (last-commit age threshold — a quietly stopped commit job is indistinguishable from "nothing changed"). Silent on success; alerts to `alerts@example.com`. Exercise every failure path before trusting it.
- **Secrets:** `VAULT_OAUTH_PASSWORD` and any service tokens go to Dan's password manager, never into the vault, never into this doc's successors. MFA already on Cloudflare; add a rate-limit rule on the hostname.

## 9. Auth / ingress detail

- **B1 (start here):** `cloudflared` → server; Claude connectors use the server's own OAuth 2.0 + PKCE. Watch specifically for the **redirect-URI / Dynamic Client Registration** failure class during claude.ai connector setup — that class bit this homelab's previous MCP go-live.
- **B2 (fallback):** Cloudflare Access app + Managed OAuth (for Claude Desktop/iOS) + service token (for Claude Code); the app's own OAuth must then be **disabled/delegated** so there's no double-gating. Confirm the server can actually run in a trust-the-edge mode before committing (it fail-closes without `VAULT_OAUTH_PASSWORD` — verify it can delegate).
- Never expose a LAN port; verify fail-closed behavior **before** the tunnel route goes live.

## 10. Git committer design

- `git init` inside `/vault` on CT 106 only. Obsidian Sync never syncs `.git` (ignores hidden files except `.obsidian`), so this is structurally the only replica with history — that is the design, not an accident. Both Macs and the iPhone stay git-free.
- `.gitignore` at minimum: `.obsidian/workspace.json`, `.obsidian/workspace-mobile.json`, `.DS_Store`, `.trash/`, `.vault-edit-tmp-*`, upstream `*.tmp` patterns, and the Local Backup plugin's output if it writes in-vault. Prefer making every temp hidden and Sync-ignored.
- The commit job (`git add -A && git commit`, scheduled) takes a **vault-wide commit lock compatible with the mutation locks**, so it can never snapshot a prepared-but-uncommitted batch.
- ⚠️ **`.git` breaks the "disposable container" assumption.** Sync can restore files, never history. Once `.git` exists here: PBS/vzdump coverage of CT 106 is the *only* protection for vault history; `pct snapshot` before upgrades is load-bearing; any future rebuild must restore `.git` deliberately.
- ⛔ **NO git remote (GitHub etc.) until the vault credential sweep is closed** — until every credential-shaped string in the vault has been reviewed and resolved, a remote would carry exposure that local-only git does not. Once init happens, a **pre-commit secret-scan hook** is wanted so the vault can't accept new secrets.
- Never run `git checkout`/`reset` against the live tree casually — git rewrites files, Sync propagates the revert vault-wide to every device.

## 11. Guest OS + ops recipe (house standard, with paid-for corrections)

`unattended-upgrades` scoped to **Debian-Security only**, `Automatic-Reboot "false"`; `msmtp` → Fastmail (a new dedicated app password, SMTP scope — Dan creates it; value to password manager); monthly-nudge cron. **Six corrections, each a failure already paid for elsewhere in this lab — apply all at build time:**

1. `set_from_header on` in the msmtprc `defaults` block. unattended-upgrades calls sendmail directly with `From: root@<host>`; without this Fastmail rejects `551 5.7.1` and the failure is silent.
2. Answer **NO** to the msmtp AppArmor debconf prompt (profile can't load in an unprivileged LXC and breaks `passwordeval`/logfile). Password **inline** in msmtprc; log via `syslog LOG_MAIL` — never `passwordeval` + file, never `/var/log/msmtp.log`.
3. Install **`bsd-mailx`**, never `mailutils` (pulls a full MTA that fights `msmtp-mta` for `/usr/sbin/sendmail`).
4. Tagged recipients per §3 — `Unattended-Upgrade::Mail "lab@example.com"`.
5. Test cron entries with `env -i` and cron's real PATH (`/usr/sbin` is absent from it; interactive-shell tests lie).
6. Prove the **unattended-upgrades mail path specifically**: set `MailReport "always"`, run `unattended-upgrade -v`, confirm arrival, set back to `on-change`. An `echo | mail` test does NOT cover it. Record which path was proven.

**LXC build traps (all previously hit in this lab):**

- Unprivileged CT + Debian 13: set **`nesting=1`** even with no Docker — systemd 254+ needs it for `LoadCredential=` tmpfs; without it units fail `243/CREDENTIALS` and the CT boots degraded.
- **Pin the CT's DNS** (`pct set 106 --nameserver "192.168.1.1 1.1.1.1"`). A blank DNS field inherits the Proxmox host's resolver, which is Tailscale MagicDNS `100.100.100.100` — a black hole inside a CT. `/etc/resolv.conf` regenerates only at CT start; reboot and re-check.
- **vzdump of an unprivileged CT to the NFS backup share fails without `tmpdir: /var/tmp` in `/etc/vzdump.conf` on the host** (tar runs as UID 100000 in a userns and can't traverse the root-squashed staging dir). The fix should already be in place from an earlier CT on this host — **verify, don't assume**, with a manual `vzdump 106 --storage nas-backup` + `zstd -t` of the artifact, and do it **before `.git` exists**.
- There is **no community helper script** for this service (the community-scripts "Obsidian" entry is an unanswered request) — everything is a hand-written runbook.
- `pct snapshot obsidian-mcp pre-upgrade-YYYYMMDD` before any OS/app bump. App upgrades = move the pinned git tag / npm version deliberately, snapshot first; `cloudflared` rides apt.

## 12. Build sequence

1. **LXC:** Debian 13 unprivileged CT, VMID 106, DHCP reservation, `onboot: 1`, DNS pinned, `nesting=1`.
2. **Runtimes:** Node 22 (NodeSource), Python 3.12 + `uv`.
3. **Sync unit:** §5 exactly; then a self-written `obsidian-headless.service` running `ob sync --continuous` against `/vault`. Verify content arrives, including `.sh`/`.py` files.
4. **Query layer:** implement §6. Run the equivalence suite (§13) before anything else depends on it.
5. **Transaction layer:** fork/wrap pinned `obsidian-web-mcp`; implement §7; disable/hide every unconditional upstream mutation tool. Two-process stale-edit and simultaneous-create race tests pass through the real MCP transport **before** the service touches the real vault.
6. **MCP unit:** both services under one dedicated account; `VAULT_OAUTH_PASSWORD` set; per-client credentials where supported; audit log outside `/vault`; conflict-file detection and fail-closed mutation gating on.
7. **Ingress:** `cloudflared.service` → `mcp.example.com`; B1 first, B2 fallback; verify fail-closed before the route goes live.
8. **Clients & enforcement (cutover):** add the connector to every agent surface; only after query parity passes, remove local Helios folders from agent workspaces; install the routing instruction (§14) *outside* the vault; update the vault's own `CLAUDE.md` from the local `vault-edit.py` workflow to the MCP-only rule (Dan/an in-vault agent does this part); verify an MCP outage produces a hard stop, not fallback.
9. **Ops:** §11 in full; host-side monitoring per §8.
10. **Git:** §10. Commit job + lock; `.gitignore`; **no remote**.
11. **Host-side git-commit freshness check**, all failure paths exercised, alerting `alerts@example.com`.

## 13. Acceptance tests (definition of done)

**Read/query equivalence** — compare MCP results against local `rg --files`, constrained `find`, and `rg --json` over the same fixture vault: literal + regex patterns, smart case, Unicode, whole words, multiline, spaces in paths, name/path + include/exclude globs, mtime/size filters, context lines, filename-only mode, per-file/total counts, no-match queries, binary/non-UTF-8 files, and **>50 matches across multiple pages** (recombined pages: no duplicates, no omissions, stable order). Mutate a file during a deliberately slow search and prove `changed_during_query`. Test timeout/cancel and symlink/traversal rejection.

**Mutation safety** — through the actual MCP transport: stale-SHA rejection; two-process race on one path (one winner, one clean typed rejection, file never corrupt); simultaneous no-clobber create (exactly one success); guard-violation rejection; batch with one bad hash mutates nothing; post-write reopen/byte-compare catches an induced short write; soft delete lands in `.trash/`.

**Conflict gate** — introduce a synthetic `*(Conflicted copy ...)` file; mutations to original + sibling are blocked and an alert fires.

**Fail-closed** — stop each of sync/MCP/tunnel in turn; agent operations produce hard typed failures; nothing falls back; monitoring notices from outside the CT.

**Backup** — manual `vzdump 106` + `zstd -t` passes (before `.git` exists); CT appears in that night's PBS run.

## 14. Routing instruction to install on agent surfaces at cutover

> Helios has one authoritative agent interface: the `obsidian` MCP server. Use it for every vault read and write. Never use or request a local Helios folder. If MCP is unavailable, stop; do not fall back to local filesystem access.

## 15. Hard prohibitions (recap)

- Vault on NFS — never.
- Exposing any unconditional/last-write-wins mutation tool — never.
- Any local-filesystem fallback path for agents after cutover — never.
- A git remote before Dan closes the credential sweep — never.
- Agents auto-resolving Sync conflict files — never.
- Trusting a success receipt without reopening and byte-comparing — never.
- Secrets in the vault, in this doc, or in git — never.

## 16. What you need from Dan

1. **Obsidian account login** for `ob login` on the CT (interactive), and confirmation of the Sync plan/version-history depth.
2. **Cloudflare access** to the `example.com` zone (tunnel + DNS record + Access app if B2; rate-limit rule).
3. **Fastmail**: create a dedicated app password (SMTP scope); generate `VAULT_OAUTH_PASSWORD`; both go in his password manager.
4. **Confirmations:** CT 106 IP reservation, rootfs/CPU/RAM sizing, startup order, commit-job cadence (suggest: every 15–60 min + on-demand), and the fork's repo location (his GitHub account vs local).
5. **Cutover approval**: the moment agents lose local vault access is his call, made only after §13 passes in full.
6. The vault-side `CLAUDE.md` swap and agent-workspace changes at cutover happen in the vault itself — done by Dan or an agent that still has vault access, not by you.
