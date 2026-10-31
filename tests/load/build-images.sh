#!/usr/bin/env bash
# build-images.sh — build the images used by tests/load/docker-compose.yml from the local source tree.
#
#   bash tests/load/build-images.sh            # tags :loadtest
#   TAG=mytag bash tests/load/build-images.sh
#   PLUGINS_ROOT=~/src/fleans-plugins bash tests/load/build-images.sh   # default: ../fleans-plugins next to this repo
#
# Images: fleans-api (Fleans.Api, Core silos), fleans-worker (Fleans.WorkerHost, Worker silos),
# fleans-plugin (Fleans.E2E.PluginHost, Plugin-role external worker hosting the `e2e-probe` task),
# fleans-kafka-worker (Fleans.Samples.KafkaWorker from a nightBaker/fleans-plugins checkout, the Kafka
#   external-task worker of overrides/kafka-worker.yml; skipped when PLUGINS_ROOT has no checkout),
# fleans-citus (Citus 12.1 on postgres:16, built from source — the journal store).
#
# Uses `dotnet publish` + a throwaway Dockerfile on mcr.microsoft.com/dotnet/aspnet:10.0 instead of
# `/t:PublishContainer`: the SDK container builder always pulls the base image from MCR itself, which
# makes local builds depend on direct registry access.
set -euo pipefail

TAG="${TAG:-loadtest}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PLUGINS_ROOT="${PLUGINS_ROOT:-${ROOT}/../fleans-plugins}"
WORK="$(mktemp -d)"
trap 'rm -rf "${WORK:?}"' EXIT

build() {
  local project="$1" image="$2" dir="${3:-${ROOT}/src/Fleans}"
  echo "==> ${project} → ${image}:${TAG}"
  dotnet publish "${dir}/${project}/${project}.csproj" -c Release -o "${WORK}/${project}/app" --nologo -v q
  cat > "${WORK}/${project}/Dockerfile" <<EOF
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY app/ .
ENV ASPNETCORE_HTTP_PORTS=8080
ENTRYPOINT ["dotnet", "${project}.dll"]
EOF
  docker build -q -t "${image}:${TAG}" "${WORK}/${project}"
}

build Fleans.Api            fleans-api
build Fleans.WorkerHost     fleans-worker
build Fleans.E2E.PluginHost fleans-plugin
if [[ -d "${PLUGINS_ROOT}/samples/Fleans.Samples.KafkaWorker" ]]; then
  build Fleans.Samples.KafkaWorker fleans-kafka-worker "${PLUGINS_ROOT}/samples"
else
  echo "==> skip fleans-kafka-worker: no nightBaker/fleans-plugins checkout at ${PLUGINS_ROOT}"
fi

# Citus for the journal (docker-compose.yml): citusdata/citus is amd64-only, so build it natively.
echo "==> citus/Dockerfile → fleans-citus:12.1"
docker build -q -t fleans-citus:12.1 "${ROOT}/tests/load/citus"
