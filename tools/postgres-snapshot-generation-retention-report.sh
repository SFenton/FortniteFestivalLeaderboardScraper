#!/bin/bash
set -euo pipefail

if [[ -n "${LD_PRELOAD:-}" || -n "${LD_LIBRARY_PATH:-}" || -n "${LD_AUDIT:-}" ]]; then
    printf 'ERROR: loader injection environment is not permitted.\n' >&2
    exit 64
fi
unset LD_PRELOAD LD_LIBRARY_PATH LD_AUDIT DOTNET_ROOT DOTNET_ROOT_X64 DOTNET_ROOT_X86
unset BASH_ENV ENV CDPATH
PATH=/usr/bin:/bin
export PATH
unset DOTNET_STARTUP_HOOKS DOTNET_ADDITIONAL_DEPS DOTNET_SHARED_STORE DOTNET_MODIFIABLE_ASSEMBLIES
unset CORECLR_ENABLE_PROFILING CORECLR_PROFILER CORECLR_PROFILER_PATH CORECLR_PROFILER_PATH_64 CORECLR_PROFILER_PATH_32

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd -P)"
BINARY="$REPO_ROOT/tools/FstSnapshotGenerationRetentionReport/bin/Release/net9.0/linux-x64/publish/FstSnapshotGenerationRetentionReport"
expected=${FST_SNAPSHOT_RETENTION_REPORT_BINARY_SHA256:-}
if [[ ! "$expected" =~ ^[0-9a-f]{64}$ ]]; then
    printf 'ERROR: FST_SNAPSHOT_RETENTION_REPORT_BINARY_SHA256 must be a lowercase SHA-256.\n' >&2
    exit 64
fi
if [[ ! -f "$BINARY" || ! -x "$BINARY" || -L "$BINARY" ]]; then
    printf 'ERROR: the prebuilt single-file offline report executable is unavailable.\n' >&2
    exit 1
fi
actual=$(sha256sum "$BINARY" | cut -d' ' -f1)
if [[ "$actual" != "$expected" ]]; then
    printf 'ERROR: the offline report executable differs from its approved SHA-256.\n' >&2
    exit 1
fi
FST_DATA="/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data"
if [[ ! -d "$FST_DATA" \
      || "$(stat -c '%d' "$REPO_ROOT")" != "$(stat -c '%d' "$FST_DATA")" ]]; then
    printf 'ERROR: the offline report worktree and runtime extraction must be on the FST drive.\n' >&2
    exit 1
fi
(
    cd "$REPO_ROOT"
    mkdir -p artifacts/offline-retention-report-runtime
)
export DOTNET_BUNDLE_EXTRACT_BASE_DIR="$REPO_ROOT/artifacts/offline-retention-report-runtime"
export DOTNET_EnableDiagnostics=0
export FST_SNAPSHOT_RETENTION_REPORT_BINARY_PATH="$BINARY"
exec "$BINARY" "$@"
