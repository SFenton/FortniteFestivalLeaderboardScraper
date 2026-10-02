#!/usr/bin/env python3
"""Archive, verify, detach, and drop retired snapshot-generation children.

Candidates come only from one immutable report-only retention cycle in which
the planner and the independent oracle agree, bound by the operator-supplied
candidate identity hash. Every child is re-proven unreferenced against the live
publication roots immediately before it is touched, archived with pg_dump
(zstd) onto the FST drive, verified by an independent row count in a
network-none PostgreSQL container (with periodic full restore drills that
compare an order-independent content hash), and only then detached
concurrently and dropped without CASCADE. Rollback for any child is one
``pg_restore`` of its archive, which recreates and re-attaches it.

Commands:
  plan     report eligible children and bytes; no mutation
  retire   archive and drop eligible children (bounded by --limit)
  auto     retire the newest agreeing cycle bound to the current publication,
           only while the live scrape is network-bound; integrity failures
           write a disable file that only an operator clears
  restore  restore one retired child from its archive and re-attach it
"""

from __future__ import annotations

import argparse
import datetime as dt
import fcntl
import hashlib
import json
import os
import pathlib
import re
import subprocess
import sys
import time
import urllib.request

PG_CONTAINER = "fst-postgres"
DB_USER = "fst"
DB_NAME = "fstservice"
DRILL_IMAGE = "postgres:17"
ARCHIVE_ROOT = pathlib.Path(
    "/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/archives/snapshot-generations")
FST_DRIVE = pathlib.Path("/mnt/docker-storage")
LOCK_PATH = ARCHIVE_ROOT / ".retirement.lock"
MIN_FREE_BYTES = 100 * 1024**3
ROOT_RELATION = "leaderboard_entries_snapshot"
# Children with operator history in the old quarantine/drop/restore machine.
NEVER = {("Solo_Bass", 1308)}
IDENT = re.compile(r"^[a-z_][a-z0-9_]*$")
SHA256 = re.compile(r"^[0-9a-f]{64}$")
SERVICE_INFO_URL = "http://127.0.0.1:8081/api/service-info"
AUTO_DISABLE_FILE = ARCHIVE_ROOT / "AUTO_DISABLED"
# Network-bound leaderboard fetch sub-operations. Spool drains, index drops,
# flushes, index builds, and every later phase read or write snapshots heavily,
# so anything outside this allow-list closes the window.
NETWORK_BOUND_PHASE = "scrape.leaderboards"
NETWORK_BOUND_SUB_OPERATIONS = frozenset({"fetching_leaderboards", "fetching_pages", "awaiting_band"})


class RetirementError(RuntimeError):
    pass


class TransientRefusal(RetirementError):
    """A live-safety deferral (load, health, disk, contention); retry later."""


def utcnow() -> str:
    return dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def psql(sql: str, timeout: int = 900) -> str:
    result = subprocess.run(
        ["docker", "exec", "-i", PG_CONTAINER, "psql", "-X", "-q", "-At",
         "-v", "ON_ERROR_STOP=1", "-U", DB_USER, "-d", DB_NAME],
        input=sql, capture_output=True, text=True, timeout=timeout)
    if result.returncode != 0:
        raise RetirementError(f"psql failed: {result.stderr.strip()[:500]}")
    return result.stdout.strip()


def parse_child(identity: str) -> dict:
    """Parses one planner child identity string.

    Format: instrument|parentSchema|parentRelation|parentOid|parentRelfilenode|
    parentPartitionKey|parentBound|schema|relation|snapshotId|oid|relfilenode|bound
    """
    parts = identity.split("|")
    if len(parts) != 13:
        raise RetirementError(f"unexpected child identity shape: {identity[:120]}")
    child = {
        "identity": identity,
        "instrument": parts[0],
        "parent_schema": parts[1],
        "parent_relation": parts[2],
        "schema": parts[7],
        "relation": parts[8],
        "snapshot_id": int(parts[9]),
        "oid": int(parts[10]),
        "relfilenode": int(parts[11]),
    }
    for key in ("parent_schema", "parent_relation", "schema", "relation"):
        if not IDENT.match(child[key]):
            raise RetirementError(f"unsafe identifier {key}={child[key]!r}")
    if not child["parent_relation"].startswith(ROOT_RELATION + "_"):
        raise RetirementError(f"child is not below {ROOT_RELATION}: {identity[:120]}")
    if not child["relation"].startswith(child["parent_relation"] + "_s"):
        raise RetirementError(f"relation does not match parent: {identity[:120]}")
    return child


def is_eligible(row: dict) -> tuple[bool, str]:
    checks = [
        (row.get("present"), "missing"),
        (row.get("identity_ok"), "oid-or-relfilenode-changed"),
        (row.get("relispartition"), "not-attached"),
        (row.get("parent_ok"), "parent-changed"),
        (not row.get("live_scope"), "bound-by-live-publication"),
        (not row.get("active_state"), "active-snapshot-state"),
        (not row.get("projection_source"), "solo-projection-source"),
        (not row.get("ever_held"), "retention-hold-history"),
        (not row.get("has_trigger"), "has-trigger"),
        (not row.get("surface_binding"), "publication-surface-binding"),
        ((row["instrument"], row["snapshot_id"]) not in NEVER, "operator-excluded"),
    ]
    for ok, reason in checks:
        if not ok:
            return False, reason
    return True, "eligible"


def load_cycle(cycle_id: int, expected_hash: str) -> list[dict]:
    if not SHA256.match(expected_hash):
        raise RetirementError("expected candidate hash must be 64 lowercase hex characters")
    row = json.loads(psql(f"""
        SELECT row_to_json(c) FROM (
          SELECT cycle_id, report_only, status, oracle_agreement, candidate_identity_hash,
                 global_blockers::text AS global_blockers, candidate_count,
                 planner_candidate_set::jsonb AS planner, oracle_candidate_set::jsonb AS oracle
          FROM snapshot_generation_retention_cycles WHERE cycle_id = {int(cycle_id)}) c;"""))
    if not (row["report_only"] and row["status"] == "observed" and row["oracle_agreement"]):
        raise RetirementError("cycle is not an observed report-only cycle with oracle agreement")
    if row["candidate_identity_hash"] != expected_hash:
        raise RetirementError("candidate identity hash does not match the authorized cycle")
    if json.loads(row["global_blockers"] or "[]"):
        raise RetirementError("cycle recorded global blockers")
    if sorted(row["planner"]) != sorted(row["oracle"]):
        raise RetirementError("planner and oracle candidate sets differ")
    children = [parse_child(item) for item in row["planner"]]
    if len(children) != row["candidate_count"]:
        raise RetirementError("candidate count does not match the candidate set")
    return children


def live_status(children: list[dict]) -> list[dict]:
    """Re-proves each child against live roots in one read-only statement."""
    values = ",".join(
        "({},{},{},{},{},{},{},{})".format(
            quote(c["instrument"]), quote(c["parent_schema"]), quote(c["parent_relation"]),
            quote(c["schema"]), quote(c["relation"]), c["snapshot_id"], c["oid"], c["relfilenode"])
        for c in children)
    sql = f"""
BEGIN READ ONLY;
SET LOCAL statement_timeout = '120s';
WITH cand(instrument, parent_schema, parent_relation, schema, relation, snapshot_id, oid, relfilenode) AS (
  VALUES {values}
),
state AS (
  SELECT current_publication_id, previous_publication_id, working_publication_id
  FROM scrape_publication_state WHERE id
),
live_publications AS (
  SELECT pg.publication_id, pg.scrape_id FROM publication_generations pg, state
  WHERE pg.publication_id IN (state.current_publication_id, state.previous_publication_id,
                              state.working_publication_id)
     OR pg.status IN ('building', 'current')
)
SELECT coalesce(json_agg(r), '[]'::json) FROM (
  SELECT cand.instrument, cand.relation, cand.snapshot_id,
         pc.oid IS NOT NULL AS present,
         (pc.oid = cand.oid::oid AND pc.relfilenode = cand.relfilenode::oid) AS identity_ok,
         coalesce(pc.relispartition, false) AS relispartition,
         (SELECT i.inhparent = to_regclass(format('%I.%I', cand.parent_schema, cand.parent_relation))
            FROM pg_inherits i WHERE i.inhrelid = pc.oid AND NOT i.inhdetachpending) AS parent_ok,
         coalesce(pg_total_relation_size(pc.oid), 0) AS total_bytes,
         coalesce(pg_relation_size(pc.oid), 0) AS heap_bytes,
         EXISTS (SELECT 1 FROM leaderboard_published_scope_source s
                 WHERE s.instrument = cand.instrument AND s.source_snapshot_id = cand.snapshot_id
                   AND s.published_scrape_id IN (SELECT scrape_id FROM live_publications)) AS live_scope,
         EXISTS (SELECT 1 FROM leaderboard_snapshot_state s
                 WHERE s.instrument = cand.instrument AND s.active_snapshot_id = cand.snapshot_id) AS active_state,
         EXISTS (SELECT 1 FROM solo_current_projection_scope s
                 WHERE s.instrument = cand.instrument AND s.source_snapshot_id = cand.snapshot_id) AS projection_source,
         EXISTS (SELECT 1 FROM snapshot_generation_retention_holds h
                 WHERE h.instrument = cand.instrument AND h.snapshot_id = cand.snapshot_id) AS ever_held,
         EXISTS (SELECT 1 FROM pg_trigger t WHERE t.tgrelid = pc.oid AND NOT t.tgisinternal) AS has_trigger,
         EXISTS (SELECT 1 FROM publication_surface_bindings b
                 WHERE b.publication_id IN (SELECT publication_id FROM live_publications)
                   AND strpos(b.binding_json::text, cand.relation) > 0) AS surface_binding
  FROM cand
  LEFT JOIN pg_class pc ON pc.relname = cand.relation
                       AND pc.relnamespace = to_regnamespace(cand.schema)
  ORDER BY cand.snapshot_id, cand.instrument
) r;
COMMIT;
"""
    rows = json.loads(psql(sql, timeout=300))
    by_relation = {c["relation"]: c for c in children}
    for row in rows:
        row.update({k: v for k, v in by_relation[row["relation"]].items() if k not in row})
        row["eligible"], row["reason"] = is_eligible(row)
    return rows


def quote(value: str) -> str:
    return "'" + value.replace("'", "''") + "'"


def preflight() -> None:
    usage = os.statvfs(FST_DRIVE)
    if usage.f_bavail * usage.f_frsize < MIN_FREE_BYTES:
        raise TransientRefusal("FST drive free space is below the safety floor")
    for container in ("fst-postgres", "fstservice"):
        state = subprocess.run(
            ["docker", "inspect", container, "--format",
             "{{.State.Status}}|{{if .State.Health}}{{.State.Health.Status}}{{end}}"],
            capture_output=True, text=True, timeout=20).stdout.strip()
        if state != "running|healthy":
            raise TransientRefusal(f"{container} is not running and healthy ({state})")
    waiting = int(psql("SELECT count(*) FROM pg_locks WHERE NOT granted;") or 0)
    if waiting > 20:
        raise TransientRefusal(f"{waiting} lock waits are active; deferring")


def content_fingerprint(child: dict) -> tuple[int, str]:
    """Row count and order-independent content hash of one child (one scan)."""
    out = psql(f"""
        BEGIN READ ONLY;
        SET LOCAL statement_timeout = '30min';
        SELECT count(*) || '|' || coalesce(sum(hashtextextended(t::text, 0)::numeric), 0)
        FROM {child['schema']}.{child['relation']} t;
        COMMIT;""", timeout=2000)
    count, digest = out.split("|", 1)
    return int(count), digest


def dump_child(child: dict, path: pathlib.Path) -> None:
    partial = path.with_suffix(".partial")
    with partial.open("wb") as output:
        result = subprocess.run(
            ["docker", "exec", PG_CONTAINER, "pg_dump", "-U", DB_USER, "-d", DB_NAME,
             "-Fc", "-Z", "zstd:3", "--no-owner", "--no-privileges",
             "-t", f"{child['schema']}.{child['relation']}"],
            stdout=output, stderr=subprocess.PIPE, timeout=7200)
        output.flush()
        os.fsync(output.fileno())
    if result.returncode != 0:
        partial.unlink(missing_ok=True)
        raise RetirementError(f"pg_dump failed: {result.stderr.decode()[:500]}")
    partial.rename(path)


def sha256_file(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(8 * 1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def drill_run(path: pathlib.Path, script: str, timeout: int = 3600) -> str:
    result = subprocess.run(
        ["docker", "run", "--rm", "--network", "none", "--cpus", "2", "--memory", "2g",
         "-e", "POSTGRES_HOST_AUTH_METHOD=trust",
         "-v", f"{path}:/archive/child.dump:ro", "--entrypoint", "bash", DRILL_IMAGE,
         "-c", script],
        capture_output=True, text=True, timeout=timeout)
    if result.returncode != 0:
        raise RetirementError(f"drill container failed: {result.stderr.strip()[-500:]}")
    return result.stdout.strip()


def verify_archive(child: dict, path: pathlib.Path, rows: int, full_drill: bool,
                   fingerprint: str | None) -> dict:
    relation = child["relation"]
    toc = drill_run(path, "pg_restore -l /archive/child.dump")
    required = [f"TABLE {child['schema']} {relation} ", f"TABLE DATA {child['schema']} {relation} ",
                "CONSTRAINT", "INDEX"]
    for marker in required:
        if marker not in toc:
            raise RetirementError(f"archive TOC lacks {marker.strip()}")
    counted = int(drill_run(path, (
        "pg_restore -f - --data-only /archive/child.dump | "
        "awk '/^COPY /{on=1;next} /^\\\\\\.$/{on=0} on{n++} END{print n+0}'")))
    if counted != rows:
        raise RetirementError(f"archive holds {counted} rows, table held {rows}")
    result = {"toc_ok": True, "archive_rows": counted}
    if full_drill:
        script = f"""
set -euo pipefail
docker-entrypoint.sh postgres -c fsync=off >/tmp/pg.log 2>&1 &
for i in $(seq 1 60); do pg_isready -q -U postgres && break; sleep 1; done
pg_restore -l /archive/child.dump | grep -v ' ATTACH ' > /tmp/list
pg_restore -U postgres -d postgres --no-owner -L /tmp/list /archive/child.dump
psql -U postgres -At -c "SELECT count(*) || '|' || coalesce(sum(hashtextextended(t::text, 0)::numeric), 0) FROM {child['schema']}.{relation} t"
"""
        restored = drill_run(path, script).splitlines()[-1]
        count, digest = restored.split("|", 1)
        if int(count) != rows or digest != fingerprint:
            raise RetirementError("full restore drill did not reproduce the content fingerprint")
        result["restore_drill"] = "passed"
    return result


DETACH_ATTEMPTS = 12


def detach_and_drop(child: dict, sleep=time.sleep) -> int:
    """Detaches then drops one child; returns the number of lock attempts.

    Every instrument parent owns a DEFAULT partition, which rules out
    ``DETACH ... CONCURRENTLY``. A plain DETACH is a catalog-only change but
    needs a brief ACCESS EXCLUSIVE lock on the instrument parent, so a short
    lock_timeout bounds how long queued readers/writers can wait behind it and
    the attempt is retried later instead of queueing indefinitely.
    """
    parent = f"{child['parent_schema']}.{child['parent_relation']}"
    relation = f"{child['schema']}.{child['relation']}"
    for attempt in range(1, DETACH_ATTEMPTS + 1):
        try:
            psql(f"SET lock_timeout = '2s'; SET statement_timeout = '60s'; "
                 f"ALTER TABLE {parent} DETACH PARTITION {relation};", timeout=120)
            break
        except RetirementError as error:
            if "lock timeout" not in str(error):
                raise
            if attempt == DETACH_ATTEMPTS:
                raise TransientRefusal(
                    f"detach of {relation} stayed lock-contended after {attempt} attempts") from error
            sleep(min(60, 5 * attempt))
    still_attached = psql(f"SELECT relispartition FROM pg_class WHERE oid = to_regclass('{relation}');")
    if still_attached != "f":
        raise RetirementError("child is still attached after DETACH")
    psql(f"SET lock_timeout = '10s'; DROP TABLE {relation} RESTRICT;", timeout=120)
    return attempt


def append_manifest(manifest: pathlib.Path, entry: dict) -> None:
    with manifest.open("a") as handle:
        handle.write(json.dumps(entry, sort_keys=True) + "\n")
        handle.flush()
        os.fsync(handle.fileno())


def retired_relations(manifest: pathlib.Path) -> set[str]:
    if not manifest.exists():
        return set()
    done = set()
    for line in manifest.read_text().splitlines():
        entry = json.loads(line)
        if entry.get("state") == "dropped":
            done.add(entry["relation"])
        elif entry.get("state") == "restored":
            done.discard(entry["relation"])
    return done


def cycle_dir(cycle_id: int) -> pathlib.Path:
    return ARCHIVE_ROOT / f"cycle-{cycle_id}"


def command_plan(args: argparse.Namespace) -> int:
    children = load_cycle(args.cycle, args.expected_candidate_hash)
    rows = live_status(children)
    summary: dict[str, list] = {}
    for row in rows:
        summary.setdefault(row["reason"], []).append(row)
    for reason, items in sorted(summary.items()):
        size = sum(int(r["total_bytes"]) for r in items)
        heap = sum(int(r["heap_bytes"]) for r in items)
        print(f"{reason}: children={len(items)} total_gib={size / 1024**3:.1f} heap_gib={heap / 1024**3:.1f}")
    if args.output:
        pathlib.Path(args.output).write_text(json.dumps(rows, indent=1))
    return 0


def command_retire(args: argparse.Namespace, window=None) -> int:
    """Retires one cycle; ``window`` returns (open, reason) and is checked before each child."""
    directory = cycle_dir(args.cycle)
    directory.mkdir(parents=True, exist_ok=True)
    manifest = directory / "manifest.jsonl"
    LOCK_PATH.parent.mkdir(parents=True, exist_ok=True)
    with LOCK_PATH.open("w") as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            raise TransientRefusal("another retirement run holds the lock")
        children = load_cycle(args.cycle, args.expected_candidate_hash)
        if args.only:
            children = [c for c in children if c["relation"] in set(args.only)]
            if len(children) != len(set(args.only)):
                raise RetirementError("--only names a relation outside the authorized cycle")
        done = retired_relations(manifest)
        children = [c for c in children if c["relation"] not in done]
        order = sorted(children, key=lambda c: (c["snapshot_id"], c["instrument"]))
        processed = 0
        for child in order:
            if processed >= args.limit:
                break
            if args.stop_file and pathlib.Path(args.stop_file).exists():
                print("stop file present; ending run", flush=True)
                break
            if window is not None:
                is_open, why = window()
                if not is_open:
                    print(f"window closed ({why}); ending run", flush=True)
                    break
            preflight()
            status = live_status([child])[0]
            if not status["eligible"]:
                print(f"skip {child['relation']}: {status['reason']}", flush=True)
                continue
            started = time.monotonic()
            rows, fingerprint = content_fingerprint(child)
            path = directory / child["instrument"] / f"{child['relation']}.dump"
            path.parent.mkdir(parents=True, exist_ok=True)
            dump_child(child, path)
            full_drill = args.drill_every > 0 and processed % args.drill_every == 0
            verification = verify_archive(child, path, rows, full_drill, fingerprint)
            archive_sha = sha256_file(path)
            base = {
                "cycle_id": args.cycle,
                "candidate_identity_hash": args.expected_candidate_hash,
                "identity": child["identity"],
                "instrument": child["instrument"],
                "relation": child["relation"],
                "snapshot_id": child["snapshot_id"],
                "oid": child["oid"],
                "relfilenode": child["relfilenode"],
                "rows": rows,
                "content_fingerprint": fingerprint,
                "total_bytes": int(status["total_bytes"]),
                "heap_bytes": int(status["heap_bytes"]),
                "archive_path": str(path),
                "archive_bytes": path.stat().st_size,
                "archive_sha256": archive_sha,
                **verification,
            }
            append_manifest(manifest, {**base, "state": "archived", "at": utcnow()})
            if args.archive_only:
                print(f"archived {child['relation']} rows={rows}", flush=True)
                processed += 1
                continue
            # Re-prove immediately before mutation; the archive step can take minutes.
            recheck = live_status([child])[0]
            if not recheck["eligible"]:
                print(f"skip after archive {child['relation']}: {recheck['reason']}", flush=True)
                continue
            attempts = detach_and_drop(child)
            append_manifest(manifest, {**base, "state": "dropped", "detach_attempts": attempts, "at": utcnow()})
            processed += 1
            print(
                f"retired {child['relation']} rows={rows} reclaimed_gib={base['total_bytes'] / 1024**3:.2f} "
                f"archive_mib={base['archive_bytes'] / 1024**2:.1f} drill={verification.get('restore_drill', '-')} "
                f"seconds={time.monotonic() - started:.0f}", flush=True)
            if args.pause_seconds:
                time.sleep(args.pause_seconds)
        print(f"run complete processed={processed}", flush=True)
    return 0


def attach_restored(parent: str, relation: str, snapshot_id: int, sleep=time.sleep) -> None:
    for attempt in range(1, DETACH_ATTEMPTS + 1):
        try:
            psql(f"SET lock_timeout = '2s'; SET statement_timeout = '30min'; "
                 f"ALTER TABLE {parent} ATTACH PARTITION {relation} FOR VALUES IN ({int(snapshot_id)});",
                 timeout=2000)
            return
        except RetirementError as error:
            if "lock timeout" not in str(error) or attempt == DETACH_ATTEMPTS:
                raise
            sleep(min(60, 5 * attempt))


def command_restore(args: argparse.Namespace) -> int:
    manifest = cycle_dir(args.cycle) / "manifest.jsonl"
    entries = [json.loads(line) for line in manifest.read_text().splitlines()]
    matches = [e for e in entries if e["relation"] == args.relation and e["state"] == "dropped"]
    if not matches:
        raise RetirementError("relation has no dropped manifest entry")
    entry = matches[-1]
    child = parse_child(entry["identity"])
    path = pathlib.Path(entry["archive_path"])
    if sha256_file(path) != entry["archive_sha256"]:
        raise RetirementError("archive checksum does not match the manifest")
    relation = f"{child['schema']}.{child['relation']}"
    if psql(f"SELECT to_regclass('{relation}') IS NOT NULL;") == "t":
        raise RetirementError("relation already exists; refusing to overwrite")
    # A single-table archive of an attached partition orders TABLE ATTACH before
    # its own primary key, so restore it standalone and attach afterwards; the
    # attach adopts the restored indexes as partitions of the parent indexes.
    result = subprocess.run(
        ["docker", "run", "--rm", "--network", f"container:{PG_CONTAINER}",
         "-v", f"{path}:/archive/child.dump:ro", "--entrypoint", "bash", DRILL_IMAGE, "-c",
         "set -euo pipefail; pg_restore -l /archive/child.dump | grep -v ' ATTACH ' > /tmp/list; "
         f"pg_restore -h 127.0.0.1 -U {DB_USER} -d {DB_NAME} --no-owner --no-privileges "
         "--exit-on-error --single-transaction -L /tmp/list /archive/child.dump"],
        capture_output=True, text=True, timeout=7200)
    if result.returncode != 0:
        raise RetirementError(f"pg_restore failed: {result.stderr.strip()[-500:]}")
    attach_restored(f"{child['parent_schema']}.{child['parent_relation']}", relation, child["snapshot_id"])
    rows, fingerprint = content_fingerprint(child)
    if rows != entry["rows"] or fingerprint != entry["content_fingerprint"]:
        raise RetirementError("restored child does not match the archived fingerprint")
    append_manifest(manifest, {**entry, "state": "restored", "at": utcnow()})
    print(f"restored {entry['relation']} rows={rows}", flush=True)
    return 0


def fetch_service_info(url: str, timeout: float = 10) -> dict:
    with urllib.request.urlopen(url, timeout=timeout) as response:
        return json.load(response)


def network_bound_window(info: dict) -> tuple[bool, str]:
    """True only while the live scrape fetches leaderboard pages."""
    update = info.get("currentUpdate") or {}
    status = update.get("status") or "idle"
    phase = update.get("phaseId") or "none"
    sub_operation = update.get("subOperation") or ""
    if status == "idle":
        return False, "worker idle"
    if phase != NETWORK_BOUND_PHASE:
        return False, f"phase {phase}"
    if sub_operation not in NETWORK_BOUND_SUB_OPERATIONS:
        return False, f"sub-operation {sub_operation or 'none'}"
    return True, f"scrape {update.get('scrapeId')} {phase}/{sub_operation}"


def probe_window(url: str) -> tuple[bool, str]:
    try:
        return network_bound_window(fetch_service_info(url))
    except Exception as error:  # an unreachable API is never a safe window
        return False, f"service-info unavailable: {error}"


def select_auto_cycle(max_age_hours: float) -> dict | None:
    """Newest agreeing report-only cycle observed for the current publication."""
    out = psql(f"""
        SELECT coalesce(row_to_json(c)::text, '') FROM (
          SELECT r.cycle_id, r.candidate_identity_hash, r.trigger_scrape_id, r.trigger_publication_id
          FROM snapshot_generation_retention_cycles r
          WHERE r.report_only AND r.status = 'observed' AND r.oracle_agreement
            AND coalesce(r.global_blockers::text, '[]') IN ('[]', 'null')
            AND r.created_at > now() - make_interval(secs => {float(max_age_hours) * 3600})
            AND r.trigger_publication_id = (
              SELECT current_publication_id FROM scrape_publication_state WHERE id)
          ORDER BY r.cycle_id DESC LIMIT 1) c;""")
    return json.loads(out) if out else None


def trip_auto(disable_file: pathlib.Path, cycle_id: int | None, error: BaseException) -> None:
    disable_file.parent.mkdir(parents=True, exist_ok=True)
    disable_file.write_text(json.dumps({
        "at": utcnow(), "cycle_id": cycle_id, "error": f"{type(error).__name__}: {error}"[:2000],
        "clear": "investigate, then delete this file to re-enable automatic retirement"},
        indent=1) + "\n")


def command_auto(args: argparse.Namespace) -> int:
    disable_file = pathlib.Path(args.disable_file)
    if disable_file.exists():
        print(f"automatic retirement disabled by {disable_file}; operator must investigate and clear it",
              flush=True)
        return 0
    is_open, why = probe_window(args.service_info_url)
    if not is_open:
        print(f"outside the network-bound fetch window ({why}); nothing to do", flush=True)
        return 0
    cycle = select_auto_cycle(args.max_cycle_age_hours)
    if cycle is None:
        print("no agreeing retention cycle is bound to the current publication; nothing to do", flush=True)
        return 0
    print(f"{utcnow()} auto: cycle {cycle['cycle_id']} (scrape {cycle['trigger_scrape_id']}, publication "
          f"{cycle['trigger_publication_id']}) during {why}", flush=True)
    retire_args = argparse.Namespace(
        cycle=int(cycle["cycle_id"]), expected_candidate_hash=cycle["candidate_identity_hash"],
        limit=args.limit, only=None, drill_every=args.drill_every, pause_seconds=args.pause_seconds,
        stop_file=args.stop_file, archive_only=False)
    def window() -> tuple[bool, str]:
        if disable_file.exists():
            return False, f"{disable_file.name} present"
        return probe_window(args.service_info_url)

    try:
        return command_retire(retire_args, window=window)
    except TransientRefusal as error:
        print(f"deferred: {error}", flush=True)
        return 0
    except BaseException as error:
        if isinstance(error, KeyboardInterrupt):
            raise
        trip_auto(disable_file, int(cycle["cycle_id"]), error)
        raise


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    for name in ("plan", "retire"):
        cmd = sub.add_parser(name)
        cmd.add_argument("--cycle", type=int, required=True)
        cmd.add_argument("--expected-candidate-hash", required=True)
        if name == "plan":
            cmd.add_argument("--output")
        else:
            cmd.add_argument("--limit", type=int, default=1)
            cmd.add_argument("--only", action="append")
            cmd.add_argument("--drill-every", type=int, default=25)
            cmd.add_argument("--pause-seconds", type=float, default=5)
            cmd.add_argument("--stop-file")
            cmd.add_argument("--archive-only", action="store_true")
    auto = sub.add_parser("auto")
    auto.add_argument("--service-info-url", default=SERVICE_INFO_URL)
    auto.add_argument("--max-cycle-age-hours", type=float, default=12)
    auto.add_argument("--limit", type=int, default=1000)
    auto.add_argument("--drill-every", type=int, default=25)
    auto.add_argument("--pause-seconds", type=float, default=3)
    auto.add_argument("--stop-file")
    auto.add_argument("--disable-file", default=str(AUTO_DISABLE_FILE))
    restore = sub.add_parser("restore")
    restore.add_argument("--cycle", type=int, required=True)
    restore.add_argument("--relation", required=True)
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        if args.command == "plan":
            return command_plan(args)
        if args.command == "retire":
            return command_retire(args)
        if args.command == "auto":
            return command_auto(args)
        return command_restore(args)
    except RetirementError as error:
        print(f"refused: {error}", file=sys.stderr, flush=True)
        return 2


if __name__ == "__main__":
    sys.exit(main())
