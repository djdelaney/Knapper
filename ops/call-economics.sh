#!/bin/sh
# How many ROUND TRIPS is the client spending to do its vault work?
#
#   pct exec 106 -- sh /opt/knapper/ops/call-economics.sh [DAYS] [--all-clients]
#                                                          [--show-identities]
#
# Runs INSIDE CT 106 — it reads this unit's journal, which is the only
# telemetry that covers every client surface. Cowork, Claude Desktop, mobile
# and claude.ai sessions leave nothing on the operator's disk, so client-side
# transcript mining can only ever see Claude Code. This can see all of them.
#
# WHY THIS EXISTS. Measured 2026-08-24: server-side work averages 12.2ms while
# the client-observed round trip is ~2.9-3.6s, so >99% of "the vault feels
# slow" is relay latency nothing here can influence. The only lever is how
# many round trips the client spends, which makes CALL COUNT — not duration —
# the number worth tracking over time. Program.cs's CALL ECONOMICS
# instructions are the intervention; this is how you tell whether they worked.
#
# ⛔ RATIOS ARE THE COMPARISON, NOT COUNTS. Two windows never carry the same
# amount of work, so "fewer calls this week" measures how busy the week was,
# not how efficiently it was spent. Read the RATIOS block first: batch share
# and reads-per-mutation move only when client behaviour moves. The counts are
# context for judging whether the sample is big enough to mean anything.
#
# The verify probe is EXCLUDED by default. `knapper verify --url` spends a
# fixed 4 calls per run from a service-token identity (files/read/search, then
# a vault_edit expected to return NotFound — Verify.cs). It is machine
# traffic with a fixed shape, so leaving it in dilutes every ratio by an
# amount that depends on how often an operator happened to run verify. Pass
# --all-clients to see it anyway; the client breakdown always prints.
#
# Client identities in that breakdown are MASKED — this report is made to be
# pasted, and reading it should not be what discloses an owner email or an
# Access service-token client id. --show-identities prints them verbatim.
#
# Comparing across a deployment: pass a window that does NOT straddle the
# restart, or the "after" sample is half old-instructions traffic. Server
# instructions reach a client at initialize, so existing sessions keep the old
# text until they reconnect — a same-day "after" measures neither state.
set -eu

DAYS="${1:-7}"
case "$DAYS" in *[!0-9]*) DAYS=7 ;; esac
ALL_CLIENTS=0
SHOW_IDENTITIES=0
for arg in "$@"; do
    [ "$arg" = "--all-clients" ] && ALL_CLIENTS=1
    [ "$arg" = "--show-identities" ] && SHOW_IDENTITIES=1
done

command -v jq >/dev/null 2>&1 || { echo "call-economics: jq not found" >&2; exit 2; }

# Category+Outcome, never a substring match on the message: an exception whose
# STACK mentions ToolSupport also contains the string, and those rows have no
# tool name — they silently became blank entries the first time this was done
# by hand.
journalctl -u knapper --since "${DAYS} days ago" -o json \
| jq -r '(.MESSAGE|fromjson?) as $m
    | select($m.Category=="Knapper.Mcp.Tools.ToolSupport" and $m.State.Outcome!=null)
    | [(.__REALTIME_TIMESTAMP|tonumber/1000000), $m.State.Tool, $m.State.ElapsedMs,
       $m.State.Outcome, $m.State.Client] | @tsv' \
| awk -F'\t' -v days="$DAYS" -v allc="$ALL_CLIENTS" -v showids="$SHOW_IDENTITIES" '
# Client identities are MASKED in the report. This block exists so an operator
# can see which clients were counted and which were excluded, and a prefix
# answers that — but the full strings are an owner email and a Cloudflare
# Access service-token client id, and this output is made to be pasted into an
# issue, a commit message or a chat. Reading it should not be the thing that
# discloses them. The prefix is long enough to tell two clients apart and the
# kind tag preserves what the exclusion rule keyed on. --show-identities
# prints them verbatim for the one case that needs it: an unrecognised client.
function maskclient(c,   at, local) {
    if (showids) return c
    at = index(c, "@")
    if (at > 0) {
        local = substr(c, 1, at - 1)
        return substr(local, 1, 3) (length(local) > 3 ? "…" : "") "@… (user)"
    }
    return substr(c, 1, 8) "… (service token)"
}
{
    allcalls++; clients[$5]++
    # The probe identity is a service token (Access common_name / sub), never
    # an email. Match on that rather than a hardcoded token id, which would
    # rot the first time the token is rotated.
    isprobe = ($5 !~ /@/)
    if (isprobe && !allc) { probeskipped++; next }

    n++; tool[$2]++; out[$4]++; ms += $3; if ($3+0 > mx) mx = $3+0
    if (n > 1) {
        d = $1 - prev
        # <1s apart cannot contain model inference: concurrent dispatch.
        # >20s is a human thinking or a session boundary, not a round trip.
        if (d < 1) burst++
        else if (d < 20) { seq++; seqsum += d }
        else idle++
    }
    prev = $1
}
END {
    if (n == 0) { print "no tool calls in window"; exit }
    reads   = tool["vault_read"];  breads = tool["vault_batch_read"]
    muts    = tool["vault_edit"] + tool["vault_append"] + tool["vault_create"]
    batches = tool["vault_batch"]

    printf "== CALL ECONOMICS · %d-day window ==\n", days
    printf "calls %d   server mean %.1fms   max %dms\n", n, ms/n, mx
    if (probeskipped) printf "(%d verify-probe calls excluded; --all-clients to include)\n", probeskipped

    print  ""
    print  "-- RATIOS (compare these across windows) --"
    if (reads + breads > 0)
        printf "  batched-read share      %5.1f%%   (%d batch_read vs %d read)\n", \
               100*breads/(reads+breads), breads, reads
    if (muts + batches > 0)
        printf "  batched-mutation share  %5.1f%%   (%d batch vs %d single)\n", \
               100*batches/(muts+batches), batches, muts
    if (muts + batches > 0)
        printf "  reads per mutation      %5.2f    (fresh-read rule floors this near 1.00)\n", \
               (reads+breads)/(muts+batches)
    if (n > 1) {
        printf "  concurrent (<1s apart)  %5.1f%%   (%d calls)\n", 100*burst/(n-1), burst
        if (seq > 0)
            printf "  sequential hops         %5.1f%%   (%d calls, mean %.1fs apart)\n", \
                   100*seq/(n-1), seq, seqsum/seq
    }
    if (seq > 0)
        printf "  relay wait in window    %5.1f min  (%d hops x %.1fs)\n", \
               seqsum/60, seq, seqsum/seq
    printf "  server work in window   %5.1f s    (%.2f%% of the above)\n", \
           ms/1000, (seqsum > 0 ? 100*(ms/1000)/seqsum : 0)

    print  ""
    print  "-- TOOLS --"
    # Skip zeros: naming a tool in the ratio math above auto-creates its key,
    # so an unfiltered dump invents rows for tools the window never saw.
    for (k in tool) if (tool[k] > 0) printf "  %-18s %5d\n", k, tool[k]
    print  "-- OUTCOMES --"
    for (k in out)  printf "  %-18s %5d\n", k, out[k]
    printf "-- CLIENTS (pre-exclusion%s) --\n", showids ? "" : ", masked"
    for (k in clients) printf "  %-34s %5d\n", maskclient(k), clients[k]
}'
