#!/usr/bin/env python3
"""Retire the redundant per-partition band projection team/generation indexes.

``ix_cble_{duets,trios,quad}_team_scope_generation`` repeat the partitioned
``ix_cble_team_song`` key (band_type, team_key, song_id, ranking_scope,
scope_combo_id) with ``projection_generation`` appended. Every reader keys on the
shared (band_type, team_key) prefix, so the planner already uses
``ix_cble_team_song`` for the trios and quad partitions; the extra indexes only
cost writes on a table that rewrites about 15 million rows per scrape.

Commands:
  plan      read-only: definitions, sizes, use counts, and the fallback index
  apply     DROP INDEX CONCURRENTLY each present index (network-bound window only)
  rollback  CREATE INDEX CONCURRENTLY each missing index from its recorded definition
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import pathlib
import subprocess
import sys
import time
import types
import urllib.request


class RetirementError(RuntimeError):
    pass


class TransientRefusal(RetirementError):
    """A live-safety deferral; retry in a later window."""


def _psql(sql: str, timeout: int = 900) -> str:
    result = subprocess.run(
        ["docker", "exec", "-i", "fst-postgres", "psql", "-X", "-q", "-At", "-v", "ON_ERROR_STOP=1",
         "-U", "fst", "-d", "fstservice"],
        input=sql, capture_output=True, text=True, timeout=timeout)
    if result.returncode != 0:
        raise RetirementError(f"psql failed: {result.stderr.strip()[:500]}")
    return result.stdout.strip()


def _quote(value: str) -> str:
    return "'" + value.replace("'", "''") + "'"


def _utcnow() -> str:
    return dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def _append_manifest(path: pathlib.Path, entry: dict) -> None:
    with path.open("a") as handle:
        handle.write(json.dumps(entry, sort_keys=True) + "\n")
        handle.flush()
        os.fsync(handle.fileno())


def _probe_window(url: str) -> tuple[bool, str]:
    """Open only while the live scrape fetches leaderboard pages (network-bound)."""
    try:
        with urllib.request.urlopen(url, timeout=10) as response:
            update = json.load(response).get("currentUpdate") or {}
    except Exception as error:  # an unreachable API is never a safe window
        return False, f"service-info unavailable: {error}"
    if (update.get("status") or "idle") == "idle":
        return False, "worker idle"
    if update.get("phaseId") != "scrape.leaderboards":
        return False, f"phase {update.get('phaseId')}"
    if update.get("subOperation") not in ("fetching_leaderboards", "fetching_pages", "awaiting_band"):
        return False, f"sub-operation {update.get('subOperation')}"
    return True, f"scrape {update.get('scrapeId')} fetching"


def _preflight() -> None:
    for container in ("fst-postgres", "fstservice"):
        state = subprocess.run(
            ["docker", "inspect", container, "--format",
             "{{.State.Status}}|{{if .State.Health}}{{.State.Health.Status}}{{end}}"],
            capture_output=True, text=True, timeout=20).stdout.strip()
        if state != "running|healthy":
            raise TransientRefusal(f"{container} is not running and healthy ({state})")
    waiting = int(_psql("SELECT count(*) FROM pg_locks WHERE NOT granted;") or 0)
    if waiting > 20:
        raise TransientRefusal(f"{waiting} lock waits are active; deferring")


retire = types.SimpleNamespace(
    psql=_psql, quote=_quote, utcnow=_utcnow, append_manifest=_append_manifest, probe_window=_probe_window,
    preflight=_preflight, SERVICE_INFO_URL="http://127.0.0.1:8081/api/service-info")

COLUMNS = "(band_type, team_key, song_id, ranking_scope, scope_combo_id, projection_generation)"
FALLBACK_PARENT = "ix_cble_team_song"
FALLBACK_COLUMNS = "(band_type, team_key, song_id, ranking_scope, scope_combo_id)"
INDEXES = tuple(
    {"name": f"ix_cble_{band}_team_scope_generation",
     "table": f"current_band_leaderboard_entries_{band}",
     "definition": f"CREATE INDEX ix_cble_{band}_team_scope_generation ON public.current_band_leaderboard_entries_{band} "
                   f"USING btree {COLUMNS}"}
    for band in ("duets", "trios", "quad"))
EVIDENCE_ROOT = pathlib.Path(
    "/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/band-projection-index-retirement")
ATTEMPTS = 12


def inspect() -> list[dict]:
    """Current state of every retirement target and its fallback (read-only)."""
    rows = []
    for spec in INDEXES:
        out = retire.psql(f"""
SELECT json_build_object(
  'present', ix.indexrelid IS NOT NULL,
  'definition', pg_get_indexdef(ix.indexrelid),
  'valid', ix.indisvalid, 'ready', ix.indisready,
  'attached', EXISTS (SELECT 1 FROM pg_inherits WHERE inhrelid = ix.indexrelid),
  'bytes', pg_relation_size(ix.indexrelid),
  'scans', (SELECT idx_scan FROM pg_stat_user_indexes WHERE indexrelid = ix.indexrelid),
  'fallback', (SELECT json_build_object('name', f.indexrelid::regclass::text, 'valid', f.indisvalid,
                                        'ready', f.indisready, 'definition', pg_get_indexdef(f.indexrelid))
               FROM pg_index f JOIN pg_inherits fi ON fi.inhrelid = f.indexrelid
               WHERE f.indrelid = to_regclass({retire.quote('public.' + spec['table'])})
                 AND fi.inhparent = to_regclass({retire.quote('public.' + FALLBACK_PARENT)})))
FROM (SELECT 1) one
LEFT JOIN pg_index ix ON ix.indexrelid = to_regclass({retire.quote('public.' + spec['name'])});""")
        rows.append({**spec, **json.loads(out)})
    return rows


def blocker(row: dict) -> str | None:
    fallback = row.get("fallback") or {}
    if not (fallback.get("valid") and fallback.get("ready")
            and (fallback.get("definition") or "").endswith(f"USING btree {FALLBACK_COLUMNS}")):
        return f"fallback {FALLBACK_PARENT} is missing or not valid on {row['table']}"
    if not row.get("present"):
        return None
    if row.get("definition") != row["definition_expected"]:
        return f"{row['name']} definition differs from the retired shape: {row.get('definition')}"
    if row.get("attached"):
        return f"{row['name']} is attached to a partitioned index"
    return None


def annotate(rows: list[dict]) -> list[dict]:
    for row in rows:
        row["definition_expected"] = next(s["definition"] for s in INDEXES if s["name"] == row["name"])
        row["blocker"] = blocker(row)
    return rows


def command_plan(args: argparse.Namespace) -> int:
    rows = annotate(inspect())
    for row in rows:
        state = "present" if row["present"] else "absent"
        print(f"{row['name']}: {state} bytes={row.get('bytes') or 0} scans={row.get('scans')} "
              f"fallback={(row.get('fallback') or {}).get('name')} blocker={row['blocker'] or '-'}")
    if args.output:
        pathlib.Path(args.output).write_text(json.dumps(rows, indent=1))
    return 0


def run_concurrently(sql: str, sleep=time.sleep) -> int:
    for attempt in range(1, ATTEMPTS + 1):
        try:
            retire.psql(f"SET lock_timeout = '2s'; SET statement_timeout = 0; {sql}", timeout=7200)
            return attempt
        except RetirementError as error:
            if "lock timeout" not in str(error):
                raise
            if attempt == ATTEMPTS:
                raise TransientRefusal(f"stayed lock-contended after {attempt} attempts: {sql[:80]}") from error
            sleep(min(60, 5 * attempt))
    raise AssertionError("unreachable")


def evidence_dir() -> pathlib.Path:
    path = EVIDENCE_ROOT
    path.mkdir(parents=True, exist_ok=True)
    return path


def record(entry: dict) -> None:
    retire.append_manifest(evidence_dir() / "manifest.jsonl", {**entry, "at": retire.utcnow()})


def command_apply(args: argparse.Namespace) -> int:
    is_open, why = retire.probe_window(args.service_info_url)
    if not is_open:
        print(f"outside the network-bound fetch window ({why}); nothing to do", flush=True)
        return 0
    retire.preflight()
    rows = annotate(inspect())
    blocked = [row for row in rows if row["blocker"]]
    if blocked:
        raise RetirementError("; ".join(row["blocker"] for row in blocked))
    for row in rows:
        if not row["present"]:
            print(f"{row['name']}: already absent", flush=True)
            continue
        is_open, why = retire.probe_window(args.service_info_url)
        if not is_open:
            print(f"window closed ({why}); stopping before {row['name']}", flush=True)
            return 0
        record({"state": "dropping", "name": row["name"], "table": row["table"], "definition": row["definition"],
                "bytes": row["bytes"], "scans": row["scans"], "fallback": row["fallback"]})
        attempts = run_concurrently(f"DROP INDEX CONCURRENTLY IF EXISTS public.{row['name']};")
        after = annotate(inspect())
        if next(r for r in after if r["name"] == row["name"])["present"]:
            raise RetirementError(f"{row['name']} is still present after DROP INDEX CONCURRENTLY")
        record({"state": "dropped", "name": row["name"], "attempts": attempts})
        print(f"dropped {row['name']} reclaimed_gib={row['bytes'] / 1024**3:.2f} attempts={attempts}", flush=True)
    return 0


def command_rollback(args: argparse.Namespace) -> int:
    rows = annotate(inspect())
    for row in rows:
        if row["present"]:
            if not (row.get("valid") and row.get("ready")):
                raise RetirementError(f"{row['name']} exists but is invalid; drop it before rebuilding")
            print(f"{row['name']}: already present", flush=True)
            continue
        attempts = run_concurrently(row["definition_expected"].replace(
            "CREATE INDEX ", "CREATE INDEX CONCURRENTLY IF NOT EXISTS ", 1) + ";")
        rebuilt = next(r for r in annotate(inspect()) if r["name"] == row["name"])
        if not (rebuilt["present"] and rebuilt.get("valid") and rebuilt.get("ready")):
            raise RetirementError(f"{row['name']} was not rebuilt as a valid index")
        record({"state": "rebuilt", "name": row["name"], "attempts": attempts})
        print(f"rebuilt {row['name']}", flush=True)
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    plan = sub.add_parser("plan")
    plan.add_argument("--output")
    apply = sub.add_parser("apply")
    apply.add_argument("--service-info-url", default=retire.SERVICE_INFO_URL)
    sub.add_parser("rollback")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        if args.command == "plan":
            return command_plan(args)
        if args.command == "apply":
            return command_apply(args)
        return command_rollback(args)
    except TransientRefusal as error:
        print(f"deferred: {error}", file=sys.stderr, flush=True)
        return 3
    except RetirementError as error:
        print(f"refused: {error}", file=sys.stderr, flush=True)
        return 2


if __name__ == "__main__":
    sys.exit(main())
