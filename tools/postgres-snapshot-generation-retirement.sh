#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd -P)"
BINARY="$REPO_ROOT/tools/FstSnapshotGenerationRetirement/bin/Release/net9.0/linux-x64/publish/FstSnapshotGenerationRetirement"

expected=${FST_SNAPSHOT_RETIREMENT_BINARY_SHA256:-}
if [[ ! "$expected" =~ ^[0-9a-f]{64}$ ]]; then
    printf 'ERROR: FST_SNAPSHOT_RETIREMENT_BINARY_SHA256 must be a lowercase SHA-256.\n' >&2
    exit 64
fi

if [[ ! -f "$BINARY" || ! -x "$BINARY" ]]; then
    printf 'ERROR: prebuilt single-file retirement executable is missing: %s\n' "$BINARY" >&2
    exit 1
fi

actual=$(sha256sum "$BINARY" | cut -d' ' -f1)
if [[ "$actual" != "$expected" ]]; then
    printf 'ERROR: prebuilt retirement executable SHA-256 differs from the approved value.\n' >&2
    exit 1
fi

export FST_SNAPSHOT_RETIREMENT_BINARY_PATH="$BINARY"
exec "$BINARY" "$@"
