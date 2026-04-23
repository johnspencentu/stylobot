#!/usr/bin/env bash
# run-sequence-bdf.sh — Replay content-sequence BDF scenarios against the test site.
#
# Usage:
#   ./scripts/soak/run-sequence-bdf.sh [BASE_URL]
#
# Defaults to http://localhost:5080 if BASE_URL is not set.
# The test site must be running: dotnet run --project Mostlylucid.BotDetection.Demo

set -euo pipefail

BASE_URL="${1:-http://localhost:5080}"
ENDPOINT="$BASE_URL/bot-detection/bdf-replay/replay"
SCENARIOS_DIR="$(cd "$(dirname "$0")/../../test-bdf-scenarios" && pwd)"
PASS=0
FAIL=0

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
RESET='\033[0m'

header() { echo -e "\n${CYAN}=== $1 ===${RESET}"; }
pass()   { echo -e "  ${GREEN}PASS${RESET} $1"; ((PASS++)); }
fail()   { echo -e "  ${RED}FAIL${RESET} $1"; ((FAIL++)); }

check_site() {
    header "Checking test site at $BASE_URL"
    if ! curl -sf --connect-timeout 3 "$BASE_URL/api/summary" > /dev/null 2>&1; then
        echo -e "${RED}Test site is not reachable. Start it with:${RESET}"
        echo "  dotnet run --project Mostlylucid.BotDetection.Demo"
        exit 1
    fi
    echo "  Site is up."
}

replay_scenario() {
    local file="$1"
    local label
    label=$(basename "$file" .json)

    header "$label"

    local response
    response=$(curl -sf -X POST "$ENDPOINT" \
        -H "Content-Type: application/json" \
        --data-binary @"$file" 2>&1) || {
        fail "$label — curl failed (is the endpoint enabled?)"
        return
    }

    local total match fp fn
    total=$(echo "$response" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('totalRequests',0))" 2>/dev/null || echo "?")
    match=$(echo "$response" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('matches',0))" 2>/dev/null || echo "?")
    fp=$(echo "$response" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('falsePositives',0))" 2>/dev/null || echo "?")
    fn=$(echo "$response" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('falseNegatives',0))" 2>/dev/null || echo "?")

    echo "  requests=$total  matches=$match  false_positives=$fp  false_negatives=$fn"

    # Show per-request sequence signals from topReasons
    echo "$response" | python3 - <<'PYEOF' 2>/dev/null || true
import sys, json
data = json.load(sys.stdin)
for r in data.get("results", []):
    path = r.get("path", "?")
    prob = r.get("actual", {}).get("botProbability", "?")
    reasons = r.get("actual", {}).get("topReasons", [])
    seq_reasons = [x for x in reasons if "Sequence" in x or "sequence" in x]
    is_bot = r.get("actual", {}).get("isBot", False)
    match = r.get("match", None)
    status = "✓" if match else "✗"
    bot_label = "BOT" if is_bot else "human"
    print(f"  {status}  [{path}] p={prob:.3f} ({bot_label})")
    for s in seq_reasons:
        print(f"       ↳ {s}")
PYEOF

    if [[ "$fp" == "0" && "$fn" == "0" ]]; then
        pass "$label — all expectations met"
    else
        fail "$label — fp=$fp fn=$fn"
    fi
}

main() {
    check_site

    echo ""
    echo "Replaying content-sequence BDF scenarios against: $ENDPOINT"
    echo ""

    local scenarios=(
        "sequence-human-browser.json"
        "sequence-machine-speed-bot.json"
        "sequence-api-only-bot.json"
        "sequence-cache-warm.json"
    )

    for scenario in "${scenarios[@]}"; do
        local path="$SCENARIOS_DIR/$scenario"
        if [[ -f "$path" ]]; then
            replay_scenario "$path"
        else
            echo -e "  ${YELLOW}SKIP${RESET} $scenario (not found)"
        fi
    done

    echo ""
    header "Summary"
    echo -e "  Passed: ${GREEN}$PASS${RESET}  Failed: ${RED}$FAIL${RESET}"
    echo ""

    [[ $FAIL -eq 0 ]]
}

main "$@"
