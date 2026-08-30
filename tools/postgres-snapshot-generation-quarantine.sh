#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd -P)"
PROJECT="$REPO_ROOT/tools/FstSnapshotGenerationQuarantine/FstSnapshotGenerationQuarantine.csproj"
DLL="$REPO_ROOT/tools/FstSnapshotGenerationQuarantine/bin/Release/net9.0/FstSnapshotGenerationQuarantine.dll"

dotnet build "$PROJECT" -c Release --nologo >/dev/null
exec dotnet "$DLL" "$@"
