#!/bin/sh
# Shell-tier test runner. Dependency-free POSIX sh, matching the rest of ops/.
#
# The .NET suite cannot reach ops/*.sh, and sync-heartbeat.sh gates every
# mutation: a vacuous check there shipped once already (see the runbook §5
# drill and docs/extending.md). These are the tests that make it loud.
#
#   tests/shell/run.sh          # exit 0 = all passed
set -u

cd "$(dirname "$0")"

TOTAL=0
FAILED=0

for t in test_*.sh; do
    [ -f "$t" ] || continue
    TOTAL=$((TOTAL + 1))
    echo "── $t"
    if sh "$t"; then
        echo "   ok"
    else
        echo "   FAILED"
        FAILED=$((FAILED + 1))
    fi
done

echo
if [ "$FAILED" -ne 0 ]; then
    echo "shell tests: $FAILED of $TOTAL files failed" >&2
    exit 1
fi
echo "shell tests: $TOTAL file(s) passed"
