#!/usr/bin/env bash
# build-images.sh — build the images used by tests/load/docker-compose.yml from the local source tree.
#
#   bash tests/load/build-images.sh            # tags :loadtest
#   TAG=mytag bash tests/load/build-images.sh
#
# Images: fleans-api (Fleans.Api, Core silos), fleans-worker (Fleans.WorkerHost, Worker silos),
# fleans-plugin (Fleans.E2E.PluginHost, Plugin-role external worker hosting the `e2e-probe` task).
#
# Uses `dotnet publish` + a throwaway Dockerfile on mcr.microsoft.com/dotnet/aspnet:10.0 instead of
# `/t:PublishContainer`: the SDK container builder always pulls the base image from MCR itself, which
# makes local builds depend on direct registry access.
set -euo pipefail

TAG="${TAG:-loadtest}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "${WORK:?}"' EXIT

build() {
  local project="$1" image="$2"
  echo "==> ${project} → ${image}:${TAG}"
  dotnet publish "${ROOT}/src/Fleans/${project}/${project}.csproj" -c Release -o "${WORK}/${project}/app" --nologo -v q
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
