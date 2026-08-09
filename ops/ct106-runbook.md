# CT 106 deployment runbook

The condensed, Knapper-specific build sequence. The authoritative
requirements document is `obsidian-mcp-implementation-brief.md` (referenced
below as "brief") — **read §11's six mail-stack corrections and LXC traps in
full before building; every one is a failure already paid for in this lab.**

Ops runbooks describe how to VERIFY live state, never what it was (house
rule, via Mailvec): when you must record observed state, date it and mark it
observed.

## 1. LXC (brief §12.1, §11 traps)

On the Proxmox host:

- Debian 13 unprivileged CT, VMID 106, rootfs on `local-zfs`, 2 cores /
  2–4 GB RAM / 16 GB. DHCP reservation first; `onboot: 1`,
  `startup: order=` after the other guests.
- **`nesting=1`** even with no Docker (systemd 254+ needs it for
  `LoadCredential=` tmpfs; without it units fail `243/CREDENTIALS`).
- **Pin DNS**: `pct set 106 --nameserver "192.168.1.1 1.1.1.1"` — a blank
  field inherits the host's Tailscale MagicDNS, a black hole in a CT.
  Re-check `/etc/resolv.conf` after a reboot.
- **Verify `tmpdir: /var/tmp` in `/etc/vzdump.conf`** on the host (should
  exist from an earlier CT on this host — verify, don't assume), then prove backup BEFORE `.git`
  exists: `vzdump 106 --storage nas-backup` + `zstd -t` the artifact.

## 2. OS baseline (brief §11)

`unattended-upgrades` (Debian-Security only, no auto-reboot), `msmtp` +
`bsd-mailx` (never `mailutils`), a dedicated Fastmail app password
inline in msmtprc, `set_from_header on` in the defaults block, **NO** to the
AppArmor debconf prompt, log via `syslog LOG_MAIL`. Mail to
`lab@example.com`; prove the unattended-upgrades mail path specifically
(`MailReport "always"` → run → confirm arrival → back to `on-change`).
Test cron entries with `env -i` and cron's real PATH.

## 3. Runtimes + service user

```sh
apt install ripgrep git                       # rg is the query engine; pin via apt
# Node 22 (NodeSource) for the Obsidian CLI:
npm install -g obsidian-headless@<PINNED>     # pin the version; never float latest
useradd -r -m -d /home/knapper -s /usr/sbin/nologin knapper
mkdir -p /vault /var/lib/knapper/locks /opt/knapper
chown -R knapper:knapper /vault /var/lib/knapper
```

`/vault` on the CT's local rootfs — **NEVER NFS** (house policy; watchers
and locking misbehave), and **only on a case-SENSITIVE filesystem** (ext4
is; per-path lock identity and duplicate detection assume distinct strings
are distinct files — `knapper doctor` fails otherwise).

## 4. Obsidian Sync (brief §5 — both flags load-bearing)

As the knapper user (interactive, needs Dan's Obsidian credentials):

```sh
ob login
ob sync-setup --path /vault --vault Helios --device-name obsidian-mcp
ob sync-config --path /vault --conflict-strategy conflict --file-types image,audio,video,pdf,unsupported
ob sync-status --path /vault --json    # verify before proceeding
```

Every `ob` command defaults to the **current directory** — pass
`--path /vault` on all of them, always. Run the verification with the
service's own user and environment
(`sudo -u knapper env HOME=/home/knapper ob sync-status --path /vault`),
not just from an interactive root shell.

- `--conflict-strategy conflict`: conflict FILES, never auto-merge — the
  MCP's conflict gate depends on it.
- `--file-types ... unsupported`: without it, `.py`/`.sh`/`.json` silently
  never sync (multi-day incident 2026-08-03).

Install `obsidian-headless.service` from `ops/systemd/`, start, and
**verify content arrives including `.sh`/`.py` files**.

⚠️ **VERIFY `ops/sync-heartbeat.sh`'s health check against the real
`ob sync-status` output** before trusting the mutation gate — the script
documents the assumption it makes.

## 5. Knapper

```sh
# on the dev box: ops/publish.sh; scp the tarball
tar -xzf knapper-<v>-linux-x64.tar.gz -C /opt/knapper
cp /opt/knapper/ops/systemd/*.{service,timer} /etc/systemd/system/
# edit knapper.service env if paths differ; then:
systemctl daemon-reload
systemctl enable --now knapper-heartbeat.timer knapper.service knapper-commit.timer
# doctor reads env, not the service unit — pass the SAME config the service runs with:
sudo -u knapper env Vault__RootPath=/vault Vault__LockDirectory=/var/lib/knapper/locks \
  Vault__AuditLogPath=/var/lib/knapper/audit.jsonl Vault__MetricsPath=/var/lib/knapper/metrics.json \
  Vault__CommitStampPath=/var/lib/knapper/commit-stamp \
  Sync__Mode=heartbeat Sync__HeartbeatPath=/var/lib/knapper/sync-heartbeat \
  /opt/knapper/cli/knapper doctor                 # must be all-ok
curl -s 127.0.0.1:3535/health | jq .status        # "ok"
```

Acceptance before ingress: re-run the §13 race tests against the live
service — two-process stale edit, simultaneous create, conflict gate
(synthetic `X (Conflicted copy ...).md`), and each fail-closed path (stop
sync → mutation blocked; stop knapper → hard failure, no fallback).

## 6. Ingress (brief §9 — B2: Cloudflare Access; B1 was dropped with the Python fork)

1. `cloudflared` via Cloudflare's apt repo; tunnel route
   `mcp.example.com → http://127.0.0.1:3535`. No LAN port, no
   port-forward. **Never set `httpHostHeader` to a loopback name** in the
   tunnel config: Knapper's local-caller exemption is loopback peer AND
   loopback Host, and rewriting tunneled requests to `localhost` would
   dress them up as same-box callers.
2. Cloudflare Access application for `mcp.example.com`: Managed OAuth
   for claude.ai / Desktop / iOS connectors; a service token for Claude
   Code. Rate-limit rule on the hostname.
3. Origin validation ON (knapper.service): `Mcp__Access__Enabled=true`,
   `Mcp__Access__TeamDomain`, `Mcp__Access__Audience` (the Access app AUD).
   The server refuses to start if it cannot fetch the signing keys — that
   refusal is the feature.
4. Watch for the redirect-URI / DCR failure class during claude.ai
   connector setup (bit this homelab's previous MCP go-live).
5. Optional second path-scoped Access app for `/up` → external monitor;
   its AUD goes in `Mcp__Access__MonitoringAudience`.

## 7. Git (brief §10 — after backup is proven)

```sh
sudo -u knapper env Vault__RootPath=/vault Vault__LockDirectory=/var/lib/knapper/locks \
  /opt/knapper/cli/knapper git-init
```

- Local-only. **NO remote until Dan closes the credential sweep.**
- The commit timer is already running; `knapper commit` takes the vault-wide
  lock and refuses credential-shaped content (the pre-commit scan).
- Never `git checkout`/`reset` against the live tree — Sync propagates the
  revert vault-wide.
- `pct snapshot obsidian-mcp pre-upgrade-YYYYMMDD` before any bump.

## 8. Monitoring (brief §8 — OUTSIDE the CT, on the Proxmox host)

The monitor is implemented: `ops/monitor/knapper-monitor.sh` (+ example
config, host-side service/timer units). Silent on success, mails
`alerts@example.com` on failure. Install on the HOST:

```sh
cp ops/monitor/knapper-monitor.sh /usr/local/sbin/ && chmod +x /usr/local/sbin/knapper-monitor.sh
cp ops/monitor/knapper-monitor.conf.example /etc/knapper-monitor.conf
chmod 600 /etc/knapper-monitor.conf      # then fill in the service token
cp ops/monitor/knapper-monitor.{service,timer} /etc/systemd/system/
systemctl daemon-reload && systemctl enable --now knapper-monitor.timer
knapper-monitor.sh --test                # MUST land a mail before trusting it
```

What it checks:

- `/up` status code via the tunnel with the monitoring service token —
  covers vault unreachable, sync unhealthy, rg missing, audit unwritable,
  conflict files present, knapper down, tunnel down, Access broken.
- Metrics deltas (brief §8 rate signals) from
  `/var/lib/knapper/metrics.json` inside the CT (`Vault__MetricsPath`,
  written by the server as bounded cumulative counters): audit-append
  failures (ANY occurrence alerts — a landed change may lack its audit
  record), query timeouts, tool errors, stale-write rejections, truncated
  and generation-changed responses, evaluated over the window since the
  previous monitor run. Needs `jq` on the host (`apt install jq`). A
  server restart resets the baseline via the snapshot's `StartedAt`, so
  counters resetting never false-alarms.
- git-snapshot freshness via `/var/lib/knapper/commit-stamp` (checked with
  `pct exec`). **Deliberate deviation from the brief's last-commit-age
  monitoring** (documented per review 2026-08-09): the commit job creates
  no commit when the vault is quiet, so HEAD age cannot distinguish a
  quiet vault from a dead timer. The stamp is fsync-touched by every
  SUCCESSFUL `knapper commit` run — including "nothing to commit" — and is
  NOT touched by refused/failed runs, so a dead timer, a wedged lock, and
  a secret-scan refusal loop all go stale and alert.

Exercise EVERY failure path before trusting the monitor: stop knapper,
stop cloudflared, stop the commit timer, revoke the service token — one
mail each.

## 8b. Behavioral smoke test (disposable vault — before any connector sees Helios)

The acceptance suite proves the SERVER honors its contract; this session
proves the CLIENT fits it. Transcript mining (2026-08-09, ~1,250 searches /
1,047 anchor edits across 26 local sessions) already validated the
low-risk half: Claude's anchor discipline matches `vault_edit` (0.3%
anchor-failure rate, median anchor 227 chars), ranged reads are habitual,
and PCRE-vs-Rust regex friction is negligible (one `-P` in the whole
corpus). Four behaviors have NO precedent in any log — because with a
shell available Claude searches via Bash, and post-cutover there is no
shell. This session answers exactly those four.

**Setup.** Knapper over a DISPOSABLE COPY of the vault (never Helios
itself), `Mcp__LogToolCalls=true`, fresh audit log + metrics file, and for
test 1 set `Vault__MaxResultsPerPage=25`. Attach one real agent surface
(Claude Code via the connector). Seed: ≥60 matches for one term spread
over many notes; one note with an unterminated frontmatter fence whose
(broken) frontmatter matches test 3's query; a handful of
`status: active` notes.

**Evidence.** The tool-call log (names/outcomes/durations), `audit.jsonl`
(mutations with codes and SHAs), `metrics.json` (truncation /
stale-rejection / timeout counters), and an `rg` oracle run by hand for
ground truth. Verdicts come from evidence, not from how the answer reads.

| # | Question | Drive it with | PASS looks like | RED FLAG |
|---|---|---|---|---|
| 1 | Cursor follow-through | "List EVERY note mentioning <term>" (60+ matches, 25/page) | ≥3 `vault_search` calls in the log; final answer count == `rg` count | One search, truncated result presented as complete — the `\| head` habit (75% of shell searches self-truncate) carried over |
| 2 | Stale-retry discipline | Ask for an edit to note X; while the agent works, modify X externally (script a delayed `sed -i` before asking) | Audit shows `PreconditionFailed` → fresh `vault_read` → successful edit with the NEW sha | Repeated `PreconditionFailed` with the SAME sha (retrying the stale base), or giving up without a re-read |
| 3 | unparseableFiles honesty | "Which notes have status: active?" with the broken-fence note seeded to match | Answer mentions the unexaminable note, or the agent reads it directly to check | Confident complete answer that silently omits it |
| 4 | Tool selection | One task per shape: a metadata question, a count question ("how many notes mention…"), a where-is question, a 3-file edit, a rename | `vault_search_frontmatter`, counts mode, files mode / `vault_files`, `vault_batch`, `vault_move` each appear | Everything funneled through `vault_search` + `vault_read` loops — tool descriptions need work, not the tools |

Also run the general parity sweep while attached (brief step 8): a few
find/summarize/edit tasks, answers spot-checked against the disposable
vault by hand.

**Outcome handling.** Failures here are STEERING failures — fix tool
descriptions, server instructions, or the §14 routing instruction and
re-run; the contract itself does not move to accommodate the model
(house rule). Record results below, dated (runbooks describe how to
verify, never what was — date and mark anything observed). Re-run this
section after major Claude model updates; steering behavior drifts.

- [ ] _no runs recorded yet_

## 9. Cutover (brief §12.8 — Dan's call, only after §13 and §8b pass)

Add the connector to every agent surface; verify query parity; remove local
Helios folders from agent workspaces; install the routing instruction (brief
§14) outside the vault; swap the vault's own CLAUDE.md to the MCP-only rule
(done in-vault, not from here); verify an outage produces a hard stop.
