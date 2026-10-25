#!/usr/bin/env bash
# scaling-matrix.sh — the roadmap's scaling matrix, scaled to one host: the maximum sustained
# load-linear-10 rate (STOP_ON_FAIL ceiling search, rates growing 15 % per step) for
#   silos:   1 → 2 → 3 Core silos at 0.35 CPU each (results/scaling-*/overrides/core-capped.yml)
#   journal: 1 → 2 Citus workers at 0.5 CPU each   (overrides/journal-capped.yml)
# Scaling efficiency = (throughput gain) / (resource gain); the roadmap's target is ≥ 0.8.
#
#   cd tests/load && OUT=results/scaling-$(date +%F) bash scripts/scaling-matrix.sh
set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
OUT="${OUT:-results/scaling-$(date +%F)}"
OV="${OV:-$OUT/overrides}"
DURATION="${DURATION:-1m}"
# The silo axis caps each silo at 0.35 CPU: CFS throttling adds up to ~65 ms per cross-silo hop, so
# p99 grows with the silo count for reasons a production silo does not have. A rate counts as
# sustained on throughput (completions keep up, nothing left running) with p99 under P99_MAX.
P99_MAX="${P99_MAX:-3}"

rates() { python3 -c "r=$1; out=[]
while r <= $2: out.append(str(int(r))); r*=1.15
print(' '.join(out))"; }

run() { # name files scale-args env start max
  local name="$1" files="$2" scale="$3" env="$4" start="$5" max="$6"
  echo "##### $name"
  env $env docker compose $files down -v >/dev/null 2>&1
  env $env docker compose $files up -d --wait $scale >/dev/null 2>&1 || env $env docker compose $files ps -a --format '{{.Name}} {{.Status}}'
  env $env docker compose $files exec -T nginx nginx -s reload >/dev/null 2>&1; sleep 3
  K6_TARGET_URL=http://localhost:80 k6 run -q scripts/setup.js >/dev/null 2>&1
  RATES="$(rates "$start" "$max")" PROCESS=load-linear-10 DURATION="$DURATION" WARMUP=20s WAIT_S=120 \
    STOP_ON_FAIL=1 P99_MAX="$P99_MAX" OUT="$OUT/$name" bash scripts/throughput-ceiling.sh >/dev/null 2>&1
  cat "$OUT/$name/load-linear-10-summary.txt"
  env $env docker compose $files down -v >/dev/null 2>&1
}

AXES="${AXES:-silos journal}"   # AXES=silos re-runs one axis
C="-f docker-compose.yml -f $OV/core-capped.yml"
[[ " $AXES " == *" silos "* ]] && {
run silos-1 "$C" "--scale fleans-core=1" "" 10 400
run silos-2 "$C" "--scale fleans-core=2" "" 20 400
run silos-3 "$C" "--scale fleans-core=3" "" 30 400
}
J="-f docker-compose.yml -f $OV/journal-capped.yml"
[[ " $AXES " == *" journal "* ]] && {
run journal-1 "$J" "--scale citus-w2=0" "CITUS_WORKERS=citus-w1" 30 400
run journal-2 "$J" "" "" 60 400
}
find "$OUT" -name '*.stats.raw' -delete
echo "##### done"
