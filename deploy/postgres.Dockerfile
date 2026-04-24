# Custom postgres image with pg_repack for online bloat reclamation.
#
# Stays on postgres:17-alpine because fstservice was initialized with the
# musl libc collation provider (en_US.utf8). Switching to a glibc base would
# trigger collation version mismatch warnings on every text index (bandid,
# username, song_id) and require REINDEX DATABASE on ~350 GB of data.
#
# pg_repack is built from upstream source because alpine community does not
# ship a postgresql17-pg_repack package.
FROM postgres:17-alpine

ARG PG_REPACK_VERSION=1.5.2

RUN set -eux; \
    apk add --no-cache --virtual .build-deps \
        build-base clang19 llvm19 postgresql17-dev \
        openssl-dev zlib-dev readline-dev curl gawk; \
    cd /tmp; \
    curl -fsSL "https://github.com/reorg/pg_repack/archive/refs/tags/ver_${PG_REPACK_VERSION}.tar.gz" \
        -o pg_repack.tar.gz; \
    tar -xzf pg_repack.tar.gz; \
    cd "pg_repack-ver_${PG_REPACK_VERSION}"; \
    make; \
    make install; \
    cd /tmp; rm -rf "pg_repack-ver_${PG_REPACK_VERSION}" pg_repack.tar.gz; \
    apk del .build-deps; \
    apk add --no-cache libpq readline
