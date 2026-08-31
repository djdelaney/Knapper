#!/bin/sh
# Tests for ops/call-economics.sh — the before/after measurement for the
# CALL ECONOMICS instructions (docs/call-economics.md).
#
# What is worth testing here is not the arithmetic but the three filters the
# report's credibility rests on, each of which fails SILENTLY — a wrong ratio
# still prints, still looks like a measurement, and is then compared against a
# later window as if both were sound:
#
#   1. Rows are selected by Category+Outcome. The first hand-run of this query
#      matched on the message SUBSTRING "ToolSupport", which also matches an
#      exception whose stack mentions the class — those rows carry no tool name
#      and became blank entries nobody could explain.
#   2. `knapper verify --url` spends a fixed 4 calls per run from a
#      service-token identity. Left in, every ratio is diluted by however often
#      an operator happened to run verify, which is not a property of the
#      window being measured.
#   3. Naming a tool in the ratio math auto-creates its awk key, so an
#      unfiltered dump invents rows for tools the window never saw.
#   4. The audit-derived headline (files per mutation call) counts distinct
#      (RequestId, Path) pairs. Counting LINES instead double-counts every
#      applied item — attempt + outcome — and reports batches as twice their
#      real size, which is exactly how 2.08 was first mis-reported as 2.2.
#   5. That headline is gated on a date command. Where neither GNU nor BSD
#      date works the block does not print at all, and a missing headline
#      reads as a report with nothing to say.
set -u

SCRIPT="$(cd "$(dirname "$0")/../.." && pwd)/ops/call-economics.sh"
FAILURES=0
TMPROOT=$(mktemp -d)
trap 'rm -rf "$TMPROOT"' EXIT

if ! command -v jq >/dev/null 2>&1; then
    echo "   SKIP: jq not installed"
    exit 0
fi

fail() {
    echo "   FAIL: $1" >&2
    FAILURES=$((FAILURES + 1))
}

# A stub journalctl on PATH. Static fixture rather than a generator: the
# expected ratios below are hand-computed from exactly these rows, and a
# generator would let the fixture drift away from the arithmetic it anchors.
#
# Five human calls, chosen so every gap class is exercised exactly once:
#   +0.5s  burst (concurrent dispatch — no inference fits in half a second)
#   +3.5s  sequential      }  mean 5.25s
#   +7.0s  sequential      }
#   +89s   idle (a human thinking, or a session boundary)
mkdir -p "$TMPROOT/bin"
cat > "$TMPROOT/fixture.json" <<'EOF'
{"__REALTIME_TIMESTAMP":"1000000000000","MESSAGE":"{\"Category\":\"Knapper.Mcp.Tools.ToolSupport\",\"State\":{\"Tool\":\"vault_read\",\"Outcome\":\"ok\",\"ElapsedMs\":\"10\",\"Client\":\"owner@example.com\"}}"}
{"__REALTIME_TIMESTAMP":"1000000500000","MESSAGE":"{\"Category\":\"Knapper.Mcp.Tools.ToolSupport\",\"State\":{\"Tool\":\"vault_read\",\"Outcome\":\"ok\",\"ElapsedMs\":\"10\",\"Client\":\"owner@example.com\"}}"}
{"__REALTIME_TIMESTAMP":"1000004000000","MESSAGE":"{\"Category\":\"Knapper.Mcp.Tools.ToolSupport\",\"State\":{\"Tool\":\"vault_batch_read\",\"Outcome\":\"ok\",\"ElapsedMs\":\"10\",\"Client\":\"owner@example.com\"}}"}
{"__REALTIME_TIMESTAMP":"1000011000000","MESSAGE":"{\"Category\":\"Knapper.Mcp.Tools.ToolSupport\",\"State\":{\"Tool\":\"vault_edit\",\"Outcome\":\"ok\",\"ElapsedMs\":\"10\",\"Client\":\"owner@example.com\"}}"}
{"__REALTIME_TIMESTAMP":"1000100000000","MESSAGE":"{\"Category\":\"Knapper.Mcp.Tools.ToolSupport\",\"State\":{\"Tool\":\"vault_batch\",\"Outcome\":\"ok\",\"ElapsedMs\":\"10\",\"Client\":\"owner@example.com\"}}"}
{"__REALTIME_TIMESTAMP":"1000200000000","MESSAGE":"{\"Category\":\"Knapper.Mcp.Tools.ToolSupport\",\"State\":{\"Tool\":\"vault_files\",\"Outcome\":\"ok\",\"ElapsedMs\":\"8\",\"Client\":\"00000000000000000000000000000000.access\"}}"}
{"__REALTIME_TIMESTAMP":"1000200200000","MESSAGE":"{\"Category\":\"Knapper.Mcp.Tools.ToolSupport\",\"State\":{\"Tool\":\"vault_read\",\"Outcome\":\"ok\",\"ElapsedMs\":\"8\",\"Client\":\"00000000000000000000000000000000.access\"}}"}
{"__REALTIME_TIMESTAMP":"1000200400000","MESSAGE":"{\"Category\":\"Knapper.Mcp.Tools.ToolSupport\",\"State\":{\"Tool\":\"vault_search\",\"Outcome\":\"ok\",\"ElapsedMs\":\"8\",\"Client\":\"00000000000000000000000000000000.access\"}}"}
{"__REALTIME_TIMESTAMP":"1000200600000","MESSAGE":"{\"Category\":\"Knapper.Mcp.Tools.ToolSupport\",\"State\":{\"Tool\":\"vault_edit\",\"Outcome\":\"NotFound\",\"ElapsedMs\":\"8\",\"Client\":\"00000000000000000000000000000000.access\"}}"}
{"__REALTIME_TIMESTAMP":"1000300000000","MESSAGE":"Unhandled exception at Knapper.Mcp.Tools.ToolSupport.Run(String tool)"}
{"__REALTIME_TIMESTAMP":"1000300100000","MESSAGE":"{\"Category\":\"Knapper.Mcp.Tools.ToolSupport\",\"State\":{\"Tool\":\"vault_read\",\"Client\":\"owner@example.com\"}}"}
EOF
cat > "$TMPROOT/bin/journalctl" <<EOF
#!/bin/sh
cat "$TMPROOT/fixture.json"
EOF
chmod +x "$TMPROOT/bin/journalctl"
PATH="$TMPROOT/bin:$PATH"
export PATH

# The audit fixture. Timestamps must be inside the window, which is relative
# to now — so the instant is generated and everything else is static.
#
# Three mutation calls over two paths each... no: ONE batch of 2 paths
# (req-b), and 2 single edits (req-s1, req-s2). Every applied item writes TWO
# lines (attempt + outcome, AuditLog/VaultMutationService), which is the
# double-count this fixture exists to catch: 8 lines, 4 files, 3 calls.
# batch-validate is a fourth op that must NOT be counted as a file.
NOW=$(date -u +%Y-%m-%dT%H:%M:%S 2>/dev/null)
cat > "$TMPROOT/audit.jsonl" <<EOF
{"At":"$NOW.1000000+00:00","Op":"batch-edit","Path":"a.md","Outcome":"attempt","RequestId":"req-b"}
{"At":"$NOW.2000000+00:00","Op":"batch-edit","Path":"a.md","Outcome":"ok","RequestId":"req-b"}
{"At":"$NOW.3000000+00:00","Op":"batch-edit","Path":"b.md","Outcome":"attempt","RequestId":"req-b"}
{"At":"$NOW.4000000+00:00","Op":"batch-edit","Path":"b.md","Outcome":"ok","RequestId":"req-b"}
{"At":"$NOW.5000000+00:00","Op":"edit","Path":"c.md","Outcome":"attempt","RequestId":"req-s1"}
{"At":"$NOW.6000000+00:00","Op":"edit","Path":"c.md","Outcome":"ok","RequestId":"req-s1"}
{"At":"$NOW.7000000+00:00","Op":"create","Path":"d.md","Outcome":"attempt","RequestId":"req-s2"}
{"At":"$NOW.8000000+00:00","Op":"create","Path":"d.md","Outcome":"ok","RequestId":"req-s2"}
{"At":"$NOW.9000000+00:00","Op":"batch-validate","Path":"e.md","Outcome":"AnchorMismatch","RequestId":"req-b"}
{"At":"2000-01-01T00:00:00.0000000+00:00","Op":"edit","Path":"old.md","Outcome":"ok","RequestId":"req-old"}
EOF

OUT=$(sh "$SCRIPT" 7 --audit "$TMPROOT/audit.jsonl" 2>&1)

# ---- 1. the human window is the five real calls -------------------------
# 11 fixture rows: 5 human + 4 probe + 1 exception + 1 Outcome-less. Anything
# other than 5 means a filter let something through.
printf '%s' "$OUT" | grep -q 'calls 5 ' \
    || fail "expected 5 human calls, got: $(printf '%s' "$OUT" | grep '^calls' || echo none)"

# ---- 2. the verify probe is excluded, and says so -----------------------
printf '%s' "$OUT" | grep -q '4 verify-probe calls excluded' \
    || fail "the 4 service-token probe calls were not excluded (or not reported)"

# ---- 3. ratios are computed over the human calls only -------------------
# 1 batch_read vs 2 read = 33.3%; 1 batch vs 1 edit = 50.0%; 3 reads / 2
# mutations = 1.50. Each would move if the probe's read+edit leaked in.
printf '%s' "$OUT" | grep -q 'batched-read share.*33\.3%' \
    || fail "batched-read share wrong: $(printf '%s' "$OUT" | grep 'batched-read' || echo missing)"
printf '%s' "$OUT" | grep -q 'batched-mutation share.*50\.0%' \
    || fail "batched-mutation share wrong: $(printf '%s' "$OUT" | grep 'batched-mutation' || echo missing)"
printf '%s' "$OUT" | grep -q 'reads per mutation.*1\.50' \
    || fail "reads per mutation wrong: $(printf '%s' "$OUT" | grep 'reads per' || echo missing)"

# ---- 3b. the headline counts FILES, not audit lines -------------------
# 4 files (a,b,c,d) in 3 calls = 1.333. Counting lines would say 8/3 = 2.667;
# counting batch-validate as a file would say 5/3; letting the stale 2000
# entry in would say 5/4.
printf '%s' "$OUT" | grep -q 'files per mutation call.*1\.333.*(4 files in 3 calls)' \
    || fail "files per mutation call wrong: $(printf '%s' "$OUT" | grep 'files per mutation' || echo missing)"
printf '%s' "$OUT" | grep -q 'mean batch size.*2\.00.*(2 files in 1 batches)' \
    || fail "mean batch size wrong: $(printf '%s' "$OUT" | grep 'mean batch size' || echo missing)"
printf '%s' "$OUT" | grep -q 'batched share BY FILE.*50\.0%' \
    || fail "batched share by file wrong: $(printf '%s' "$OUT" | grep 'BY FILE' || echo missing)"

# ---- 3c. an unreadable audit log drops the block, never invents it -----
NOAUDIT=$(sh "$SCRIPT" 7 --audit "$TMPROOT/does-not-exist.jsonl" 2>&1)
printf '%s' "$NOAUDIT" | grep -q 'files per mutation call' \
    && fail "the headline printed with no audit log to compute it from"
printf '%s' "$NOAUDIT" | grep -q 'calls 5 ' \
    || fail "an unreadable audit log broke the rest of the report"

# ---- 3d. consecutive runs, the metric the shares cannot express --------
# The fixture opens with two vault_read calls 0.5s apart: one run of 2, so
# one excess call. Nothing else repeats, so nothing else is listed.
printf '%s' "$OUT" | grep -q 'vault_read.*2 calls.*100% in runs.*1 excess.*(longest 2)' \
    || fail "run detection wrong: $(printf '%s' "$OUT" | grep -A 4 'CONSECUTIVE RUNS' || echo missing)"
printf '%s' "$OUT" | grep -q 'vault_batch_read.*excess' \
    && fail "a tool that never repeated was listed as having a run"

# ---- 3e. --daily reports the spread the two-window comparison lacks ----
DAILY=$(sh "$SCRIPT" 7 --daily --audit "$TMPROOT/audit.jsonl" 2>&1)
printf '%s' "$DAILY" | grep -q '3 calls.*4 files.*1\.333' \
    || fail "daily row wrong: $(printf '%s' "$DAILY" | grep -A 3 'DAILY files' || echo missing)"
printf '%s' "$DAILY" | grep -q 'mean 1\.333   sd 0\.000 over 1 days' \
    || fail "daily summary wrong: $(printf '%s' "$DAILY" | grep 'mean ' || echo missing)"

# ---- 4. gap classification ----------------------------------------------
# 4 inter-call gaps: 1 burst, 2 sequential (mean 5.25s), 1 idle.
printf '%s' "$OUT" | grep -q 'concurrent (<1s apart).*25\.0%' \
    || fail "burst share wrong: $(printf '%s' "$OUT" | grep 'concurrent' || echo missing)"
printf '%s' "$OUT" | grep -q 'sequential hops.*50\.0%.*5\.2s apart' \
    || fail "sequential hops wrong: $(printf '%s' "$OUT" | grep 'sequential hops' || echo missing)"

# ---- 5. tools the window never saw are not invented ---------------------
# vault_append/vault_create are named in the mutation ratio, which creates
# their awk keys; an unfiltered dump would list them at 0.
printf '%s' "$OUT" | grep -q 'vault_append' \
    && fail "vault_append was listed despite never appearing in the window"
printf '%s' "$OUT" | grep -q 'vault_create' \
    && fail "vault_create was listed despite never appearing in the window"

# ---- 6. the client breakdown shows the probe, but MASKED ----------------
# Excluded from the ratios, never hidden: an operator has to be able to see
# that the exclusion happened and how big it was. But the real values are an
# owner email and an Access service-token client id, and this report is made
# to be pasted — so a prefix plus a kind tag is what appears, and the full
# strings must not.
printf '%s' "$OUT" | grep -q '00000000… (service token)' \
    || fail "the excluded client is missing (or unmasked) in the breakdown"
printf '%s' "$OUT" | grep -q 'own…@… (user)' \
    || fail "the human client is missing (or unmasked) in the breakdown"
printf '%s' "$OUT" | grep -q '00000000000000000000000000000000.access' \
    && fail "the service-token id was printed in full"
printf '%s' "$OUT" | grep -q 'owner@example.com' \
    && fail "the client email was printed in full"

# ---- 6b. --show-identities opts back in ---------------------------------
IDS=$(sh "$SCRIPT" 7 --show-identities --audit "$TMPROOT/audit.jsonl" 2>&1)
printf '%s' "$IDS" | grep -q '00000000000000000000000000000000.access' \
    || fail "--show-identities did not print the service-token id in full"
printf '%s' "$IDS" | grep -q 'owner@example.com' \
    || fail "--show-identities did not print the client email in full"

# ---- 7. --all-clients includes the probe --------------------------------
ALL=$(sh "$SCRIPT" 7 --all-clients --audit "$TMPROOT/audit.jsonl" 2>&1)
printf '%s' "$ALL" | grep -q 'calls 9 ' \
    || fail "--all-clients expected 9 calls, got: $(printf '%s' "$ALL" | grep '^calls' || echo none)"
printf '%s' "$ALL" | grep -q 'verify-probe calls excluded' \
    && fail "--all-clients still reported an exclusion"

# ---- 8. an empty window says so rather than dividing by zero ------------
: > "$TMPROOT/fixture.json"
EMPTY=$(sh "$SCRIPT" 7 --audit "$TMPROOT/audit.jsonl" 2>&1)
printf '%s' "$EMPTY" | grep -q 'no tool calls in window' \
    || fail "an empty window did not report itself: $EMPTY"

if [ "$FAILURES" -ne 0 ]; then
    echo "call-economics: $FAILURES assertion(s) failed" >&2
    exit 1
fi
exit 0
