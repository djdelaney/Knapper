#!/bin/sh
# How many ROUND TRIPS is the client spending to do its vault work?
#
#   pct exec 106 -- sh /opt/knapper/ops/call-economics.sh [DAYS] [--all-clients]
#                                                          [--show-identities]
#                                                          [--daily] [--audit PATH]
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
# not how efficiently it was spent. Read the RATIOS block first. The counts
# are context for judging whether the sample is big enough to mean anything.
#
# ⛔ AND A SHARE OF CALLS IS NOT A MEASURE OF WORK. The batch shares say what
# fraction of CALLS were batch-shaped; they say nothing about how much went
# in each one. Measured 2026-08-31, they disagreed badly: batched-mutation
# share rose 13.3% -> 17.9% (+35% relative) while FILES PER MUTATION CALL —
# the thing the share is a proxy for — moved 1.156 -> 1.184, about 2%. The
# share had risen because the window carried 35% more mutation work, not
# because agents batched better; mean batch size was 2.04 before and 2.08
# after. Read WORK PER ROUND TRIP, which counts files, and treat the shares
# as supporting detail.
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
# The client APPLICATION names beside them are not masked and are not
# identities: they are product names (claude-ai, claude-code), and they are
# the axis Access identity cannot supply, because that identity is per-USER
# while the round-trip cost this report measures is per-SURFACE. They are
# sanitised where they are logged (ToolSupport.ClientApp), not here.
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
DAILY=0
AUDIT="${Vault__AuditLogPath:-/var/lib/knapper/audit.jsonl}"
take_audit=0
for arg in "$@"; do
    if [ "$take_audit" = 1 ]; then AUDIT="$arg"; take_audit=0; continue; fi
    [ "$arg" = "--all-clients" ] && ALL_CLIENTS=1
    [ "$arg" = "--show-identities" ] && SHOW_IDENTITIES=1
    [ "$arg" = "--daily" ] && DAILY=1
    [ "$arg" = "--audit" ] && take_audit=1
done

command -v jq >/dev/null 2>&1 || { echo "call-economics: jq not found" >&2; exit 2; }

# The audit window must be the SAME instant as the journal's. journalctl's
# "N days ago" and this are both now-minus-N; the representations differ, the
# instant does not. String comparison against .At is valid because AuditLog
# writes DateTimeOffset.UtcNow, which always serialises with a +00:00 offset —
# a switch to DateTimeOffset.Now would silently shift this by the box's
# timezone. jq's fromdateiso8601 is NOT the alternative: it rejects the
# fractional seconds .NET emits.
#
# GNU date first (CT 106), then BSD (the macOS dev box, where the shell tests
# run). Not a portability nicety: with neither, SINCE is empty and the whole
# WORK PER ROUND TRIP block silently does not print — a missing headline
# reads as a report that had nothing to say, not as a broken date command.
SINCE=$(date -u -d "${DAYS} days ago" +%Y-%m-%dT%H:%M:%S 2>/dev/null \
     || date -u -v-"${DAYS}"d +%Y-%m-%dT%H:%M:%S 2>/dev/null \
     || echo "")

# WORK PER ROUND TRIP. The journal records one line per CALL and nothing about
# what was in it, so files-per-call cannot come from there. The audit log has
# it already: one line per PATH, tagged with the RequestId of the call that
# touched it (AuditLog.Entry). Distinct (RequestId, Path) pairs are the files;
# distinct RequestIds are the calls.
#
# ⛔ Vault paths are note titles — the user's data. Nothing below prints one:
# this counts pairs and discards them. Do not add a per-path breakdown to a
# report whose whole point is being pasted into an issue.
#
# Ops are the batch/single pair for the same three kinds the call-side ratio
# uses, so the two are about the same population. batch-validate is excluded
# deliberately: it records an item that failed validation, and counting it
# inflated mean batch size 2.08 -> 2.2 the first time this was done by hand.
audit_counts() {
    if [ -z "$SINCE" ] || [ ! -r "$AUDIT" ]; then
        echo "0 0 0"
        return
    fi
    jq -r --arg since "$SINCE" '
        select(.At > $since)
        | select(.Op | test("^(batch-)?(edit|append|create)$"))
        | [(if (.Op | startswith("batch-")) then "batch" else "single" end), .RequestId, .Path]
        | @tsv' "$AUDIT" 2>/dev/null \
    | sort -u \
    | awk -F'\t' '
        { files[$1]++; if (!seen[$1 SUBSEP $2]++) calls[$1]++ }
        END { printf "%d %d %d\n", calls["batch"]+0, files["batch"]+0, files["single"]+0 }'
}

set -- $(audit_counts)
A_BATCH_CALLS=$1
A_BATCH_FILES=$2
A_SINGLE_FILES=$3

journalctl -u knapper --since "${DAYS} days ago" -o json \
| jq -r '(.MESSAGE|fromjson?) as $m
    | select($m.Category=="Knapper.Mcp.Tools.ToolSupport" and $m.State.Outcome!=null)
    | [(.__REALTIME_TIMESTAMP|tonumber/1000000), $m.State.Tool, $m.State.ElapsedMs,
       $m.State.Outcome, $m.State.Client, ($m.State.ClientApp // "unrecorded")] | @tsv' \
| awk -F'\t' -v days="$DAYS" -v allc="$ALL_CLIENTS" -v showids="$SHOW_IDENTITIES" \
      -v abcalls="$A_BATCH_CALLS" -v abfiles="$A_BATCH_FILES" -v asfiles="$A_SINGLE_FILES" '
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
# A run is consecutive calls of the SAME tool with no session boundary between
# them. This is the metric the batch shares cannot express: a lone vault_edit
# is correct, three in a row is the un-batched loop the instructions target,
# and a share of calls cannot tell them apart. It is also the ONLY measure
# that reaches vault_search, which has no batch form at all and is the single
# largest consumer of round trips in every window measured so far.
function closerun() {
    if (runtool == "") return
    if (runlen > 1) { chained[runtool] += runlen; excess[runtool] += runlen - 1 }
    if (runlen > longest[runtool]) longest[runtool] = runlen
}
{
    allcalls++; clients[$5]++
    # The probe identity is a service token (Access common_name / sub), never
    # an email. Match on that rather than a hardcoded token id, which would
    # rot the first time the token is rotated.
    isprobe = ($5 !~ /@/)
    if (isprobe && !allc) { probeskipped++; next }

    n++; tool[$2]++; out[$4]++; ms += $3; if ($3+0 > mx) mx = $3+0
    apps[$6]++
    d = 20
    if (n > 1) {
        d = $1 - prev
        # <1s apart cannot contain model inference: concurrent dispatch.
        # >20s is a human thinking or a session boundary, not a round trip.
        if (d < 1) burst++
        else if (d < 20) { seq++; seqsum += d }
        else idle++
    }
    if (n > 1 && $2 == runtool && d < 20) runlen++
    else { closerun(); runtool = $2; runlen = 1 }
    prev = $1
}
END {
    if (n == 0) { print "no tool calls in window"; exit }
    closerun()
    reads   = tool["vault_read"];  breads = tool["vault_batch_read"]
    muts    = tool["vault_edit"] + tool["vault_append"] + tool["vault_create"]
    batches = tool["vault_batch"]

    printf "== CALL ECONOMICS · %d-day window ==\n", days
    printf "calls %d   server mean %.1fms   max %dms\n", n, ms/n, mx
    if (probeskipped) printf "(%d verify-probe calls excluded; --all-clients to include)\n", probeskipped

    # THE headline. Files, not call shapes — see the banner comment. Printed
    # first because the shares below have twice been read as evidence of a
    # change this number says did not happen.
    if (abcalls + asfiles > 0) {
        mcalls = abcalls + asfiles   # single mutations are one path per call
        mfiles = abfiles + asfiles
        print  ""
        print  "-- WORK PER ROUND TRIP (audit log; the number to compare) --"
        printf "  files per mutation call %5.3f    (%d files in %d calls)\n", \
               mfiles/mcalls, mfiles, mcalls
        if (abcalls > 0)
            printf "  mean batch size         %5.2f    (%d files in %d batches)\n", \
                   abfiles/abcalls, abfiles, abcalls
        printf "  batched share BY FILE   %5.1f%%   (%d of %d files)\n", \
               100*abfiles/mfiles, abfiles, mfiles
    }

    print  ""
    print  "-- RATIOS (call shapes; supporting detail, not the headline) --"
    if (reads + breads > 0)
        printf "  batched-read share      %5.1f%%   (%d batch_read vs %d read)\n", \
               100*breads/(reads+breads), breads, reads
    if (muts + batches > 0)
        printf "  batched-mutation share  %5.1f%%   (%d batch vs %d single)\n", \
               100*batches/(muts+batches), batches, muts
    if (muts + batches > 0)
        # NOT a batching signal, despite having been used as one. Same work
        # fully unbatched (10 read + 10 edit) and fully batched (1 batch_read
        # + 1 batch) both give 1.00 — it is invariant to batching applied
        # evenly, and batch-reading alone drives it BELOW 1.00 with the
        # fresh-read rule intact. What it actually measures is reading that
        # is not driving a write. Kept because a value near 1 alongside a
        # near-zero PreconditionFailed count is evidence the fresh-read rule
        # is being honoured.
        printf "  reads per mutation call %5.2f    (reading not driving a write; NOT a batching signal)\n", \
               (reads+breads)/(muts+batches)
    if (n > 1) {
        # ⛔ These two move MECHANICALLY when batching changes, in the
        # direction that reads as a regression. Batching destroys concurrent
        # gaps by construction — five vault_read calls dispatched together
        # produce four sub-second gaps, the vault_batch_read replacing them
        # produces none — so burst share falls and, sharing a denominator,
        # sequential share rises with serialization unchanged. Do not read
        # either as evidence about client discipline.
        printf "  concurrent (<1s apart)  %5.1f%%   (%d calls)\n", 100*burst/(n-1), burst
        if (seq > 0)
            printf "  sequential hops         %5.1f%%   (%d calls, mean %.1fs apart)\n", \
                   100*seq/(n-1), seq, seqsum/seq
    }

    print  ""
    print  "-- CONSECUTIVE RUNS (calls one wider call could have replaced) --"
    print  "   ⛔ a CEILING, not a saving: a call that genuinely needed the"
    print  "      previous answer is counted here too."
    for (k in tool)
        if (tool[k] > 0 && excess[k] > 0)
            printf "  %-18s %5d calls  %4.0f%% in runs  %4d excess  (longest %d)\n", \
                   k, tool[k], 100*chained[k]/tool[k], excess[k], longest[k]

    print  ""
    print  "-- TOTALS (scale with how busy the window was; do NOT compare) --"
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
    # The surface, which the Access identity below cannot distinguish: a
    # directly-configured client measured ~120ms against the relay at ~3s, so
    # a window whose surface MIX moved looks exactly like one whose agents
    # changed behaviour. "unrecorded" is traffic logged before this field existed;
    # the field; "unknown" is a client that declined to name itself.
    print  "-- CLIENT APPS --"
    for (k in apps) printf "  %-34s %5d\n", k, apps[k]
    printf "-- CLIENTS (pre-exclusion%s) --\n", showids ? "" : ", masked"
    for (k in clients) printf "  %-34s %5d\n", maskclient(k), clients[k]
}'

# The variance the two-window comparison does not have. Two adjacent weeks
# give a difference with no error bar, and every ratio here is driven by work
# MIX: the 2026-08-24 -> 08-31 comparison moved every headline in the
# "improved" direction and not one of them cleared significance (best was
# batched-read share at p≈0.08, before correcting for calls clustering within
# sessions). A day-to-day spread is the cheapest way to know what size step
# would have meant something.
if [ "$DAILY" = 1 ]; then
    if [ -z "$SINCE" ] || [ ! -r "$AUDIT" ]; then
        echo ""
        echo "-- DAILY -- audit log unreadable at $AUDIT (pass --audit PATH)"
        exit 0
    fi
    echo ""
    echo "-- DAILY files per mutation call (audit log) --"
    jq -r --arg since "$SINCE" '
        select(.At > $since)
        | select(.Op | test("^(batch-)?(edit|append|create)$"))
        | [(.At[0:10]), .RequestId, .Path] | @tsv' "$AUDIT" 2>/dev/null \
    | sort -u \
    | awk -F'\t' '
        { files[$1]++; if (!seen[$1 SUBSEP $2]++) calls[$1]++ }
        END {
            for (d in calls) { ratio[d] = files[d]/calls[d]; n++; sum += ratio[d] }
            if (n == 0) { print "  no mutations in window"; exit }
            mean = sum/n
            for (d in ratio) { v = ratio[d] - mean; ss += v*v }
            sd = (n > 1) ? sqrt(ss/n) : 0
            m = 0
            for (d in calls) days[m++] = d
            # Chronological: dates are ISO, so a string sort is a date sort.
            for (i = 1; i < m; i++)
                for (j = i; j > 0 && days[j-1] > days[j]; j--) { t = days[j]; days[j] = days[j-1]; days[j-1] = t }
            for (i = 0; i < m; i++) {
                d = days[i]
                printf "  %s  %4d calls  %4d files  %5.3f\n", d, calls[d], files[d], ratio[d]
            }
            printf "  mean %.3f   sd %.3f over %d days\n", mean, sd, n
            printf "  ⛔ a step between two windows smaller than ~%.3f (2 sd) is not\n", 2*sd
            print  "     distinguishable from day-to-day work mix."
        }'
fi
