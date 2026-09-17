#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd -P)"
DLL="$REPO_ROOT/tools/FstSnapshotGenerationDrop/bin/Release/net9.0/FstSnapshotGenerationDrop.dll"

expected=${FST_SNAPSHOT_DROP_BINARY_SHA256:-}
if [[ ! "$expected" =~ ^[0-9a-f]{64}$ ]]; then
    printf 'ERROR: FST_SNAPSHOT_DROP_BINARY_SHA256 must be a lowercase SHA-256.\n' >&2
    exit 64
fi

if [[ ! -f "$DLL" ]]; then
    printf 'ERROR: prebuilt drop assembly is missing: %s\n' "$DLL" >&2
    exit 1
fi

actual=$(sha256sum "$DLL" | cut -d' ' -f1)
if [[ "$actual" != "$expected" ]]; then
    printf 'ERROR: prebuilt drop assembly SHA-256 differs from the approved value.\n' >&2
    exit 1
fi

exec dotnet "$DLL" "$@"
