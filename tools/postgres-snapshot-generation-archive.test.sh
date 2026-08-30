#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"

bash -n "$SCRIPT_DIR/postgres-snapshot-generation-archive.sh"
bash -n "$SCRIPT_DIR/postgres-snapshot-generation-archive.test.sh"
python3 -m py_compile \
  "$SCRIPT_DIR/postgres-snapshot-generation-archive.py" \
  "$SCRIPT_DIR/postgres-snapshot-generation-archive-drill.py" \
  "$SCRIPT_DIR/postgres-snapshot-generation-archive.test.py"
python3 "$SCRIPT_DIR/postgres-snapshot-generation-archive.test.py" -q

python3 - "$SCRIPT_DIR/postgres-snapshot-generation-archive.py" <<'PY'
import pathlib
import importlib.util
import sys

path = pathlib.Path(sys.argv[1])
source = path.read_text(encoding="utf-8")
lower = source.lower()
for module in (
    "postgres-snapshot-generation-migration",
    "postgres-pro-bass-snapshot-rewrite",
):
    if module in lower:
        raise SystemExit(f"unsafe module reference: {module}")
spec = importlib.util.spec_from_file_location("archive_static", path)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
for sql in (
    "UPDATE source_relation SET value = 1",
    "WITH changed AS (DELETE FROM source_relation RETURNING *) SELECT * FROM changed",
    "COPY source_relation FROM STDIN",
    "SELECT pg_terminate_backend(1)",
):
    try:
        module.assert_read_only_sql(sql)
    except module.ArchiveError:
        pass
    else:
        raise SystemExit(f"source SQL guard accepted mutation: {sql}")
if '"--network",\n        "none"' not in source:
    raise SystemExit("network-none proof command is missing")
if '"-p"' in source or '"--publish"' in source:
    raise SystemExit("proof command exposes a port option")
PY
