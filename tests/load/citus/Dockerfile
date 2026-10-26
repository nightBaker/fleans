# Citus on the official PostgreSQL 16 image, built from source: citusdata/citus is published for
# amd64 only, and under emulation on an arm64 laptop it is several times slower (and was OOM-killed).
# Same major as the citusdata/citus:12.1 image used by the Testcontainers runs.
#   docker build -t fleans-citus:12.1 tests/load/citus     (build-images.sh does it)
FROM postgres:16-bookworm AS build
ARG CITUS_VERSION=v12.1.6
RUN apt-get update && apt-get install -y --no-install-recommends \
      build-essential git ca-certificates autoconf flex bison \
      postgresql-server-dev-16 libcurl4-openssl-dev liblz4-dev libzstd-dev libssl-dev libkrb5-dev \
 && git clone --depth 1 --branch "$CITUS_VERSION" https://github.com/citusdata/citus.git /src/citus \
 && cd /src/citus && ./configure --without-libcurl && make -j"$(nproc)" && make install DESTDIR=/out

FROM postgres:16-bookworm
RUN apt-get update && apt-get install -y --no-install-recommends liblz4-1 libzstd1 && rm -rf /var/lib/apt/lists/*
COPY --from=build /out/ /
# Loaded at server start; the extension is created in POSTGRES_DB on first init.
RUN echo "shared_preload_libraries='citus'" >> /usr/share/postgresql/postgresql.conf.sample \
 && echo "CREATE EXTENSION citus;" > /docker-entrypoint-initdb.d/001-create-citus-extension.sql
