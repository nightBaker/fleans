#!/bin/sh
# Backs up the Redis keys Fleans needs after a restore, as a redis-cli command file.
#
# Run it INSIDE the Redis container, with its output redirected to a file:
#   docker compose exec -T orleans-redis sh -s -- fleans < fleans-redis-backup.sh > redis-keys.txt
#   kubectl exec -i <release>-redis-0 -- sh -s -- fleans < fleans-redis-backup.sh > redis-keys.txt
# The argument is the Orleans ServiceId (default: fleans).
#
# Restore by piping the file into redis-cli on the new Redis:
#   docker compose exec -T orleans-redis sh -c 'redis-cli -a "$REDIS_PASSWORD" --no-auth-warning' < redis-keys.txt
#   kubectl exec -i <release>-redis-0 -- redis-cli < redis-keys.txt
#
# Saved keys (each one only if it exists):
#   <ServiceId>/reminders            pending BPMN timers and timer start events
#   streamprovider-*                 Redis stream queues, with their consumer groups
#   fleans-web-dataprotection-keys   admin UI sign-in cookie keys (only with authentication on)
# The membership table (<ServiceId>/members/*) is left out: silos rebuild it when they start.
#
# DUMP payloads are tied to the Redis major version: restore into the same Redis image.
set -eu

service_id="${1:-fleans}"

cli() {
    if [ -n "${REDIS_PASSWORD:-}" ]; then
        redis-cli -a "$REDIS_PASSWORD" --no-auth-warning "$@"
    else
        redis-cli "$@"
    fi
}

for key in "$service_id/reminders" $(cli --scan --pattern 'streamprovider-*') fleans-web-dataprotection-keys; do
    [ "$(cli EXISTS "$key")" = "1" ] || continue
    # --no-raw prints the binary payload as a quoted string with \x escapes,
    # which redis-cli parses back when it reads commands from stdin.
    printf 'RESTORE "%s" 0 %s REPLACE\n' "$key" "$(cli --no-raw DUMP "$key")"
done
