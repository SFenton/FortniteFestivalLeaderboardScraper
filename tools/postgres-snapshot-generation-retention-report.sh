#!/bin/bash -p
set -euo pipefail

if [[ "$-" != *p* ]]; then
    builtin printf 'ERROR: the offline report wrapper requires privileged Bash startup.\n' >&2
    exit 64
fi
if [[ -n "${LD_PRELOAD:-}" || -n "${LD_LIBRARY_PATH:-}" || -n "${LD_AUDIT:-}" ]]; then
    builtin printf 'ERROR: loader injection environment is not permitted.\n' >&2
    exit 64
fi
unset LD_PRELOAD LD_LIBRARY_PATH LD_AUDIT DOTNET_ROOT DOTNET_ROOT_X64 DOTNET_ROOT_X86
unset BASH_ENV ENV CDPATH
unset GIT_ALTERNATE_OBJECT_DIRECTORIES GIT_CEILING_DIRECTORIES GIT_COMMON_DIR
unset GIT_CONFIG GIT_CONFIG_COUNT GIT_CONFIG_GLOBAL GIT_CONFIG_NOSYSTEM GIT_CONFIG_SYSTEM
unset GIT_DIR GIT_DISCOVERY_ACROSS_FILESYSTEM GIT_EXEC_PATH GIT_INDEX_FILE
unset GIT_OBJECT_DIRECTORY GIT_OPTIONAL_LOCKS GIT_PREFIX GIT_WORK_TREE
PATH=/usr/bin:/bin
export PATH
IFS=$' \t\n'
umask 077
unset DOTNET_STARTUP_HOOKS DOTNET_ADDITIONAL_DEPS DOTNET_SHARED_STORE DOTNET_MODIFIABLE_ASSEMBLIES
unset CORECLR_ENABLE_PROFILING CORECLR_PROFILER CORECLR_PROFILER_PATH CORECLR_PROFILER_PATH_64 CORECLR_PROFILER_PATH_32

SCRIPT_DIR="$(cd -- "$(/usr/bin/dirname -- "${BASH_SOURCE[0]}")" && builtin pwd -P)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." && builtin pwd -P)"
BINARY="$REPO_ROOT/tools/FstSnapshotGenerationRetentionReport/bin/Release/net9.0/linux-x64/publish/FstSnapshotGenerationRetentionReport"
expected=${FST_SNAPSHOT_RETENTION_REPORT_BINARY_SHA256:-}
if [[ ! "$expected" =~ ^[0-9a-f]{64}$ ]]; then
    builtin printf 'ERROR: FST_SNAPSHOT_RETENTION_REPORT_BINARY_SHA256 must be a lowercase SHA-256.\n' >&2
    exit 64
fi
if [[ ! -f "$BINARY" || ! -x "$BINARY" || -L "$BINARY" ]]; then
    builtin printf 'ERROR: the prebuilt single-file offline report executable is unavailable.\n' >&2
    exit 1
fi
actual=$(/usr/bin/sha256sum -- "$BINARY")
actual=${actual%% *}
if [[ "$actual" != "$expected" ]]; then
    builtin printf 'ERROR: the offline report executable differs from its approved SHA-256.\n' >&2
    exit 1
fi
FST_DATA="/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data"
if [[ ! -d "$FST_DATA" \
      || "$(/usr/bin/stat -c '%d' -- "$REPO_ROOT")" != "$(/usr/bin/stat -c '%d' -- "$FST_DATA")" ]]; then
    builtin printf 'ERROR: the offline report worktree and runtime extraction must be on the FST drive.\n' >&2
    exit 1
fi
(
    cd "$REPO_ROOT"
    /usr/bin/mkdir -p artifacts/offline-retention-report-runtime
)
export DOTNET_BUNDLE_EXTRACT_BASE_DIR="$REPO_ROOT/artifacts/offline-retention-report-runtime"
export DOTNET_EnableDiagnostics=0
export FST_SNAPSHOT_RETENTION_REPORT_BINARY_PATH="$BINARY"
exec "$BINARY" "$@"
