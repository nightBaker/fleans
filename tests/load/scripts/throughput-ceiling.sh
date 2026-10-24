#!/usr/bin/env bash
# throughput-ceiling.sh — throughput.js at several start rates, one result row per rate.
#
#   cd tests/load && RATES="100 200 300" PROCESS=load-linear-10 DURATION=2m bash scripts/throughput-ceiling.sh
#
# First an unmeasured pre-warm at the first rate (PREWARM, default 60s; 0 skips it). Per rate: warm-up + measured window at a constant start rate, then — once the window's instances
# have finished (or WAIT_S passed) — throughput-window.sql over the measured window, and the average CPU
# per compose service over that window (docker stats, % of one core, summed over replicas).
# Output: $OUT/<process>-<rate>.{k6.txt,sql.txt,cpu.txt} and a summary table on stdout.
#
# The stack runs on the same host as k6, so a rate past the ceiling also starves k6; read the
# summary together with dropped_iterations in the k6 output.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."
RATES="${RATES:-100 200 300}"
PROCESS="${PROCESS:-load-linear-10}"
DURATION="${DURATION:-2m}"
WARMUP="${WARMUP:-30s}"
WAIT_S="${WAIT_S:-120}"
PREWARM="${PREWARM:-60s}"   # unmeasured run at the first rate: JIT, pools, topics, activations
OUT="${OUT:-results/throughput-$(date +%Y%m%d-%H%M%S)}"
TARGET="${K6_TARGET_URL:-http://localhost:80}"
mkdir -p "$OUT"

secs() { local v="$1"; case "$v" in *m) echo $(( ${v%m} * 60 ));; *s) echo "${v%s}";; *) echo "$v";; esac; }
iso() { python3 -c "import datetime,sys; print(datetime.datetime.fromtimestamp(float(sys.argv[1]), datetime.timezone.utc).isoformat())" "$1"; }

sql() {
  docker compose exec -T postgres psql -U postgres -d fleans -v ON_ERROR_STOP=1 "$@"
}

# "<rows> <running>" of the window: the read model lags the journal, so a window counts as
# finished once nothing in it is running and its row count has stopped growing.
window_state() {
  sql -At -F ' ' -c "SELECT count(*), count(*) FILTER (WHERE NOT wi.\"IsCompleted\" AND NOT wi.\"IsFailed\")
              FROM \"WorkflowInstances\" wi JOIN \"ProcessDefinitions\" pd USING (\"ProcessDefinitionId\")
              WHERE pd.\"ProcessDefinitionKey\" = '$PROCESS'
                AND wi.\"CreatedAt\" >= '$1' AND wi.\"CreatedAt\" < '$2'"
}

printf '%-6s %s\n' rate "started | completed | failed | not_finished | completed_per_s | p50 | p95 | p99 | max" > "$OUT/$PROCESS-summary.txt"

first_rate="${RATES%% *}"
if [ "$(secs "$PREWARM")" -gt 0 ]; then
  echo "==> pre-warm: $PROCESS at $first_rate starts/s for $PREWARM (not measured)"
  K6_TARGET_URL="$TARGET" TP_PROCESS="$PROCESS" TP_RATE="$first_rate" TP_DURATION="$PREWARM" TP_WARMUP=1s \
    k6 run -q scripts/throughput.js > "$OUT/$PROCESS-prewarm.k6.txt" 2>&1 || true
  sleep 10
fi

for rate in $RATES; do
  echo "==> $PROCESS at $rate starts/s ($WARMUP warm-up + $DURATION)"
  t0=$(python3 -c 'import time; print(time.time())')
  from=$(iso "$(python3 -c "print($t0 + $(secs "$WARMUP"))")")
  to=$(iso "$(python3 -c "print($t0 + $(secs "$WARMUP") + $(secs "$DURATION"))")")

  # CPU samples and database counters (db-counters.sh) over the measured window only.
  ( sleep "$(secs "$WARMUP")"; end=$(( $(date +%s) + $(secs "$DURATION") ))
    bash scripts/db-counters.sh > "$OUT/$PROCESS-$rate.db-before.txt"
    while [ "$(date +%s)" -lt "$end" ]; do
      docker stats --no-stream --format '{{.Name}} {{.CPUPerc}}' 2>/dev/null
    done
    bash scripts/db-counters.sh > "$OUT/$PROCESS-$rate.db-after.txt" ) > "$OUT/$PROCESS-$rate.stats.raw" &
  stats_pid=$!

  K6_TARGET_URL="$TARGET" TP_PROCESS="$PROCESS" TP_RATE="$rate" TP_DURATION="$DURATION" TP_WARMUP="$WARMUP" \
    k6 run -q scripts/throughput.js > "$OUT/$PROCESS-$rate.k6.txt" 2>&1 || true
  wait "$stats_pid" || true

  last=-1; stable=0
  for _ in $(seq 1 "$WAIT_S"); do
    read -r rows running <<< "$(window_state "$from" "$to")"
    if [ "$rows" = "$last" ] && [ "$running" = "0" ]; then stable=$((stable + 1)); else stable=0; fi
    [ "$stable" -ge 10 ] && [ "$rows" != "0" ] && break
    last="$rows"; sleep 1
  done

  sql -v process="$PROCESS" -v from="$from" -v to="$to" < scripts/throughput-window.sql | tee "$OUT/$PROCESS-$rate.sql.txt"
  python3 - "$OUT/$PROCESS-$rate.stats.raw" > "$OUT/$PROCESS-$rate.cpu.txt" <<'EOF'
import re, sys, collections
samples = collections.defaultdict(list)        # service -> per-snapshot sums
snap = collections.defaultdict(float); seen = set()
for line in open(sys.argv[1]):
    parts = line.split()
    if len(parts) != 2 or not parts[1].rstrip('%').replace('.', '', 1).isdigit():
        continue                                # a stopped / restarting container reports "--"
    name, cpu = parts
    svc = re.sub(r'^fleans-load-|-\d+$', '', name)
    if name in seen:                            # a container repeats → next snapshot
        for s, v in snap.items(): samples[s].append(v)
        snap.clear(); seen.clear()
    seen.add(name); snap[svc] += float(cpu.rstrip('%'))
for s, v in snap.items(): samples[s].append(v)
total = 0
for s in sorted(samples, key=lambda s: -sum(samples[s]) / len(samples[s])):
    avg = sum(samples[s]) / len(samples[s]); total += avg
    print(f"{s:18} {avg:7.1f} %")
print(f"{'total':18} {total:7.1f} %")
EOF
  cat "$OUT/$PROCESS-$rate.cpu.txt"
  # Per completed instance: transactions and WAL bytes on each database server.
  completed=$(sql -At -v process="$PROCESS" -v from="$from" -v to="$to" < scripts/throughput-window.sql | cut -d'|' -f2)
  paste -d' ' "$OUT/$PROCESS-$rate.db-before.txt" "$OUT/$PROCESS-$rate.db-after.txt" \
    | awk -v n="$completed" 'BEGIN { printf "%-14s %10s %12s\n", "server", "xact/inst", "WAL KB/inst" }
        n > 0 { printf "%-14s %10.1f %12.1f\n", $1, ($5 - $2) / n, ($6 - $3) / n / 1024 }' \
    | tee "$OUT/$PROCESS-$rate.db.txt"
  row=$(sql -At -F ' | ' -v process="$PROCESS" -v from="$from" -v to="$to" < scripts/throughput-window.sql)
  printf '%-6s %s\n' "$rate" "$row" >> "$OUT/$PROCESS-summary.txt"
done

echo; cat "$OUT/$PROCESS-summary.txt"
