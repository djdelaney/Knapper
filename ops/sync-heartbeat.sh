#!/bin/sh
# Touch the sync heartbeat iff continuous sync looks healthy.
# Run by knapper-heartbeat.timer every minute. Silent on success.
#
# ⚠️ VERIFY AT DEPLOY (runbook step): the exact `ob sync-status` output/exit
# semantics for a healthy state. This script currently requires (a) the
# obsidian-headless unit active AND (b) `ob sync-status` exiting 0. If the
# real CLI reports errors with exit 0, tighten the check against its --json
# schema before trusting the gate — the failure mode of a too-loose check is
# mutations proceeding on a dead sync.
set -eu

HEARTBEAT="${1:?usage: sync-heartbeat.sh <heartbeat-file>}"

systemctl is-active --quiet obsidian-headless.service || exit 0
ob sync-status >/dev/null 2>&1 || exit 0

touch "$HEARTBEAT"
