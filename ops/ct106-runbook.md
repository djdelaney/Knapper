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
and locking misbehave).

## 4. Obsidian Sync (brief §5 — both flags load-bearing)

As the knapper user (interactive, needs Dan's Obsidian credentials):

```sh
ob login
ob sync-setup --vault Helios --device-name obsidian-mcp
ob sync-config --conflict-strategy conflict --file-types image,audio,video,pdf,unsupported
ob sync-status --json    # verify before proceeding
```

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
sudo -u knapper /opt/knapper/cli/knapper doctor   # must be all-ok
curl -s 127.0.0.1:3535/health | jq .status        # "ok"
```

Acceptance before ingress: re-run the §13 race tests against the live
service — two-process stale edit, simultaneous create, conflict gate
(synthetic `X (Conflicted copy ...).md`), and each fail-closed path (stop
sync → mutation blocked; stop knapper → hard failure, no fallback).

## 6. Ingress (brief §9 — B2: Cloudflare Access; B1 was dropped with the Python fork)

1. `cloudflared` via Cloudflare's apt repo; tunnel route
   `mcp.example.com → http://127.0.0.1:3535`. No LAN port, no
   port-forward.
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

Following the `pbs-backup-freshness.sh` precedent, silent-on-success, alerts
to `alerts@example.com`:

- `/up` status code via the tunnel (with the monitoring service token) —
  degrades on: vault unreachable, sync unhealthy, rg missing, audit
  unwritable, conflict files present.
- git-commit freshness: last-commit age threshold (a quietly dead commit
  timer is indistinguishable from "nothing changed" from inside).
- Exercise EVERY failure path before trusting a monitor.

## 9. Cutover (brief §12.8 — Dan's call, only after §13 passes)

Add the connector to every agent surface; verify query parity; remove local
Helios folders from agent workspaces; install the routing instruction (brief
§14) outside the vault; swap the vault's own CLAUDE.md to the MCP-only rule
(done in-vault, not from here); verify an outage produces a hard stop.
