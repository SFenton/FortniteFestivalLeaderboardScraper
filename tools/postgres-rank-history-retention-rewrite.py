#!/usr/bin/env python3
"""Rewrite rank history down to the retention window (default 30 days).

Rank history lives in ``composite_rank_history`` and the nine instrument
partitions of ``rank_history``. The worker's daily retention deletes only the
rows that one day's cutoff advance supersedes; it refuses to delete most of a
large table row by row. This tool performs the initial reduction instead: for
each table it builds a replacement holding exactly the rows retention keeps
(every row after the cutoff, plus each account's newest row at or before it),
swaps the replacement in with one short lock-bounded transaction, archives the
swapped-out table with ``pg_dump``, and drops it once the archive is verified.

Rows dated before the build day are immutable (only the current day's rows are
written or updated), so the build runs without blocking writers. The swap holds
a SHARE lock on the old table only while it copies rows dated on or after the
build day, so it runs only while the live scrape fetches leaderboard pages
(no rank-history writers) unless ``--window any`` is given.

Commands:
  plan      sizes, estimated rows, retained fraction, and progress per table
  build     build replacements (kept rows dated before today)
  swap      swap built replacements in (fetch window only)
  run       build and swap every table, waiting for fetch windows
  archive   pg_dump each swapped-out table and verify the archive
  drop      drop swapped-out tables whose archive is verified
  rollback  swap one table's original back before it is dropped
"""

from __future__ import annotations

import argparse
import datetime as dt
import fcntl
import importlib.util
import json
import os
import pathlib
import subprocess
import sys
import time

_SPEC = importlib.util.spec_from_file_location(
    "snapshot_archive_retire",
    pathlib.Path(__file__).with_name("postgres-snapshot-archive-retire.py"))
retire = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(retire)
retire.PG_CONTAINER = os.environ.get("FST_RH_PG_CONTAINER", retire.PG_CONTAINER)

RetirementError = retire.RetirementError
TransientRefusal = retire.TransientRefusal

ROOT = pathlib.Path(os.environ.get(
    "FST_RH_ARCHIVE_ROOT",
    "/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/archives/rank-history-retention"))
LOCK_PATH = pathlib.Path(os.environ.get("FST_RH_LOCK_PATH", str(retire.LOCK_PATH)))
PARENT = "rank_history"
PARENT_BRIN = "ix_rh_retention_cutoff_brin"
COMPOSITE = "composite_rank_history"
COMPOSITE_BRIN = "ix_crh_retention_cutoff_brin"
PARTITIONS = {
    "rank_history_pro_cymbals": "Solo_PeripheralCymbals",
    "rank_history_pro_drums": "Solo_PeripheralDrums",
    "rank_history_pro_guitar": "Solo_PeripheralGuitar",
    "rank_history_solo_vocals": "Solo_Vocals",
    "rank_history_solo_drums": "Solo_Drums",
    "rank_history_solo_guitar": "Solo_Guitar",
    "rank_history_pro_vocals": "Solo_PeripheralVocals",
    "rank_history_solo_bass": "Solo_Bass",
    "rank_history_pro_bass": "Solo_PeripheralBass",
}
# Smallest first, so the first windows prove the whole path cheaply.
TABLES = list(PARTITIONS) + [COMPOSITE]
SWAP_ATTEMPTS = 12
TRANSIENT_ERRORS = ("lock timeout", "deadlock detected")
BRIN_OPTIONS = "WITH (pages_per_range = 128, autosummarize = on)"


def quote(value: str) -> str:
    return retire.quote(value)


def psql(sql: str, timeout: int = 900) -> str:
    return retire.psql(sql, timeout=timeout)


def utc_today() -> dt.date:
    return dt.datetime.now(dt.timezone.utc).date()


def names(table: str) -> dict:
    return {
        "new": f"{table}_rtn_new", "old": f"{table}_rtn_old",
        "new_pkey": f"{table}_rtn_new_pkey", "old_pkey": f"{table}_rtn_old_pkey",
        "new_brin": f"{table}_rtn_new_brin", "old_brin": f"{table}_rtn_old_brin",
        "pkey": f"{table}_pkey",
        "brin": COMPOSITE_BRIN if table == COMPOSITE else f"{table}_snapshot_date_brin",
        "bound": f"{table}_rtn_bound",
        "scope": "composite" if table == COMPOSITE else f"solo:{PARTITIONS[table]}",
    }


def key_columns(table: str) -> str:
    return "account_id, snapshot_date" if table == COMPOSITE else "account_id, instrument, snapshot_date"


# ── manifest ─────────────────────────────────────────────────────────

def manifest_path() -> pathlib.Path:
    return ROOT / "manifest.json"


def load_manifest() -> dict:
    path = manifest_path()
    return json.loads(path.read_text()) if path.exists() else {}


def save_manifest(manifest: dict) -> None:
    ROOT.mkdir(parents=True, exist_ok=True)
    partial = manifest_path().with_suffix(".partial")
    partial.write_text(json.dumps(manifest, indent=2, sort_keys=True))
    os.replace(partial, manifest_path())


def update(manifest: dict, table: str, **fields) -> None:
    manifest.setdefault(table, {}).update(fields)
    save_manifest(manifest)


def exists(relation: str) -> bool:
    return psql(f"SELECT to_regclass({quote('public.' + relation)}) IS NOT NULL;") == "t"


def is_partition(relation: str) -> bool:
    return psql(f"SELECT relispartition FROM pg_class WHERE oid = to_regclass({quote('public.' + relation)});") == "t"


# ── safety ───────────────────────────────────────────────────────────

def require_window(window: str) -> str:
    if window == "any":
        return "window check disabled"
    ok, reason = retire.probe_window(retire.SERVICE_INFO_URL)
    if not ok:
        raise TransientRefusal(f"outside the network-bound fetch window ({reason})")
    return reason


class run_lock:
    """Shares the snapshot retirement/compaction flock, so no IO-heavy archive job overlaps."""

    def __enter__(self):
        LOCK_PATH.parent.mkdir(parents=True, exist_ok=True)
        self.handle = LOCK_PATH.open("w")
        try:
            fcntl.flock(self.handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            self.handle.close()
            raise TransientRefusal("a snapshot retirement or compaction run holds the lock")
        return self

    def __exit__(self, *exc):
        fcntl.flock(self.handle, fcntl.LOCK_UN)
        self.handle.close()


def ensure_state_table() -> None:
    psql("""
CREATE TABLE IF NOT EXISTS rank_history_retention_state (
    scope            TEXT        PRIMARY KEY,
    retained_cutoff  DATE        NOT NULL,
    updated_at       TIMESTAMPTZ NOT NULL DEFAULT now());""")


def ensure_parent_brin() -> None:
    """The partitioned BRIN index is created ON ONLY the parent; each swap attaches
    its partition's BRIN index, and the parent becomes valid with the last one."""
    if exists(PARENT_BRIN):
        return
    psql(f"SET lock_timeout = '2s'; CREATE INDEX IF NOT EXISTS {PARENT_BRIN} ON ONLY public.{PARENT} "
         f"USING brin (snapshot_date) {BRIN_OPTIONS};", timeout=120)


# ── plan ─────────────────────────────────────────────────────────────

def table_sizes() -> dict:
    out = psql(f"""
SELECT coalesce(json_object_agg(c.relname, json_build_object(
    'bytes', pg_total_relation_size(c.oid), 'rows', greatest(c.reltuples, 0)::bigint)), '{{}}')
FROM pg_class c WHERE c.relname = ANY(ARRAY[{",".join(quote(t) for t in TABLES)}]) AND c.relkind = 'r';""")
    return json.loads(out)


def retained_fraction(table: str, cutoff: dt.date) -> float:
    out = psql(f"SELECT coalesce(avg((snapshot_date > {quote(cutoff.isoformat())}::date)::int), 0) "
               f"FROM public.{table} TABLESAMPLE SYSTEM (0.2);", timeout=600)
    return float(out or 0)


def command_plan(args: argparse.Namespace) -> int:
    cutoff = utc_today() - dt.timedelta(days=args.retention_days)
    sizes, manifest = table_sizes(), load_manifest()
    print(f"cutoff {cutoff} (retention {args.retention_days} days)")
    total = kept = 0.0
    for table in TABLES:
        size = sizes.get(table, {"bytes": 0, "rows": 0})
        fraction = retained_fraction(table, cutoff) if args.sample else None
        total += size["bytes"]
        if fraction is not None:
            kept += size["bytes"] * fraction
        state = manifest.get(table, {}).get("status", "pending")
        print(f"{table:28s} {size['bytes'] / 1e9:8.1f} GB {size['rows']:>13,} rows "
              f"{'' if fraction is None else f'{fraction * 100:5.1f}% after cutoff'}  {state}")
    print(f"total {total / 1e9:.1f} GB" + (f", about {kept / 1e9:.1f} GB after cutoff" if args.sample else ""))
    return 0


# ── build ────────────────────────────────────────────────────────────

def build(table: str, cutoff: dt.date, build_day: dt.date) -> dict:
    n = names(table)
    if exists(n["new"]):
        if is_partition(n["new"]):
            raise RetirementError(f"leftover {n['new']} is attached; refusing to touch it")
        psql(f"SET lock_timeout = '10s'; DROP TABLE public.{n['new']} RESTRICT;", timeout=120)
    c, b = quote(cutoff.isoformat()), quote(build_day.isoformat())
    statements = [
        # One snapshot for the aggregate and the copy, so the verification counts match.
        "BEGIN ISOLATION LEVEL REPEATABLE READ;",
        "SET LOCAL statement_timeout = '6h';",
        "SET LOCAL work_mem = '1GB';",
        # NOT NULL constraints copy; no other constraint or index does.
        f"CREATE TABLE public.{n['new']} (LIKE public.{table} INCLUDING DEFAULTS "
        f"INCLUDING STORAGE INCLUDING COMPRESSION) WITH (fillfactor = 100);",
        # One scan: per-date row counts (for verification) and each account's newest
        # date at or before the cutoff.
        f"""CREATE TEMP TABLE _rtn_stats ON COMMIT DROP AS
SELECT account_id, snapshot_date,
       max(snapshot_date) FILTER (WHERE snapshot_date <= {c}::date) AS boundary,
       count(*) AS n
FROM public.{table} WHERE snapshot_date < {b}::date
GROUP BY GROUPING SETS ((account_id), (snapshot_date));""",
        f"""INSERT INTO public.{n['new']}
SELECT o.* FROM public.{table} o
LEFT JOIN (SELECT account_id, boundary FROM _rtn_stats
           WHERE account_id IS NOT NULL AND boundary IS NOT NULL) k
  ON k.account_id = o.account_id AND k.boundary = o.snapshot_date
WHERE o.snapshot_date < {b}::date AND (o.snapshot_date > {c}::date OR k.account_id IS NOT NULL);""",
    ]
    if table != COMPOSITE:
        statements.append(f"ALTER TABLE public.{n['new']} ADD CONSTRAINT {n['bound']} "
                          f"CHECK (instrument = {quote(PARTITIONS[table])});")
    statements += [
        f"CREATE UNIQUE INDEX {n['new_pkey']} ON public.{n['new']} ({key_columns(table)});",
        f"ALTER TABLE public.{n['new']} ADD CONSTRAINT {n['new_pkey']} PRIMARY KEY USING INDEX {n['new_pkey']};",
        f"CREATE INDEX {n['new_brin']} ON public.{n['new']} USING brin (snapshot_date) {BRIN_OPTIONS};",
        # Pages written later (the current day's rows) keep room for HOT updates.
        f"ALTER TABLE public.{n['new']} SET (fillfactor = 70);",
        f"""SELECT json_build_object(
  'source_by_date', (SELECT coalesce(json_object_agg(snapshot_date, n), '{{}}') FROM _rtn_stats
                     WHERE account_id IS NULL AND snapshot_date > {c}::date),
  'source_rows', (SELECT coalesce(sum(n), 0) FROM _rtn_stats WHERE account_id IS NULL),
  'boundary_accounts', (SELECT count(*) FROM _rtn_stats WHERE account_id IS NOT NULL AND boundary IS NOT NULL));""",
        "COMMIT;",
    ]
    out = psql("\n".join(statements), timeout=6 * 3600 + 600)
    stats = json.loads(out.splitlines()[-1])
    psql(f"ANALYZE public.{n['new']};", timeout=3600)
    built = json.loads(psql(f"""
SELECT json_build_object(
  'by_date', (SELECT coalesce(json_object_agg(snapshot_date, n), '{{}}') FROM (
      SELECT snapshot_date, count(*) AS n FROM public.{n['new']}
      WHERE snapshot_date > {c}::date GROUP BY snapshot_date) d),
  'boundary_rows', (SELECT count(*) FROM public.{n['new']} WHERE snapshot_date <= {c}::date),
  'boundary_accounts', (SELECT count(DISTINCT account_id) FROM public.{n['new']} WHERE snapshot_date <= {c}::date),
  'rows', (SELECT count(*) FROM public.{n['new']}),
  'bytes', pg_total_relation_size({quote('public.' + n['new'])}::regclass));""", timeout=3600))
    if built["by_date"] != stats["source_by_date"]:
        raise RetirementError(f"{table}: per-date counts after the cutoff differ between source and replacement")
    if not (built["boundary_rows"] == built["boundary_accounts"] == stats["boundary_accounts"]):
        raise RetirementError(f"{table}: boundary rows {built['boundary_rows']}/{built['boundary_accounts']} "
                              f"!= accounts {stats['boundary_accounts']}")
    return {"source_rows_before_build_day": stats["source_rows"], "boundary_accounts": stats["boundary_accounts"],
            "replacement_rows": built["rows"], "replacement_bytes": built["bytes"]}


def command_build(args: argparse.Namespace, tables: list[str] | None = None) -> int:
    manifest = load_manifest()
    with run_lock():
        for table in tables or selected(args):
            if manifest.get(table, {}).get("status") not in (None, "pending", "built"):
                continue
            require_window(args.window)
            retire.preflight()
            build_day = utc_today()
            cutoff = build_day - dt.timedelta(days=args.retention_days)
            started = time.monotonic()
            result = build(table, cutoff, build_day)
            update(manifest, table, status="built", cutoff=cutoff.isoformat(), build_day=build_day.isoformat(),
                   built_at=retire.utcnow(), build_seconds=round(time.monotonic() - started), **result)
            print(f"{retire.utcnow()} built {table}: {result['replacement_rows']:,} rows "
                  f"({result['replacement_bytes'] / 1e9:.1f} GB) from {result['source_rows_before_build_day']:,}",
                  flush=True)
    return 0


# ── swap ─────────────────────────────────────────────────────────────

def swap_sql(table: str, entry: dict) -> str:
    n = names(table)
    b = quote(entry["build_day"])
    lines = [
        "BEGIN;",
        # lock_timeout stays below the server's 1 s deadlock_timeout.
        "SET LOCAL lock_timeout = '500ms';",
        "SET LOCAL statement_timeout = '15min';",
        f"LOCK TABLE public.{table} IN SHARE MODE;",
        f"INSERT INTO public.{n['new']} SELECT * FROM public.{table} WHERE snapshot_date >= {b}::date;",
    ]
    if table == COMPOSITE:
        lines += [
            f"ALTER TABLE public.{table} RENAME TO {n['old']};",
            f"ALTER INDEX public.{n['pkey']} RENAME TO {n['old_pkey']};",
            f"ALTER INDEX public.{COMPOSITE_BRIN} RENAME TO {n['old_brin']};",
            f"ALTER TABLE public.{n['new']} RENAME TO {table};",
            f"ALTER INDEX public.{n['new_pkey']} RENAME TO {n['pkey']};",
            f"ALTER INDEX public.{n['new_brin']} RENAME TO {COMPOSITE_BRIN};",
        ]
    else:
        lines += [
            f"ALTER TABLE public.{PARENT} DETACH PARTITION public.{table};",
            f"ALTER TABLE public.{table} RENAME TO {n['old']};",
            f"ALTER INDEX public.{n['pkey']} RENAME TO {n['old_pkey']};",
            f"ALTER TABLE public.{n['new']} RENAME TO {table};",
            f"ALTER INDEX public.{n['new_pkey']} RENAME TO {n['pkey']};",
            f"ALTER INDEX public.{n['new_brin']} RENAME TO {n['brin']};",
            f"ALTER TABLE public.{PARENT} ATTACH PARTITION public.{table} FOR VALUES IN ({quote(PARTITIONS[table])});",
            # ATTACH adopts the BRIN index but only an explicit index attach revalidates the parent.
            f"ALTER INDEX public.{PARENT_BRIN} ATTACH PARTITION public.{n['brin']};",
        ]
    lines += [
        f"""INSERT INTO rank_history_retention_state (scope, retained_cutoff, updated_at)
VALUES ({quote(n['scope'])}, {quote(entry['cutoff'])}::date, now())
ON CONFLICT (scope) DO UPDATE SET
  retained_cutoff = GREATEST(rank_history_retention_state.retained_cutoff, EXCLUDED.retained_cutoff),
  updated_at = now();""",
        f"SELECT count(*) FROM public.{table} WHERE snapshot_date >= {b}::date;",
        "COMMIT;",
    ]
    return "\n".join(lines)


def run_with_lock_retries(sql: str, what: str, timeout: int, sleep=time.sleep) -> tuple[str, int]:
    for attempt in range(1, SWAP_ATTEMPTS + 1):
        try:
            return psql(sql, timeout=timeout), attempt
        except RetirementError as error:
            if not any(marker in str(error) for marker in TRANSIENT_ERRORS):
                raise
            if attempt == SWAP_ATTEMPTS:
                raise TransientRefusal(f"{what} stayed lock-contended after {attempt} attempts") from error
            sleep(min(30, 3 * attempt))
    raise AssertionError("unreachable")


def verify_swapped(table: str) -> dict:
    n = names(table)
    out = json.loads(psql(f"""
SELECT json_build_object(
  'attached', c.relispartition,
  'bound', pg_get_expr(c.relpartbound, c.oid),
  'indexes', (SELECT json_agg(ic.relname ORDER BY ic.relname) FROM pg_index ix
              JOIN pg_class ic ON ic.oid = ix.indexrelid WHERE ix.indrelid = c.oid),
  'old_standalone', (SELECT NOT relispartition FROM pg_class WHERE oid = to_regclass({quote('public.' + n['old'])})),
  'bytes', pg_total_relation_size(c.oid))
FROM pg_class c WHERE c.oid = to_regclass({quote('public.' + table)});"""))
    expected_indexes = sorted([n["pkey"], n["brin"]])
    if out["indexes"] != expected_indexes or not out["old_standalone"]:
        raise RetirementError(f"{table} swap verification failed: {out}")
    if table != COMPOSITE and not (out["attached"] and out["bound"] == f"FOR VALUES IN ('{PARTITIONS[table]}')"):
        raise RetirementError(f"{table} is not attached as expected: {out}")
    return out


def drop_bound(table: str) -> bool:
    if table == COMPOSITE:
        return True
    try:
        psql(f"SET lock_timeout = '2s'; ALTER TABLE public.{table} DROP CONSTRAINT IF EXISTS {names(table)['bound']};",
             timeout=60)
        return True
    except RetirementError as error:
        if "lock timeout" in str(error):
            return False
        raise


def command_swap(args: argparse.Namespace, tables: list[str] | None = None) -> int:
    manifest = load_manifest()
    ensure_state_table()
    with run_lock():
        for table in tables or selected(args):
            entry = manifest.get(table, {})
            if entry.get("status") == "swapped" and not entry.get("bound_dropped"):
                update(manifest, table, bound_dropped=drop_bound(table))
            if entry.get("status") != "built":
                continue
            if not exists(names(table)["new"]) or is_partition(names(table)["new"]):
                raise RetirementError(f"{table}: built replacement {names(table)['new']} is missing or attached")
            reason = require_window(args.window)
            if table != COMPOSITE:
                ensure_parent_brin()
            started = time.monotonic()
            out, attempts = run_with_lock_retries(swap_sql(table, entry), f"swap of {table}", timeout=1200)
            current_rows = int(out.splitlines()[-1])
            verified = verify_swapped(table)
            update(manifest, table, status="swapped", swapped_at=retire.utcnow(), swap_attempts=attempts,
                   swap_seconds=round(time.monotonic() - started), rows_from_build_day=current_rows,
                   swapped_bytes=verified["bytes"], window=reason, bound_dropped=drop_bound(table))
            print(f"{retire.utcnow()} swapped {table} in {attempts} attempt(s); {current_rows:,} rows dated "
                  f"from {entry['build_day']} copied under lock", flush=True)
    return 0


def command_run(args: argparse.Namespace) -> int:
    """Builds and swaps every table, one at a time, waiting for fetch windows."""
    deadline = time.monotonic() + args.max_hours * 3600
    for table in selected(args):
        while True:
            status = load_manifest().get(table, {}).get("status", "pending")
            if status not in ("pending", "built"):
                break
            if time.monotonic() > deadline:
                print(f"{retire.utcnow()} stopping: --max-hours reached", flush=True)
                return 0
            try:
                if status == "pending":
                    command_build(args, [table])
                else:
                    command_swap(args, [table])
            except TransientRefusal as refusal:
                print(f"{retire.utcnow()} waiting: {refusal}", flush=True)
                time.sleep(args.poll_seconds)
    return 0


# ── archive and drop ─────────────────────────────────────────────────

def command_archive(args: argparse.Namespace) -> int:
    manifest = load_manifest()
    with run_lock():
        for table in selected(args):
            entry = manifest.get(table, {})
            if entry.get("status") != "swapped":
                continue
            n = names(table)
            path = ROOT / f"{n['old']}.dump"
            # The swapped-out table is detached and never written again: it holds the
            # rows counted at build time plus the rows copied under the swap lock.
            rows = int(entry["source_rows_before_build_day"]) + int(entry["rows_from_build_day"])
            retire.dump_child({"schema": "public", "relation": n["old"]}, path)
            toc = retire.drill_run(path, "pg_restore -l /archive/child.dump")
            for marker in (f"TABLE public {n['old']} ", f"TABLE DATA public {n['old']} ", "CONSTRAINT"):
                if marker not in toc:
                    raise RetirementError(f"{path.name}: archive TOC lacks {marker.strip()}")
            counted = int(retire.drill_run(path, (
                "pg_restore -f - --data-only /archive/child.dump | "
                "awk '/^COPY /{on=1;next} /^\\\\\\.$/{on=0} on{n++} END{print n+0}'"), timeout=6 * 3600))
            if counted != rows:
                raise RetirementError(f"{path.name}: archive holds {counted} rows, table holds {rows}")
            update(manifest, table, status="archived", archived_at=retire.utcnow(), archive_path=str(path),
                   archive_bytes=path.stat().st_size, archive_sha256=retire.sha256_file(path), archive_rows=counted)
            print(f"{retire.utcnow()} archived {n['old']}: {counted:,} rows, {path.stat().st_size / 1e9:.1f} GB",
                  flush=True)
    return 0


def command_drop(args: argparse.Namespace) -> int:
    manifest = load_manifest()
    for table in selected(args):
        entry = manifest.get(table, {})
        if entry.get("status") != "archived":
            continue
        n, path = names(table), pathlib.Path(entry["archive_path"])
        if not path.exists() or retire.sha256_file(path) != entry["archive_sha256"]:
            raise RetirementError(f"{table}: archive {path} is missing or changed")
        if is_partition(n["old"]):
            raise RetirementError(f"{n['old']} is attached; refusing to drop it")
        bytes_before = int(psql(f"SELECT pg_total_relation_size({quote('public.' + n['old'])}::regclass);"))
        psql(f"SET lock_timeout = '10s'; DROP TABLE public.{n['old']} RESTRICT;", timeout=300)
        update(manifest, table, status="dropped", dropped_at=retire.utcnow(), reclaimed_bytes=bytes_before)
        print(f"{retire.utcnow()} dropped {n['old']} ({bytes_before / 1e9:.1f} GB)", flush=True)
    return 0


# ── rollback ─────────────────────────────────────────────────────────

def rollback_sql(table: str, entry: dict) -> str:
    """Copies rows the replacement gained since the build day into the original
    (upsert), then puts the original back under the canonical names."""
    n = names(table)
    columns = psql(f"""
SELECT string_agg(quote_ident(attname), ', ' ORDER BY attnum) FROM pg_attribute
WHERE attrelid = {quote('public.' + table)}::regclass AND attnum > 0 AND NOT attisdropped;""").split(", ")
    keys = [k.strip() for k in key_columns(table).split(",")]
    updates = ", ".join(f"{c} = EXCLUDED.{c}" for c in columns if c not in keys)
    lines = [
        "BEGIN;",
        "SET LOCAL lock_timeout = '500ms';",
        "SET LOCAL statement_timeout = '30min';",
        f"LOCK TABLE public.{table} IN SHARE MODE;",
        f"INSERT INTO public.{n['old']} ({', '.join(columns)}) SELECT {', '.join(columns)} FROM public.{table} "
        f"WHERE snapshot_date >= {quote(entry['build_day'])}::date "
        f"ON CONFLICT ({key_columns(table)}) DO UPDATE SET {updates};",
    ]
    if table == COMPOSITE:
        lines += [
            f"ALTER TABLE public.{table} RENAME TO {n['new']};",
            f"ALTER INDEX public.{n['pkey']} RENAME TO {n['new_pkey']};",
            f"ALTER INDEX public.{COMPOSITE_BRIN} RENAME TO {n['new_brin']};",
            f"ALTER TABLE public.{n['old']} RENAME TO {table};",
            f"ALTER INDEX public.{n['old_pkey']} RENAME TO {n['pkey']};",
            f"ALTER INDEX public.{n['old_brin']} RENAME TO {COMPOSITE_BRIN};",
        ]
    else:
        lines += [
            f"ALTER TABLE public.{PARENT} DETACH PARTITION public.{table};",
            f"ALTER TABLE public.{table} RENAME TO {n['new']};",
            f"ALTER INDEX public.{n['pkey']} RENAME TO {n['new_pkey']};",
            f"ALTER INDEX public.{n['brin']} RENAME TO {n['new_brin']};",
            f"ALTER TABLE public.{n['old']} RENAME TO {table};",
            f"ALTER INDEX public.{n['old_pkey']} RENAME TO {n['pkey']};",
            f"ALTER INDEX public.{n['old_brin']} RENAME TO {n['brin']};",
            f"ALTER TABLE public.{PARENT} ATTACH PARTITION public.{table} FOR VALUES IN ({quote(PARTITIONS[table])});",
            f"ALTER INDEX public.{PARENT_BRIN} ATTACH PARTITION public.{n['brin']};",
        ]
    lines += [f"DELETE FROM rank_history_retention_state WHERE scope = {quote(n['scope'])};", "COMMIT;"]
    return "\n".join(lines)


def prepare_rollback(table: str) -> None:
    """Gives the detached original a validated partition CHECK and a BRIN index
    before the rollback transaction, so ATTACH neither scans it nor builds an
    index while the parent is locked. The original is unused, so this blocks
    nothing."""
    if table == COMPOSITE:
        return
    n = names(table)
    psql(f"""
SET lock_timeout = '2s';
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'public.{n['old']}'::regclass
                 AND conname = {quote(n['bound'])}) THEN
    ALTER TABLE public.{n['old']} ADD CONSTRAINT {n['bound']}
      CHECK (instrument = {quote(PARTITIONS[table])}) NOT VALID;
  END IF;
END $$;
ALTER TABLE public.{n['old']} VALIDATE CONSTRAINT {n['bound']};
CREATE INDEX IF NOT EXISTS {n['old_brin']} ON public.{n['old']} USING brin (snapshot_date) {BRIN_OPTIONS};""",
         timeout=7200)


def command_rollback(args: argparse.Namespace) -> int:
    manifest = load_manifest()
    table = args.table
    entry = manifest.get(table, {})
    if entry.get("status") not in ("swapped", "archived"):
        raise RetirementError(f"{table} is {entry.get('status', 'pending')}; only a swapped table can roll back")
    if not exists(names(table)["old"]):
        raise RetirementError(f"{names(table)['old']} no longer exists; restore it from {entry.get('archive_path')}")
    with run_lock():
        prepare_rollback(table)
        require_window(args.window)
        _, attempts = run_with_lock_retries(rollback_sql(table, entry), f"rollback of {table}", timeout=3600)
        drop_bound(table)
    update(manifest, table, status="rolled_back", rolled_back_at=retire.utcnow(), rollback_attempts=attempts)
    print(f"{retire.utcnow()} rolled back {table}; the replacement is kept as {names(table)['new']}", flush=True)
    return 0


# ── cli ──────────────────────────────────────────────────────────────

def selected(args: argparse.Namespace) -> list[str]:
    chosen = getattr(args, "tables", None) or TABLES
    unknown = [t for t in chosen if t not in TABLES]
    if unknown:
        raise RetirementError(f"unknown tables: {unknown}")
    return [t for t in TABLES if t in chosen]


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    for name in ("plan", "build", "swap", "run", "archive", "drop"):
        p = sub.add_parser(name)
        p.add_argument("--tables", nargs="*", help="limit to these tables")
        p.add_argument("--retention-days", type=int, default=30)
        p.add_argument("--window", choices=("fetch", "any"), default="fetch")
        if name == "plan":
            p.add_argument("--sample", action="store_true", help="sample each table's retained fraction")
        if name == "run":
            p.add_argument("--max-hours", type=float, default=12)
            p.add_argument("--poll-seconds", type=int, default=60)
    p = sub.add_parser("rollback")
    p.add_argument("table", choices=TABLES)
    p.add_argument("--window", choices=("fetch", "any"), default="fetch")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    commands = {"plan": command_plan, "build": command_build, "swap": command_swap, "run": command_run,
                "archive": command_archive, "drop": command_drop, "rollback": command_rollback}
    if getattr(args, "retention_days", 30) < 1:
        raise SystemExit("--retention-days must be at least 1")
    try:
        return commands[args.command](args)
    except TransientRefusal as refusal:
        print(f"{retire.utcnow()} refused: {refusal}", file=sys.stderr)
        return 3


if __name__ == "__main__":
    sys.exit(main())
