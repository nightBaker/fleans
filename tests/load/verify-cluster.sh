#!/usr/bin/env bash
# verify-cluster.sh — Pre-flight check before running k6 against a Docker Compose stack.
#
# Usage:
#   EXPECTED_REPLICA_COUNT=2 bash verify-cluster.sh
#   EXPECTED_REPLICA_COUNT=4 bash verify-cluster.sh
#
# Requirements:
#   - Docker Compose >= 2.17 (stable Health field in --format json output)
#   - nginx.conf must include: add_header X-Upstream-Host $upstream_addr;
#
# Stage 1: Wait until all expected api containers report healthy.
# Stage 2: Verify nginx responds HTTP 200 on /health.
# Stage 3: Verify all expected replicas are receiving nginx traffic (detects stale DNS
#          after 'docker compose up --scale api=N' without nginx -s reload).

set -eu

EXPECTED=${EXPECTED_REPLICA_COUNT:-2}

echo "=== Stage 1: Docker container health (expected ${EXPECTED} healthy) ==="
for i in $(seq 1 12); do
  HEALTHY=$(docker compose ps api --format json 2>/dev/null \
    | python3 -c "
import json, sys

def is_healthy(c):
    return c.get('Health') == 'healthy' or 'healthy' in c.get('Status', '')

lines = sys.stdin.read().strip().split('\n')
containers = [json.loads(l) for l in lines if l.strip()]
print(sum(1 for c in containers if is_healthy(c)))
")
  echo "Healthy api containers: ${HEALTHY}/${EXPECTED} (attempt ${i}/12)"
  if [ "${HEALTHY}" -ge "${EXPECTED}" ]; then
    echo "Stage 1 passed."
    break
  fi
  if [ "${i}" -eq 12 ]; then
    echo "ERROR: Only ${HEALTHY}/${EXPECTED} containers healthy after 2 minutes." >&2
    exit 1
  fi
  sleep 10
done

echo "=== Stage 2: HTTP liveness probe ==="
HTTP_STATUS=$(curl -s -o /dev/null -w "%{http_code}" http://localhost:80/health || echo "000")
if [ "${HTTP_STATUS}" != "200" ]; then
  echo "ERROR: nginx/api returned HTTP ${HTTP_STATUS}, expected 200." >&2
  exit 1
fi
echo "Stage 2 passed (HTTP ${HTTP_STATUS})."

echo "=== Stage 3: nginx upstream membership ==="
# Sends EXPECTED * 10 requests and counts distinct X-Upstream-Host header values.
# nginx resolves 'server api:5000' only at startup or after 'nginx -s reload'.
# If fewer than EXPECTED distinct hosts reply, nginx was not reloaded after scaling.
# Prerequisite: nginx.conf must include: add_header X-Upstream-Host $upstream_addr;
REQUESTS=$((EXPECTED * 10))
SEEN_HOSTS=$(
  for _ in $(seq 1 "$REQUESTS"); do
    curl -s -o /dev/null -D - http://localhost:80/health 2>/dev/null \
      | grep -i 'x-upstream-host' \
      | awk '{print $2}' \
      | tr -d '\r'
  done | sort -u | wc -l | tr -d ' '
)
echo "Distinct upstream hosts seen: ${SEEN_HOSTS} (expected at least ${EXPECTED})"
if [ "${SEEN_HOSTS}" -lt "${EXPECTED}" ]; then
  echo "ERROR: Only ${SEEN_HOSTS}/${EXPECTED} distinct upstream replicas received traffic." >&2
  echo "nginx was not reloaded after scaling. Run:" >&2
  echo "  docker compose exec nginx nginx -s reload" >&2
  echo "  EXPECTED_REPLICA_COUNT=${EXPECTED} bash verify-cluster.sh" >&2
  exit 1
fi
echo "Stage 3 passed (${SEEN_HOSTS} distinct replicas receiving traffic)."
echo "=== Cluster verification PASSED: ${EXPECTED} replicas healthy and all receiving traffic ==="
