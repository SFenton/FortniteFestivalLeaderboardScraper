#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dll="$root/tools/FstSnapshotGenerationRestoreContinuation/bin/Release/net9.0/FstSnapshotGenerationRestoreContinuation.dll"

if [[ ! -f "$dll" ]]; then
  echo "Continuation binary is missing: $dll" >&2
  exit 1
fi

expected="${FST_SNAPSHOT_RESTORE_CONTINUATION_BINARY_SHA256:-}"
if [[ ! "$expected" =~ ^[0-9a-f]{64}$ ]]; then
  echo "FST_SNAPSHOT_RESTORE_CONTINUATION_BINARY_SHA256 is required." >&2
  exit 1
fi

actual="$(sha256sum "$dll" | awk '{print $1}')"
if [[ "$actual" != "$expected" ]]; then
  echo "Continuation binary SHA-256 differs from the approved value." >&2
  exit 1
fi

exec dotnet "$dll" "$@"
