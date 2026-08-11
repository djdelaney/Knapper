# CT 106 deployment runbook

The condensed, Knapper-specific build sequence. The authoritative
requirements document is `obsidian-mcp-implementation-brief.md` (referenced
below as "brief") — **read §11's six mail-stack corrections and LXC traps in
full before building; every one is a failure already paid for in this lab.**

Ops runbooks describe how to VERIFY live state, never what it was (house
rule, via Mailvec): when you must record observed state, date it and mark it
observed.

## Map: what exists when

The intended state after each section — a map of the build, not a record of
any run. Two review rounds found defects that were invisible section by
section and obvious against this table (a drill reading `.git` before §7
created it; a step in §5 configuring a unit whose environment §5 had already
captured). **If a section disagrees with this table, one of them is a bug —
resolve it, don't pick a winner.**

| After § | On disk | Running | Reachable | Deviations in force |
|---|---|---|---|---|
| 1 | bare rootfs; **archive #1** (pre-vault, disposable) | — | — | scratch CT 9nn, transient |
| 2 | mail stack | — | — | `MailReport always`, reverted in-section |
| 3 / 3b | rg 15, node, empty `/vault`, persistent journal | — | — | — (first reboot; DNS re-checked) |
| 4 | **`/vault` = Helios, syncing** | `obsidian-headless` | — | — |
| 5 | `/opt/knapper`, units, `_deploy-check/` **(synced)** | + `knapper` :3535, heartbeat timer | loopback | scratch subtree + conflict fixture; services stopped during gates |
| 6 | tunnel config, Access apps 1–2 | + `cloudflared` | prod hostname, loopback | — |
| 7 | **`.git`**, commit stamp, **archive #2** (first with history) | + commit timer | as §6 | scratch CT 9nn again |
| 8 | monitor + conf (on the HOST) | + monitor timer | as §6 | **`MAILTO` → drill address**; `/up` token replaced |
| 8b | smoke unit, disposable vault outside `/vault`, Access app 3 | + `knapper-smoke` :3536 | + smoke hostname | smoke unit/vault/route/app/connector; `LogToolCalls`; page-size override |
| 9 | production | `knapper`, `obsidian-headless`, 2 timers, monitor | prod hostname only | **all of the above must be zero** |

**Peak surface is §8b**: 2 Knappers, 2 hostnames, 2 ports, 3 Access apps, 3
service tokens, 2 vaults, 2 audit logs, 2 archives, 6 live deviations. That
row is what §9's checklist has to reverse in full.

Each identity below has exactly one job. Most of what goes wrong late in this
document is a reference that resolves to the wrong member of a pair:

| Identity | Belongs to | Never |
|---|---|---|
| Root Access app + token (§6.2) | Claude Code and the other connectors | the monitor |
| `/up` path-scoped app + token (§6.4) | the host-side monitor only | the vault surface |
| Smoke app + token (§8b) | the smoke instance on :3536 | production, and gone by §9 |
| `_deploy-check/` (§5) | live-vault acceptance gates — **synced to Dan's devices** | mistaken for isolated |
| `/var/lib/knapper-smoke/vault` (§8b) | the smoke instance — outside `/vault`, unreachable by its unit | inside `/vault` |
| Archive #1 (§1) | proof that restore works | an incident restore |
| Archive #2 (§7) | the only backup containing vault history | pruned before #1 |

## 1. LXC (brief §12.1, §11 traps)

On the Proxmox host:

- **Before `pct create`**: `casesensitivity` is inherited from the parent
  dataset at creation and is immutable afterwards, so check the PARENT —
  `zfs get casesensitivity <parent-dataset>` must read `sensitive`. If it
  does not, fix or choose a different parent and create the CT under that;
  doing this first turns a destroy-and-rebuild into a no-op.
- Debian 13 unprivileged CT, VMID 106, rootfs on `local-zfs`, 2 cores /
  2–4 GB RAM / 16 GB. DHCP reservation first; `onboot: 1`,
  `startup: order=` after the other guests.
- **`nesting=1`** even with no Docker (systemd 254+ needs it for
  `LoadCredential=` tmpfs; without it units fail `243/CREDENTIALS`).
- **Pin DNS**: `pct set 106 --nameserver "<lan-resolver> 1.1.1.1"` — a blank
  field inherits the host's Tailscale MagicDNS, a black hole in a CT.
  Re-check `/etc/resolv.conf` after a reboot.
- **Confirm the rootfs itself came out case-SENSITIVE**, not just its parent:
  `zfs get casesensitivity <rootfs-dataset>` must report `sensitive`. If the
  parent check above was skipped and this reads `insensitive`, the remedy is
  destroy the CT, fix the parent, create again — the property cannot be
  changed in place, and discovering it from a `knapper doctor` failure (§5)
  means doing that with a synced vault already inside.
- **Verify `tmpdir: /var/tmp` in `/etc/vzdump.conf`** on the host (should
  exist from an earlier CT on this host — verify, don't assume), then prove backup BEFORE `.git`
  exists: `vzdump 106 --storage <backup-storage>` + `zstd -t` the artifact.
- If this host ALSO runs a scheduled backup product, that path is a separate
  proof: confirm a snapshot of this CT exists in its datastore after the
  first scheduled run, and that any offsite replication picked it up. A
  guest-selection of "all" is a configuration, not evidence. Also before
  `.git` exists.
- **Prove a RESTORE, not just a backup.** Everything above proves an archive
  exists and is not corrupt; none of it proves anything can be got back out,
  and this CT holds the only copy of vault git history (`.git` never syncs —
  §7). `zstd -t` is an integrity check on a container archive; it says
  nothing about whether the repository inside survived the unprivileged-CT
  `tmpdir` handling. Same principle as the bullet above, one step further
  down the chain: an untested restore is a configuration, not evidence.

  ⛔ **Do not START the restored container.** It carries the same Obsidian
  Sync device identity, the same enabled units and the same MAC as the
  original: boot it and `ob sync --continuous` comes up as a SECOND client,
  with the same device name, pushing an OLDER tree at the live vault — the
  vault-wide rollback hazard §7 warns about, arriving through a door this
  drill opened. (`knapper-commit.timer` and the heartbeat start too, and two
  containers with one MAC collide on the LAN and the DHCP reservation.)
  Inspect it cold, from the host. Use a scratch VMID from a range reserved
  for throwaways (9xx here) — this runs twice, and a collision with a real
  or planned guest is a bad way to learn the numbering.

  **First pass — HERE.** At this point in the build there is no `/vault`
  (created in §3), no vault content (§4) and no `.git` (§7), so this pass
  proves only that the restore MECHANISM works — which is the honest limit
  of what any backup taken now could prove:

  ```sh
  pct restore 9<nn> <archive> --storage <storage>   # needs room for a second rootfs
  pct mount 9<nn>                                   # NOT pct start
  cat /var/lib/lxc/9<nn>/rootfs/etc/os-release      # rootfs is populated…
  cat /var/lib/lxc/9<nn>/rootfs/etc/hostname        # …and is THIS container
  pct unmount 9<nn> && pct destroy 9<nn>
  ```

  **Second pass — after §7**, against a backup taken AFTER `git-init`, which
  is why §7 says to run `vzdump` again first. This is the pass that matters,
  because it is the only one that can see the irreplaceable part:

  ```sh
  pct restore 9<nn> <fresh-archive> --storage <storage>
  pct mount 9<nn>
  git --git-dir=/var/lib/lxc/9<nn>/rootfs/vault/.git log -1
  git --git-dir=/var/lib/lxc/9<nn>/rootfs/vault/.git fsck --no-dangling
  pct unmount 9<nn> && pct destroy 9<nn>
  ```

  `fsck`, not just `log`: `git log` walks commits and prints happily until it
  needs a damaged object, so it passes on a truncated packfile or a missing
  blob — and `vzdump` of a RUNNING CT can capture the moment a `knapper
  commit` was mid-flight. That narrow window is exactly what this drill
  exists to catch. If `fsck` ever gets too slow for the repo's size, the
  cheap floor is `git rev-parse HEAD && git cat-file -e HEAD^{tree}`.

  **A failing `fsck` on pass 2 has two candidate causes**, and the commit
  timer is live by then (§7 starts it before taking that backup): the backup
  path may be broken, or this archive may simply BE the mid-commit capture.
  Distinguish before concluding: stop `knapper-commit.timer`, take another
  `vzdump`, re-run the check. Still failing means the backup path; clean
  means you caught the window, which is worth knowing but is not a backup
  defect — real backups run against a live timer.
  If the container must be booted, remove its network interface AND disable
  `obsidian-headless` in the mounted rootfs before the first start.

  §7 and §9's checklist call the second pass back in, because nobody re-opens
  §1 mid-build.

  **This first archive is disposable, and it is dangerous to keep unlabelled.**
  It restores cleanly, passes every integrity check, and contains no vault
  and no history — so during an incident "restore the backup of 106" can
  select it and succeed at nothing. Once §7's post-`git-init` archive
  verifies, prune this one or mark it in the storage's notes field:
  `pre-git, mechanism proof only`.

## 2. OS baseline (brief §11)

`unattended-upgrades` (Debian-Security only, no auto-reboot), `msmtp` +
`msmtp-mta` + `bsd-mailx` (never `mailutils`), a dedicated Fastmail app password
inline in msmtprc, `set_from_header on` in the defaults block, **NO** to the
AppArmor debconf prompt, log via `syslog LOG_MAIL`. Mail to
`lab@example.com`; prove the unattended-upgrades mail path specifically
(`MailReport "always"` → run → confirm arrival → back to `on-change`).
Test cron entries with `env -i` and cron's real PATH.

`msmtp-mta` is not optional dressing: it is what provides `/usr/sbin/sendmail`,
and `bsd-mailx` DEPENDS on an MTA — install it without staging `msmtp-mta`
first and apt satisfies that dependency by pulling in `exim4`. `mailutils`
stays forbidden for the same reason in reverse: it ships its own MTA and
takes over the sendmail path.

## 3. Runtimes + service user

```sh
apt install git
# ripgrep 15+ from the RELEASE build, NOT apt: Debian 13 ships 14.x, which
# reports "searches": 0 for a query with no matches and so empties the
# scannedFiles evidence behind every "no match" answer. `knapper doctor`
# fails on anything older. Pin the version; never float latest.
RG=15.2.0
curl -sSLf -o /tmp/rg.tar.gz \
  "https://github.com/BurntSushi/ripgrep/releases/download/${RG}/ripgrep-${RG}-x86_64-unknown-linux-musl.tar.gz"
tar xzf /tmp/rg.tar.gz -C /tmp
install "/tmp/ripgrep-${RG}-x86_64-unknown-linux-musl/rg" /usr/local/bin/rg
rg --version                                  # must report 15.x or newer
# Node 22 (NodeSource) for the Obsidian CLI:
npm install -g obsidian-headless@<PINNED>     # pin the version; never float latest
useradd -r -m -d /home/knapper -s /usr/sbin/nologin knapper
mkdir -p /vault /var/lib/knapper/locks /opt/knapper
chown -R knapper:knapper /vault /var/lib/knapper
```

`/vault` on the CT's local rootfs — **NEVER NFS** (house policy; watchers
and locking misbehave), and **only on a case-SENSITIVE filesystem** (ext4
is; per-path lock identity and duplicate detection assume distinct strings
are distinct files — `knapper doctor` fails otherwise). §1 already proved
this for a ZFS rootfs, where it cannot be fixed after the fact.

## 3b. Make the journal persistent (BEFORE the service runs)

Numbered separately because it is a prerequisite with its own verify-and-reboot
loop, not a detail of installing packages — sections are what people skim, and
this one must not be skipped.

Knapper writes no log file. It logs structured JSON to stdout and systemd
routes that to journald, which owns rotation, size caps and retention. The
tool errors agents receive say *"details in the server log"* — that promise
is only as good as the journal's durability, and journald's default
`Storage=auto` keeps logs in RAM ONLY unless `/var/log/journal` exists. On a
fresh CT it does not, so a crash followed by a reboot destroys exactly the
evidence the crash was worth investigating for.

```sh
mkdir -p /var/log/journal
systemd-tmpfiles --create --prefix /var/log/journal
# The drop-in directory does NOT exist on a fresh Debian CT — without this
# the heredoc below dies on redirection and journald keeps its RAM-only
# default while the rest of this section reads as if it were configured.
mkdir -p /etc/systemd/journald.conf.d
cat >/etc/systemd/journald.conf.d/knapper.conf <<'CONF'
[Journal]
Storage=persistent
SystemMaxUse=512M
SystemMaxFileSize=64M
MaxRetentionSec=90day
CONF
systemctl restart systemd-journald
```

**Verify** (do not assume — this is the whole point):

```sh
journalctl --header | grep -i 'file path'   # must be under /var/log/journal
journalctl --disk-usage
systemctl reboot                            # then, after it comes back:
journalctl --boot=-1 | tail                 # last boot's logs still readable
cat /etc/resolv.conf                        # §1's DNS pin, checked at the reboot it warned about
```

That `resolv.conf` read is not incidental: this is the FIRST reboot of the
build, and §1's nameserver pin says to re-check after one. It must still read
`<lan-resolver> 1.1.1.1` — if MagicDNS came back, the first thing to fail is
`ob login` in §4, and it fails in a way that looks like an Obsidian problem.

Knapper does not exist yet at this point, so the previous-boot read above
uses whatever the CT already logs — that is what proves persistence. Repeat
it against the service itself once §5 has it running and it has survived one
reboot: `journalctl --boot=-1 -u knapper | tail`.

Sizing note: `SystemMaxUse` is the cap for the WHOLE journal, shared with
every other unit on the CT, not a per-service quota. 512M against a 16 GB
rootfs leaves ample headroom; raise it if a busy period truncates history
sooner than 90 days.

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
**verify content arrives including `.sh`/`.py` files**. The publish tarball
that puts `ops/` on the CT does not land until §5, so copy this ONE unit
from the repo now — `scp ops/systemd/obsidian-headless.service
root@ct106:/etc/systemd/system/` — then `systemctl daemon-reload` and
`systemctl enable --now obsidian-headless`. §5's bulk copy re-installs this
same file, which is harmless if you left it alone — **but if you edited it**
(a different vault path or service user), re-apply that edit after §5's copy
and reload, or Sync stops and the symptom surfaces sections later. Sync must
be running and healthy before §5: the
heartbeat probe gates mutations on it, and `knapper doctor` fails on a
stale heartbeat.

⚠️ **VERIFY `ops/sync-heartbeat.sh`'s health check against the real
`ob sync-status` output** before trusting the mutation gate — the script
documents the assumption it makes.

## 5. Knapper

Configure `knapper.service` ONCE, here, before anything reads its environment
— every value below is knowable now, and the environment captured further down
is only as good as the unit being finished when it is taken:

- paths, if they differ from the unit's defaults;
- `Mcp__AllowedHosts__0` = the REAL public hostname (the unit ships the
  `mcp.example.com` placeholder). §6.3 re-checks it because that is when it
  starts mattering, not because it is edited then;
- `Sync__MaxAgeSeconds=300` — see the sync-gate bullet below for why it is
  pinned explicitly.

The ONE edit that genuinely cannot happen yet is §6.3's Access block: the AUD
does not exist until the Access app does. That edit carries its own
`daemon-reload` + `restart`, and re-running the `doctor` line below after it
is the cheapest way to confirm the unit still says what you think.

```sh
# on the dev box: ops/publish.sh; scp the tarball
tar -xzf knapper-<v>-linux-x64.tar.gz -C /opt/knapper
cp /opt/knapper/ops/systemd/*.{service,timer} /etc/systemd/system/
$EDITOR /etc/systemd/system/knapper.service     # the three edits listed above
systemctl daemon-reload
# NOT knapper-commit.timer — it runs `knapper commit`, which fails on a vault
# with no .git, and the repo is not created until §7. It starts there.
systemctl enable --now knapper-heartbeat.timer knapper.service
# doctor reads env, not the service unit — so ASK THE UNIT for its environment
# instead of retyping it. A hand-copied list is a second source of truth that
# drifts silently: doctor then passes against a configuration nothing runs.
#
# The case guard is load-bearing: `systemctl show` answers exit 0 with an EMPTY
# string for a unit that is misspelled, missing, or not yet loaded, and `env`
# with no assignments is not an error either — so an unguarded line degrades to
# running doctor against built-in defaults, which may well report all-ok. That
# is the SAME "graded a configuration nothing runs" failure this pattern was
# introduced to remove, wearing different clothes.
#
# NOTE: this reads inline `Environment=` ONLY. Values from an `EnvironmentFile=`
# are read at exec time and never appear in this property, so if these units
# ever gain one, the CLI silently gets a partial environment — and the guard
# below would not catch it. Both call sites (§5 and §7) need revisiting then.
KNAPPER_ENV=$(systemctl show knapper.service -p Environment --value)
echo "$KNAPPER_ENV"     # eyeball it once: is this the config you meant?
case "$KNAPPER_ENV" in
  # Word-splitting is intended; no value contains a space (quote if one ever does).
  # The checks live INSIDE this branch on purpose: /health and `verify` do not
  # read this environment, so on a bad capture they would pass and the run
  # would look clean with doctor never having executed at all.
  *Vault__RootPath=*)
      sudo -u knapper env $KNAPPER_ENV /opt/knapper/cli/knapper doctor &&   # must be all-ok
      curl -s 127.0.0.1:3535/health | jq .status &&                         # "ok"
      sudo -u knapper /opt/knapper/cli/knapper verify --url http://127.0.0.1:3535/ ;;
  *) echo "REFUSING: no usable environment from knapper.service — wrong unit name, or not loaded." \
          "Fix this before continuing; nothing below proves anything without it." >&2 ;;
esac
```

These blocks are written to be pasted into an interactive root shell, so no
guard here calls `exit` — that would close the shell rather than stop the
step. A refusal prints and stops the chain; read the output, do not assume the
absence of red means the checks ran.

`knapper verify` is READ-ONLY by design and stays that way: at this point
`/vault` is already Helios via Sync, so a write test would land real notes on
Dan's devices. From loopback it checks the contract the wire carries — the
13 locked tool names exactly (a partially-registered surface answers
`tools/list` without complaint), the routing instruction, a no-match search
that still reports `scannedFiles > 0` (the live ripgrep-15 evidence check),
a well-formed completeness envelope, whole-file SHAs on reads, and a typed
`[NotFound]` from the mutation surface. It announces the ingress checks it
SKIPPED over loopback; §6 re-runs it through the tunnel, where those are the
point.

Acceptance before ingress — the deployment-specific half, none of which any
repo test can reach. Do it in a scratch subtree (`_deploy-check/`, removed
afterwards through the tools), never against real notes.

⚠️ **This scratch is SYNCED. §8b's is not.** The word covers two different
things in this runbook and only one of them is contained: `_deploy-check/`
lives inside `/vault`, which by §4 is Helios, so the subtree *and* the fake
`X (Conflicted copy 2026-01-01).md` replicate to every device Dan owns while
these gates run — including a phone that is offline now and picks them up
after cleanup. §8b's disposable vault lives outside `/vault` and its unit
cannot even write there. Keep the fixtures few, obviously named, and short-
lived, and confirm at §9 that the DELETION propagated rather than merely
happened on the CT. (The conflict fixture also degrades `/up` vault-wide
while it exists — harmless here because the monitor arrives in §8, but a
reason not to re-run these gates casually afterwards.)

- **Sync gate**: `systemctl stop obsidian-headless` → within
  `Sync__MaxAgeSeconds` a mutation must fail `[MutationBlocked]`; restart and
  confirm it clears. This is the only test of `sync-heartbeat.sh`'s
  assumption about real `ob sync-status` output.

  That window is the fail-closed budget, which is why it was pinned
  explicitly in the unit above rather than left to `appsettings.json`: nobody
  reading `knapper.service` could otherwise see how long sync may be dead
  while writes still land. With the heartbeat timer at 60s, the honest
  statement is "up to ~5 minutes of dead sync is invisible to agents" — and
  this test has a number to wait for instead of a guess.
- **Conflict gate**: create `X (Conflicted copy 2026-01-01).md` beside a
  scratch note; mutations to BOTH must be refused until it is removed.
  Prefer a conflict file Sync itself produced if one has appeared.
- **No fallback**: `systemctl stop knapper` → the client hard-fails; confirm
  no agent silently reaches the filesystem instead.
- **Two-process write races** (stale edit, simultaneous create) belong to
  §8b's disposable-vault session, not here — the repo's acceptance tier
  already proves them over the same wire against the same binary, and here
  the blast radius is Helios.

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
   `Mcp__Access__TeamDomain`, `Mcp__Access__Audience` (the Access app AUD) —
   then `systemctl daemon-reload && systemctl restart knapper`. This is the
   one unit edit §5 could not fold in, because the AUD does not exist until
   the Access app does; re-run §5's `doctor` line afterwards so the captured
   environment matches the unit again. The server refuses to start if it
   cannot fetch the signing keys — that refusal is the feature. **`Mcp__AllowedHosts__0` must already be the
   real public hostname**: a tunneled request keeps that hostname, and the
   DNS-rebinding guard rejects every Host it does not recognize — with the
   shipped `mcp.example.com` placeholder still in place, ingress comes up
   and then refuses all of it. Verify through the tunnel, not from the CT:
   a loopback `curl` passes the guard no matter what this is set to.
4. Second path-scoped Access app for `/up` → external monitor. Its AUD goes
   in `Mcp__Access__MonitoringAudience`, which is accepted on `/up` and
   nowhere else, and it must be a genuinely SEPARATE application: startup
   refuses when it equals `Mcp__Access__Audience`, because equal AUDs give
   the monitoring token the whole vault surface while the config still reads
   like a path-scoped restriction.

   Leaving `MonitoringAudience` empty is the **single-app setup**, and it is
   a downgrade with a name rather than a neutral choice: the monitor then
   authenticates with the main app's token, so a credential living in a
   config file on ANOTHER machine can read and mutate the whole vault. Two
   apps is the default; take one only deliberately.
5. **Re-run the verifier through the tunnel** — from the dev box or the
   Proxmox host, NOT the CT (a loopback URL skips every ingress check):

   ```sh
   CF_ACCESS_CLIENT_ID=<token>.access CF_ACCESS_CLIENT_SECRET=<secret> \
   CF_MONITOR_CLIENT_ID=<monitor-token>.access CF_MONITOR_CLIENT_SECRET=<monitor-secret> \
     knapper verify --url https://mcp.example.com/
   ```

   Now the skipped checks do the work: unauthenticated callers refused on
   both `/up` and the MCP endpoint, `/health` 404 from outside, `/up`
   answering 200 with booleans only, and — with the monitor token supplied —
   that credential being refused at the vault surface.

   **Read the `skip` lines, not just the red ones.** A missing `CF_MONITOR_*`
   pair does not fail the monitoring-token check, it SKIPS it, and a skimmer
   looking for red sees a clean run that proved nothing about the credential
   asymmetry. In a two-app deployment every line must read `ok` and exit must
   be 0; the only acceptable `skip` is the single-app one from §6.4, and only
   if that was a deliberate choice.
6. Watch for the redirect-URI / DCR failure class during claude.ai
   connector setup (bit this homelab's previous MCP go-live).
7. **Nothing may mutate the live vault until §7 completes.** Ingress is up
   from here, but the repo does not exist yet, so any write landing in this
   window is outside git history permanently — history begins at `git-init`.
   Nothing in §6.5 or §8b writes to Helios by design (the verifier is
   read-only, §8b uses a disposable copy); this line makes that a constraint
   rather than a happy accident. Connect no agent surface **to the live
   vault** before §9 — §8b does attach a real connector, pointed at a
   disposable copy, and that distinction is the whole of what makes it safe.

## 7. Git (brief §10 — after backup is proven)

```sh
# Same rule as §5, same guard for the same reason: take the environment from the
# unit that will run this job — an empty expansion here would `git-init` in
# whatever directory the binary falls back to.
CLI_ENV=$(systemctl show knapper-commit.service -p Environment --value)
case "$CLI_ENV" in
  *Vault__RootPath=*) ;;
  *) echo "REFUSING: no usable environment from knapper-commit.service" >&2; exit 1 ;;
esac
# shellcheck disable=SC2086
sudo -u knapper env $CLI_ENV /opt/knapper/cli/knapper git-init
# Seed the monitor's freshness stamp with one commit BY HAND, then start the
# timer. In that order: `knapper commit` fails on a vault with no .git, and
# §8's staleness alert is undefined until the stamp exists at all.
sudo -u knapper env $CLI_ENV /opt/knapper/cli/knapper commit
systemctl enable --now knapper-commit.timer
```

**Take a FRESH backup, then run §1's restore drill a second time.** The only
archive that exists so far predates the OS baseline, the runtimes, Sync and
git, so restoring it and looking for `.git` would report a failure that isn't
one:

```sh
vzdump 106 --storage <backup-storage>      # the first archive containing .git
```

Then §1's second-pass commands against THAT archive. Cold inspection only — a
booted restore is a second Sync client with this CT's device identity, holding
an older tree. This is the pass that proves the backup carries the only copy
of vault history; the first proved nothing more than that restore works.

That environment is deliberately smaller than §5's, and the difference is not
an omission: `GitCommitJob` takes neither the audit log nor the sync gate, so
`knapper commit` writes NO audit entry (the commit is its own record) and is
NOT gated on sync health. Root path, lock directory and the commit stamp are
the whole of what it reads — which is exactly what `knapper-commit.service`
carries.

- Local-only. **NO remote until Dan closes the credential sweep.**
- `knapper commit` takes the vault-wide lock and refuses credential-shaped
  content (the pre-commit scan). Every SUCCESSFUL run — including "nothing to
  commit" — fsync-touches the stamp §8 watches; refused and failed runs do
  not, which is what makes a wedged job visible.
- Never `git checkout`/`reset` against the live tree — Sync propagates the
  revert vault-wide.
- `pct snapshot 106 pre-upgrade-YYYYMMDD` before any bump (`pct` takes the
  VMID, not the CT's name).
- **Rehearse the rollback MECHANICS once, before cutover**, on the same
  principle §8 applies to alerts: an unexercised recovery path is not a
  trusted one, and this one would otherwise first run during an incident. On
  a first deployment there is no previous release to roll back TO, so be
  honest about what this proves: keep the shipped tarball on the CT, extract
  that same artifact over `/opt/knapper`, `systemctl restart knapper`,
  confirm `knapper verify --url` still passes, and note how long the whole
  thing took. It proves the artifact is on disk and readable, that extraction
  does not break ownership or modes, and that the service comes back — which
  is most of what fails during a real rollback.

  What it cannot prove is a VERSION downgrade, because there is no earlier
  version yet. That belongs to the first real upgrade: snapshot, install,
  verify, and if verify fails, roll back to the retained previous tarball and
  make `knapper verify --url` passing the acceptance criterion for the
  rollback itself. Deferring silently is the only bad answer here; deferring
  in writing is fine.

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

Host prerequisites: `jq`, `curl`, and `pct` (so it runs as root on the
Proxmox host), plus a mailer — and the mailer needs the same care as §2, on a
DIFFERENT machine with a different package set. The script prefers
`sendmail -t`, falls back to `mail`, and refuses to run when neither exists,
so `--test` failing is diagnosable. **`mailutils` is forbidden here too**, and
for a sharper reason than in the CT: it bundles its own MTA and takes over
`/usr/sbin/sendmail` for the whole host, breaking unrelated monitors that
have nothing to do with this project. Reach for `msmtp` + `msmtp-mta`, or
`bsd-mailx`.

Alert cadence: one mail when the failure SET changes, a reminder at most once
per `RENOTIFY_SECONDS` (default 24h) while it is unchanged, and exactly one
mail on recovery. This matters most for the conflict-file case — `/up` stays
503 until a HUMAN reconciles, which is days, and a mail every five minutes
for three days is how a monitor gets filtered into a folder nobody reads. The
exit code still reflects live state on every run, so `systemctl` and the
journal never see the suppression. Because the unit exits non-zero for as
long as the failure lasts, confirm the host does not mail on unit failure and
that `knapper-monitor.service` has no `OnFailure=` handler — otherwise the
flood the cadence just removed comes back through systemd instead, and the
suppression looks broken when it is being bypassed.

**Which variable carries which mail**, because the two are not
interchangeable: `TEST_MAILTO` is consulted by `--test` and nothing else.
The drills below are REAL induced failures, so they travel the normal alert
path — `MAILTO` — and routing them away from the live alert stream means
editing `MAILTO` in the config for the duration. That edit is the risk in
this whole section (see the closing checklist in §9): a monitor left pointed
at the drill address keeps running, keeps exiting non-zero, keeps looking
healthy in `systemctl`, and mails every genuine alert to a mailbox nobody
reads — a silent indefinite failure introduced by the procedure that tests
for silent indefinite failures.

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

Exercise EVERY failure path before trusting the monitor — one mail each, then
one recovery mail as each is restored. Run them in this order, because the
last one does not restore cleanly:

1. `systemctl stop knapper` → restart.
2. `systemctl stop cloudflared` → restart.
3. `systemctl stop knapper-commit.timer` → **wait out `MAX_STAMP_AGE`**
   (3900s ≈ 65 min by default) before expecting the mail: the stamp has to
   age past the threshold, and "stop it, expect a mail" would read as a
   monitor failure for the first hour. Restart the timer, then run
   `knapper commit` by hand so the stamp is fresh again and the recovery mail
   fires without another hour's wait.
4. Revoke the **monitoring** token LAST, because a revoked Cloudflare token
   is not restored, it is **replaced**: issue a new one on the path-scoped
   `/up` app from §6.4 and write it into `/etc/knapper-monitor.conf` (chmod
   600, on the host). Until it is, the monitor keeps alerting, which looks
   exactly like the drill having broken something — hence doing it last.

   **Claude Code is unaffected by this drill.** It holds the ROOT app's token
   from §6.2; the monitor holds the `/up` app's. Two apps, two tokens, and
   that separation is precisely what §6.4 buys — treating them as one is the
   belief §6.4 exists to prevent. (Revoking the root token instead is the
   mirror image: the connector needs the new value and the monitor does not.)

Then the **positive control**, which is what actually closes this section:
restore `MAILTO` to the real alert address, induce ONE more failure, confirm
it arrives THERE, and clear it. The drills prove failures are detected; only
this proves they still reach the people who act on them. §6 applies the same
rule to the denial matrix — a refusal test that never confirms the allow case
proves nothing about the allow case.

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

**Where this instance runs.** A SECOND Knapper on the same CT, on its own
port, with its own environment — never the live `knapper.service` repointed
at a copy. Repointing means the live vault has no server for the duration and
the host-side monitor alerts throughout §8b, which is how an operator learns
to ignore it; and "point it back afterwards" is one more untracked temporary
change in a section that already has three. Install
`ops/systemd/knapper-smoke.service.example` as `knapper-smoke.service` and
read its header — it carries the whole configuration, including why the copy
lives OUTSIDE `/vault` (anything inside is Helios, and Sync would propagate
it). Reaching it from a real client means one more tunnel route and Access
app for the smoke hostname; that is the cost of not touching production, and
it is worth it. A second CT restored from §7's backup also works if you
prefer isolation over convenience — disable `obsidian-headless` in the
mounted rootfs BEFORE first start (§1's warning applies in full).

**The smoke instance's own gates must be OPEN, or it fails its own tests.**
Two of them would otherwise refuse every mutation, and §8b's outcome rule
would send you off to rewrite tool descriptions over it:

- **Sync gate**: nothing syncs the disposable copy and nothing touches a
  heartbeat for it, so `Sync__Mode=heartbeat` would start returning
  `[MutationBlocked]` after `Sync__MaxAgeSeconds` — failing tests 2 and 4,
  which are both mutations, in a way that reads as the MODEL misbehaving.
  The example unit sets `Sync__Mode=open` for exactly this reason; that is
  the one deployment where the dev-only opt-out is correct, because the gate
  is protecting a vault that does not exist here.
- **Conflict gate**: a single `* (Conflicted copy ...)*` name anywhere in the
  seeded corpus refuses every smoke mutation before test 1 runs. Do not seed
  one, and do not copy §5's `_deploy-check/` fixtures into this vault.

**Setup.** That instance over a DISPOSABLE COPY of the vault (never Helios
itself), `Mcp__LogToolCalls=true`, fresh audit log + metrics file, and for
test 1 set `Vault__MaxResultsPerPage=25` — then **restore it to the
production value before test 2**, so tests 2–4 exercise the page size agents
will actually meet (test 4 reads counts and files modes, where a 25-item page
would change what the log shows). Every one of these settings lives in the
smoke unit, so tearing that unit down at §9 retires all of them at once —
which is the point of keeping them out of `knapper.service`. Attach one real agent surface
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
descriptions, server instructions, or the brief's §14 routing instruction and
re-run; the contract itself does not move to accommodate the model
(house rule). Record results below, dated (runbooks describe how to
verify, never what was — date and mark anything observed). Re-run this
section after major Claude model updates (steering behavior drifts) AND
after any change to the tool surface — a renamed or added tool invalidates
test 4's routing evidence exactly as thoroughly as a new model does.

- [ ] _no runs recorded yet_

## 9. Cutover (brief §12.8 — Dan's call, only after the brief's §13 and §8b pass)

Add the connector to every agent surface; verify query parity; remove local
Helios folders from agent workspaces; install the routing instruction (brief
§14) outside the vault; swap the vault's own CLAUDE.md to the MCP-only rule
(done in-vault, not from here); verify an outage produces a hard stop.

**Restore every temporary setting before calling this done.** Each was
introduced by a test several sections apart from every other, and each fails
silently if left:

- [ ] `MailReport` back to `on-change` (§2)
- [ ] `MAILTO` back to the real alert address, proven by the §8 positive
      control — not just edited back
- [ ] monitoring service token in `/etc/knapper-monitor.conf` AND in Claude
      Code's config is the CURRENT one, if the §8 revocation drill ran
- [ ] `knapper-smoke.service` stopped and REMOVED, `/var/lib/knapper-smoke`
      deleted, its tunnel route and Access app torn down (§8b) — that single
      teardown is what retires `Mcp__LogToolCalls=true`,
      `Vault__MaxResultsPerPage=25` and the disposable vault path together
- [ ] `knapper.service` never acquired any §8b setting — grep it for
      `LogToolCalls` and `MaxResultsPerPage` and expect nothing
- [ ] §1's restore drill run a SECOND time after §7, cold-mounted and
      `fsck`-checked, and both scratch CTs destroyed
- [ ] `_deploy-check/` and its conflict fixture removed through the tools
      (§5) AND the deletion confirmed on a second device — it is inside
      Helios, so "gone on the CT" is not gone
- [ ] the smoke CONNECTOR entry removed from the client (§8b) — a configured
      connector pointing at a dead hostname invites someone to later "fix"
      it by aiming it at production
- [ ] the shipped tarball is still on the CT, readable, for the first real
      upgrade's rollback (§7's rehearsal leaves you on current by
      construction — there is no earlier version to be stranded on)

**If cutover is abandoned**, the two irreversible-feeling steps are not: put
the local Helios folders back in the agent workspaces that had them, and
restore the vault's own CLAUDE.md from git history (`knapper commit` has been
snapshotting since §7, which is what makes this a revert rather than a
reconstruction). Reverse them in that order, then stop the connector — never
the other way round, or agents lose both paths at once.

Retrieve that file **from the host, not through the MCP** — the likeliest
reason to be abandoning cutover is that the MCP, the tunnel, or Access is not
working, and a recovery path that runs through the broken surface is no
recovery path:

```sh
pct exec 106 -- git -C /vault show <ref>:CLAUDE.md
```

Same virtue as §1's restore drill, which is host-side end to end.
