#!/usr/bin/env python3
"""Compact sparse snapshot-generation children through verified archives.

A sparse child is an attached ``leaderboard_entries_snapshot_<instrument>_s<id>``
partition whose rows are still referenced by only a few song scopes: unchanged
scopes keep pointing at the snapshot where they last changed, so a child stays
live for a handful of songs while most of its rows are unreachable. Whole-child
retirement (``postgres-snapshot-archive-retire.py``) cannot reclaim it.

For one child at a time this tool re-proves per-song liveness against the same
roots as the retention oracle, archives the whole child with ``pg_dump``
(verified like retirement), builds a replacement table holding only the live
songs' rows, verifies their exact content fingerprint, re-proves liveness,
swaps the replacement in with one lock-bounded DETACH/ATTACH transaction, and
drops the old child without CASCADE. Rollback re-inserts the archived
non-live rows into the compacted child online.

Commands:
  plan     list compactable children with live/total rows and reclaimable bytes
  compact  compact eligible children (bounded by --limit; network-bound windows only)
  restore  re-insert one compacted child's archived non-live rows
"""

from __future__ import annotations

import argparse
import fcntl
import importlib.util
import json
import pathlib
import subprocess
import sys
import time

_SPEC = importlib.util.spec_from_file_location(
    "snapshot_archive_retire",
    pathlib.Path(__file__).with_name("postgres-snapshot-archive-retire.py"))
retire = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(retire)

RetirementError = retire.RetirementError
TransientRefusal = retire.TransientRefusal

ROOTS = {
    "Solo_Guitar": "leaderboard_entries_snapshot_solo_guitar",
    "Solo_Bass": "leaderboard_entries_snapshot_solo_bass",
    "Solo_Vocals": "leaderboard_entries_snapshot_solo_vocals",
    "Solo_Drums": "leaderboard_entries_snapshot_solo_drums",
    "Solo_PeripheralGuitar": "leaderboard_entries_snapshot_pro_guitar",
    "Solo_PeripheralBass": "leaderboard_entries_snapshot_pro_bass",
    "Solo_PeripheralVocals": "leaderboard_entries_snapshot_pro_vocals",
    "Solo_PeripheralCymbals": "leaderboard_entries_snapshot_pro_cymbals",
    "Solo_PeripheralDrums": "leaderboard_entries_snapshot_pro_drums",
}
SWAP_ATTEMPTS = 12
DEFAULT_MAX_LIVE_FRACTION = 0.5
DEFAULT_MIN_RECLAIM_BYTES = 128 * 1024**2


def compaction_root() -> pathlib.Path:
    return retire.ARCHIVE_ROOT / "compaction"


def manifest_path() -> pathlib.Path:
    return compaction_root() / "manifest.jsonl"


def quote(value: str) -> str:
    return retire.quote(value)


def text_array(values: list[str]) -> str:
    if not values:
        return "ARRAY[]::text[]"
    return "ARRAY[" + ",".join(quote(v) for v in values) + "]::text[]"


def configured_resume_scrape_id() -> int:
    """The worker's Scraper:ResumeScrapeId (a whole-child liveness root), or 0."""
    result = subprocess.run(
        ["docker", "inspect", "fstworker", "--format", "{{range .Config.Env}}{{println .}}{{end}}"],
        capture_output=True, text=True, timeout=20)
    if result.returncode != 0:
        return 0
    for line in result.stdout.splitlines():
        key, _, value = line.partition("=")
        if key == "Scraper__ResumeScrapeId":
            try:
                return max(0, int(value))
            except ValueError as error:
                raise RetirementError(f"unparseable Scraper__ResumeScrapeId={value!r}") from error
    return 0


def attached_children() -> list[dict]:
    values = ",".join(f"({quote(i)},{quote(r)})" for i, r in ROOTS.items())
    rows = json.loads(retire.psql(f"""
BEGIN READ ONLY;
SET LOCAL statement_timeout = '120s';
SELECT coalesce(json_agg(r ORDER BY r.snapshot_id, r.instrument), '[]') FROM (
  SELECT m.instrument, 'public' AS parent_schema, p.relname AS parent_relation,
         'public' AS schema, c.relname AS relation,
         substring(c.relname FROM '_s([1-9][0-9]*)$')::bigint AS snapshot_id,
         c.oid::bigint AS oid, c.relfilenode::bigint AS relfilenode,
         pg_total_relation_size(c.oid) AS total_bytes,
         greatest(c.reltuples, 0)::bigint AS est_rows
  FROM (VALUES {values}) m(instrument, root)
  JOIN pg_class p ON p.relname = m.root AND p.relnamespace = 'public'::regnamespace
  JOIN pg_inherits i ON i.inhparent = p.oid AND NOT i.inhdetachpending
  JOIN pg_class c ON c.oid = i.inhrelid AND c.relnamespace = 'public'::regnamespace
  WHERE c.relname ~ ('^' || p.relname || '_s[1-9][0-9]*$')) r;
COMMIT;""", timeout=300))
    for row in rows:
        for key in ("parent_relation", "relation"):
            if not retire.IDENT.match(row[key]):
                raise RetirementError(f"unsafe identifier {row[key]!r}")
    return rows


LIVE_PUBLICATIONS_CTE = """
state AS (
  SELECT current_publication_id, previous_publication_id, working_publication_id
  FROM scrape_publication_state WHERE id
),
live_publications AS (
  SELECT pg.publication_id, pg.scrape_id FROM publication_generations pg, state
  WHERE pg.publication_id IN (state.current_publication_id, state.previous_publication_id,
                              state.working_publication_id)
     OR pg.status IN ('building', 'current')
)"""


def live_songs_sql(instrument: str, snapshot_id: int) -> str:
    """Song scopes of one (instrument, snapshot) reachable from any per-song liveness root."""
    i, s = quote(instrument), int(snapshot_id)
    return f"""
  SELECT song_id FROM leaderboard_snapshot_state WHERE instrument = {i} AND active_snapshot_id = {s}
  UNION
  SELECT song_id FROM solo_current_projection_scope WHERE instrument = {i} AND source_snapshot_id = {s}
  UNION
  SELECT source.song_id FROM leaderboard_published_scope_source source
  WHERE source.instrument = {i} AND source.source_snapshot_id = {s}
    AND source.published_scrape_id IN (SELECT scrape_id FROM live_publications)"""


def scope_liveness(child: dict, resume_scrape_id: int) -> dict:
    """Re-proves identity, whole-child roots, and the live song set of one child (read-only)."""
    i, s, rel = quote(child["instrument"]), int(child["snapshot_id"]), child["relation"]
    out = retire.psql(f"""
BEGIN READ ONLY;
SET LOCAL statement_timeout = '120s';
WITH {LIVE_PUBLICATIONS_CTE},
live_songs AS ({live_songs_sql(child["instrument"], s)})
SELECT json_build_object(
  'songs', coalesce((SELECT json_agg(song_id ORDER BY song_id) FROM live_songs), '[]'::json),
  'present', pc.oid IS NOT NULL,
  'identity_ok', pc.oid = {int(child["oid"])}::oid AND pc.relfilenode = {int(child["relfilenode"])}::oid,
  'attached', coalesce(pc.relispartition, false),
  'parent_ok', (SELECT i.inhparent = to_regclass({quote("public." + child["parent_relation"])})
                FROM pg_inherits i WHERE i.inhrelid = pc.oid AND NOT i.inhdetachpending),
  'total_bytes', coalesce(pg_total_relation_size(pc.oid), 0),
  'running_scrape', EXISTS (SELECT 1 FROM scrape_log WHERE id = {s} AND status = 'running'),
  'publication_scrape', EXISTS (SELECT 1 FROM live_publications WHERE scrape_id = {s}),
  'writer_failure', EXISTS (SELECT 1 FROM scrape_writer_failures f
                            WHERE f.instrument = {i} AND f.scrape_id = {s} AND f.replayed_at IS NULL),
  'ever_held', EXISTS (SELECT 1 FROM snapshot_generation_retention_holds h
                       WHERE h.instrument = {i} AND h.snapshot_id = {s}),
  'surface_binding', EXISTS (SELECT 1 FROM publication_surface_bindings b
                             WHERE b.publication_id IN (SELECT publication_id FROM live_publications)
                               AND strpos(b.binding_json::text, {quote(rel)}) > 0),
  'has_trigger', EXISTS (SELECT 1 FROM pg_trigger t WHERE t.tgrelid = pc.oid AND NOT t.tgisinternal),
  'resume_scrape', {s} = {int(resume_scrape_id)})
FROM (SELECT 1) one
LEFT JOIN pg_class pc ON pc.relname = {quote(rel)} AND pc.relnamespace = 'public'::regnamespace;
COMMIT;""", timeout=300)
    return json.loads(out)


def compaction_blocker(child: dict, live: dict) -> str | None:
    checks = [
        (live.get("present"), "missing"),
        (live.get("identity_ok"), "oid-or-relfilenode-changed"),
        (live.get("attached"), "not-attached"),
        (live.get("parent_ok"), "parent-changed"),
        (not live.get("running_scrape"), "running-scrape"),
        (not live.get("publication_scrape"), "named-publication-scrape"),
        (not live.get("writer_failure"), "unreplayed-writer-failure"),
        (not live.get("ever_held"), "retention-hold-history"),
        (not live.get("surface_binding"), "publication-surface-binding"),
        (not live.get("has_trigger"), "has-trigger"),
        (not live.get("resume_scrape"), "configured-resume-scrape"),
        ((child["instrument"], int(child["snapshot_id"])) not in retire.NEVER, "operator-excluded"),
        (len(live.get("songs") or []) > 0, "no-live-scopes"),
    ]
    for ok, reason in checks:
        if not ok:
            return reason
    return None


def measure(child: dict, songs: list[str]) -> dict:
    """One scan: whole-child and live-subset row counts and order-independent fingerprints."""
    rel = f"{child['schema']}.{child['relation']}"
    out = retire.psql(f"""
BEGIN READ ONLY;
SET LOCAL statement_timeout = '30min';
SELECT json_build_object(
  'rows', count(*),
  'fingerprint', coalesce(sum(h), 0)::text,
  'live_rows', count(*) FILTER (WHERE live),
  'live_fingerprint', coalesce(sum(h) FILTER (WHERE live), 0)::text,
  'songs', count(DISTINCT song_id))
FROM (SELECT t.song_id, hashtextextended(t::text, 0)::numeric AS h,
             t.song_id = ANY({text_array(songs)}) AS live
      FROM {rel} t) x;
COMMIT;""", timeout=2000)
    return json.loads(out)


def table_fingerprint(relation: str) -> tuple[int, str]:
    out = retire.psql(f"""
BEGIN READ ONLY;
SET LOCAL statement_timeout = '30min';
SELECT count(*) || '|' || coalesce(sum(hashtextextended(t::text, 0)::numeric), 0) FROM public.{relation} t;
COMMIT;""", timeout=2000)
    count, digest = out.split("|", 1)
    return int(count), digest


def object_names(child: dict) -> dict:
    """Per-compaction object names, keyed by the old child's OID so a later
    compaction of the same relation never collides with this one's indexes."""
    rel, oid = child["relation"], int(child["oid"])
    names = {"table": f"{rel}_cnew", "old": f"{rel}_cold", "fill": f"{rel}_rfill",
             "bound": f"{rel}_b{oid}", "index_prefix": f"{rel}_k{oid}_"}
    if max(len(v) for v in names.values()) + 2 > 63:
        raise RetirementError(f"compaction object names for {rel} exceed PostgreSQL's identifier limit")
    return names


def build_replacement(child: dict, songs: list[str], new: str) -> None:
    """Creates a standalone table with the live songs' rows, the partition bound as a
    validated CHECK, and the parent's index shapes so ATTACH only adopts them."""
    rel, parent = child["relation"], child["parent_relation"]
    exists = retire.psql(f"SELECT to_regclass({quote('public.' + new)}) IS NOT NULL;")
    if exists == "t":
        attached = retire.psql(f"SELECT relispartition FROM pg_class WHERE oid = to_regclass({quote('public.' + new)});")
        if attached != "f":
            raise RetirementError(f"leftover {new} is attached; refusing to touch it")
        retire.psql(f"SET lock_timeout = '10s'; DROP TABLE public.{new} RESTRICT;", timeout=120)
    index_rows = json.loads(retire.psql(f"""
SELECT coalesce(json_agg(json_build_object('primary', ix.indisprimary, 'def', pg_get_indexdef(ix.indexrelid))
                         ORDER BY ix.indisprimary DESC, ic.relname), '[]')
FROM pg_index ix JOIN pg_class ic ON ic.oid = ix.indexrelid
WHERE ix.indrelid = to_regclass({quote('public.' + parent)});"""))
    statements = [
        "BEGIN;",
        "SET LOCAL statement_timeout = '30min';",
        # NOT NULL constraints always copy; other CHECKs (a leftover bound) must not.
        f"CREATE TABLE public.{new} (LIKE public.{rel} INCLUDING DEFAULTS "
        f"INCLUDING STORAGE INCLUDING COMPRESSION);",
        f"INSERT INTO public.{new} SELECT * FROM public.{rel} WHERE song_id = ANY({text_array(songs)}) "
        f"ORDER BY snapshot_id, song_id, instrument, account_id;",
        f"ALTER TABLE public.{new} ADD CONSTRAINT {object_names(child)['bound']} "
        f"CHECK (snapshot_id = {int(child['snapshot_id'])} AND instrument = {quote(child['instrument'])});",
    ]
    for n, index in enumerate(index_rows):
        definition = index["def"]
        marker = f" ON ONLY public.{parent} "
        if marker not in definition:
            raise RetirementError(f"unexpected parent index definition: {definition[:160]}")
        columns = definition.split(marker, 1)[1]
        unique = definition.startswith("CREATE UNIQUE INDEX ")
        name = f"{object_names(child)['index_prefix']}{n}"
        statements.append(f"CREATE {'UNIQUE ' if unique else ''}INDEX {name} ON public.{new} {columns};")
        if index["primary"]:
            statements.append(f"ALTER TABLE public.{new} ADD CONSTRAINT {name} PRIMARY KEY USING INDEX {name};")
    statements += [f"ANALYZE public.{new};", "COMMIT;"]
    retire.psql("\n".join(statements), timeout=2000)


def swap_in(child: dict, songs: list[str], new: str, sleep=time.sleep) -> int:
    """Detaches the old child and attaches the replacement under the same bound in one
    lock-bounded transaction that re-checks liveness while holding the parent lock."""
    rel, parent, s = child["relation"], child["parent_relation"], int(child["snapshot_id"])
    old = object_names(child)["old"]
    sql = f"""
BEGIN;
SET LOCAL lock_timeout = '2s';
SET LOCAL statement_timeout = '60s';
ALTER TABLE public.{parent} DETACH PARTITION public.{rel};
ALTER TABLE public.{rel} RENAME TO {old};
ALTER TABLE public.{new} RENAME TO {rel};
ALTER TABLE public.{parent} ATTACH PARTITION public.{rel} FOR VALUES IN ({s});
DO $guard$
BEGIN
  IF EXISTS (
    WITH {LIVE_PUBLICATIONS_CTE}
    SELECT 1 FROM ({live_songs_sql(child["instrument"], s)}) live
    WHERE NOT (live.song_id = ANY({text_array(songs)}))) THEN
    RAISE EXCEPTION 'compaction liveness changed during swap';
  END IF;
END
$guard$;
COMMIT;"""
    for attempt in range(1, SWAP_ATTEMPTS + 1):
        try:
            retire.psql(sql, timeout=180)
            return attempt
        except RetirementError as error:
            if "lock timeout" not in str(error):
                raise
            if attempt == SWAP_ATTEMPTS:
                raise TransientRefusal(
                    f"swap of {rel} stayed lock-contended after {attempt} attempts") from error
            sleep(min(60, 5 * attempt))
    raise AssertionError("unreachable")


def verify_attached(child: dict) -> dict:
    rel, parent = child["relation"], child["parent_relation"]
    out = json.loads(retire.psql(f"""
SELECT json_build_object(
  'oid', c.oid::bigint, 'relfilenode', c.relfilenode::bigint,
  'attached', c.relispartition,
  'parent_ok', (SELECT i.inhparent = to_regclass({quote('public.' + parent)})
                FROM pg_inherits i WHERE i.inhrelid = c.oid),
  'bound', pg_get_expr(c.relpartbound, c.oid),
  'parent_indexes', (SELECT count(*) FROM pg_index WHERE indrelid = to_regclass({quote('public.' + parent)})),
  'adopted_indexes', (SELECT count(*) FROM pg_index ix JOIN pg_inherits ii ON ii.inhrelid = ix.indexrelid
                      JOIN pg_index pix ON pix.indexrelid = ii.inhparent
                      WHERE ix.indrelid = c.oid AND pix.indrelid = to_regclass({quote('public.' + parent)})),
  'child_indexes', (SELECT count(*) FROM pg_index WHERE indrelid = c.oid),
  'total_bytes', pg_total_relation_size(c.oid))
FROM pg_class c WHERE c.oid = to_regclass({quote('public.' + rel)});"""))
    expected_bound = f"FOR VALUES IN ('{int(child['snapshot_id'])}')"
    if not (out["attached"] and out["parent_ok"] and out["bound"] == expected_bound):
        raise RetirementError(f"compacted {rel} is not attached as expected: {out}")
    if not (out["parent_indexes"] == out["adopted_indexes"] == out["child_indexes"]):
        raise RetirementError(f"compacted {rel} index adoption mismatch: {out}")
    return out


def drop_old(child: dict) -> None:
    old = object_names(child)["old"]
    attached = retire.psql(f"SELECT relispartition FROM pg_class WHERE oid = to_regclass({quote('public.' + old)});")
    if attached != "f":
        raise RetirementError(f"{old} is not a detached standalone table ({attached!r})")
    retire.psql(f"SET lock_timeout = '10s'; DROP TABLE public.{old} RESTRICT;", timeout=120)


def drop_bound_check(child: dict) -> bool:
    try:
        retire.psql(f"SET lock_timeout = '2s'; ALTER TABLE public.{child['relation']} "
                    f"DROP CONSTRAINT IF EXISTS {object_names(child)['bound']};", timeout=60)
        return True
    except RetirementError as error:
        if "lock timeout" in str(error):
            return False
        raise


def compact_child(child: dict, args: argparse.Namespace, ordinal: int, resume_scrape_id: int) -> dict | None:
    live = scope_liveness(child, resume_scrape_id)
    blocker = compaction_blocker(child, live)
    if blocker:
        print(f"skip {child['relation']}: {blocker}", flush=True)
        return None
    songs = list(live["songs"])
    started = time.monotonic()
    measured = measure(child, songs)
    rows, live_rows = int(measured["rows"]), int(measured["live_rows"])
    live_fraction = live_rows / rows if rows else 1.0
    reclaim = int(live["total_bytes"] * (1 - live_fraction))
    if live_fraction > args.max_live_fraction:
        print(f"skip {child['relation']}: dense live_fraction={live_fraction:.2f}", flush=True)
        return None
    if reclaim < args.min_reclaim_bytes:
        print(f"skip {child['relation']}: small reclaim_mib={reclaim / 1024**2:.0f}", flush=True)
        return None

    path = compaction_root() / child["instrument"] / f"{child['relation']}-o{child['oid']}.dump"
    path.parent.mkdir(parents=True, exist_ok=True)
    retire.dump_child(child, path)
    full_drill = args.drill_every > 0 and ordinal % args.drill_every == 0
    verification = retire.verify_archive(child, path, rows, full_drill, measured["fingerprint"])
    base = {
        "instrument": child["instrument"],
        "parent_relation": child["parent_relation"],
        "relation": child["relation"],
        "snapshot_id": int(child["snapshot_id"]),
        "oid": int(child["oid"]),
        "relfilenode": int(child["relfilenode"]),
        "rows": rows,
        "content_fingerprint": measured["fingerprint"],
        "live_songs": songs,
        "live_rows": live_rows,
        "live_fingerprint": measured["live_fingerprint"],
        "total_bytes": int(live["total_bytes"]),
        "archive_path": str(path),
        "archive_bytes": path.stat().st_size,
        "archive_sha256": retire.sha256_file(path),
        **verification,
    }
    retire.append_manifest(manifest_path(), {**base, "state": "archived", "at": retire.utcnow()})

    new = object_names(child)["table"]
    build_replacement(child, songs, new)
    new_rows, new_fingerprint = table_fingerprint(new)
    if new_rows != live_rows or new_fingerprint != measured["live_fingerprint"]:
        retire.psql(f"DROP TABLE public.{new} RESTRICT;")
        raise RetirementError(f"replacement for {child['relation']} does not match the live fingerprint")

    recheck = scope_liveness(child, resume_scrape_id)
    blocker = compaction_blocker(child, recheck)
    if blocker or not set(recheck["songs"]) <= set(songs):
        retire.psql(f"DROP TABLE public.{new} RESTRICT;")
        print(f"skip after build {child['relation']}: {blocker or 'live set grew'}", flush=True)
        return None
    if args.window is not None:
        is_open, why = args.window()
        if not is_open:
            retire.psql(f"DROP TABLE public.{new} RESTRICT;")
            print(f"window closed before swap ({why}); dropped replacement", flush=True)
            return None

    try:
        attempts = swap_in(child, songs, new)
    except BaseException:
        if retire.psql(f"SELECT to_regclass({quote('public.' + new)}) IS NOT NULL;") == "t":
            retire.psql(f"DROP TABLE public.{new} RESTRICT;")
        raise
    attached = verify_attached(child)
    swapped_rows, swapped_fingerprint = table_fingerprint(child["relation"])
    if swapped_rows != live_rows or swapped_fingerprint != measured["live_fingerprint"]:
        raise RetirementError(f"compacted {child['relation']} lost its live fingerprint after swap; old kept as _cold")
    drop_old(child)
    check_dropped = drop_bound_check(child)
    entry = {**base, "state": "compacted", "swap_attempts": attempts,
             "new_oid": attached["oid"], "new_relfilenode": attached["relfilenode"],
             "new_total_bytes": attached["total_bytes"], "bound_check_dropped": check_dropped,
             "at": retire.utcnow()}
    retire.append_manifest(manifest_path(), entry)
    print(f"compacted {child['relation']} rows={rows}->{live_rows} songs={measured['songs']}->{len(songs)} "
          f"reclaimed_gib={(base['total_bytes'] - attached['total_bytes']) / 1024**3:.2f} "
          f"archive_mib={base['archive_bytes'] / 1024**2:.1f} drill={verification.get('restore_drill', '-')} "
          f"seconds={time.monotonic() - started:.0f}", flush=True)
    return entry


def command_plan(args: argparse.Namespace) -> int:
    resume = configured_resume_scrape_id()
    children = attached_children()
    values = ",".join(f"({quote(c['instrument'])},{int(c['snapshot_id'])})" for c in children) or "(NULL,NULL)"
    estimates = {
        (row["instrument"], int(row["snapshot_id"])): row
        for row in json.loads(retire.psql(f"""
BEGIN READ ONLY;
SET LOCAL statement_timeout = '120s';
WITH {LIVE_PUBLICATIONS_CTE},
kids(instrument, snapshot_id) AS (VALUES {values}),
live AS (
  SELECT instrument, active_snapshot_id AS snapshot_id, song_id FROM leaderboard_snapshot_state
  UNION
  SELECT instrument, source_snapshot_id, song_id FROM solo_current_projection_scope
  UNION
  SELECT instrument, source_snapshot_id, song_id FROM leaderboard_published_scope_source
  WHERE published_scrape_id IN (SELECT scrape_id FROM live_publications)),
rows_by_song AS (
  SELECT instrument, source_snapshot_id AS snapshot_id, song_id, max(row_count) AS row_count
  FROM leaderboard_published_scope_source GROUP BY 1, 2, 3)
SELECT coalesce(json_agg(r), '[]') FROM (
  SELECT k.instrument, k.snapshot_id, count(l.song_id) AS live_songs,
         coalesce(sum(rb.row_count), 0) AS live_rows_estimate,
         EXISTS (SELECT 1 FROM scrape_log WHERE id = k.snapshot_id AND status = 'running')
           OR EXISTS (SELECT 1 FROM live_publications WHERE scrape_id = k.snapshot_id)
           OR EXISTS (SELECT 1 FROM scrape_writer_failures f WHERE f.instrument = k.instrument
                        AND f.scrape_id = k.snapshot_id AND f.replayed_at IS NULL)
           OR EXISTS (SELECT 1 FROM snapshot_generation_retention_holds h WHERE h.instrument = k.instrument
                        AND h.snapshot_id = k.snapshot_id) AS whole_child_root
  FROM kids k LEFT JOIN live l ON l.instrument = k.instrument AND l.snapshot_id = k.snapshot_id
  LEFT JOIN rows_by_song rb ON rb.instrument = l.instrument AND rb.snapshot_id = l.snapshot_id
                           AND rb.song_id = l.song_id
  GROUP BY 1, 2) r;
COMMIT;""", timeout=300))
    }
    plan = []
    for child in children:
        estimate = estimates.get((child["instrument"], int(child["snapshot_id"])), {})
        live_rows = int(estimate.get("live_rows_estimate") or 0)
        rows = max(int(child["est_rows"]), 1)
        fraction = min(1.0, live_rows / rows)
        plan.append({**child, "live_songs": int(estimate.get("live_songs") or 0), "live_rows_estimate": live_rows,
                     "whole_child_root": bool(estimate.get("whole_child_root")),
                     "live_fraction_estimate": round(fraction, 3),
                     "reclaim_bytes_estimate": int(child["total_bytes"] * (1 - fraction))})
    eligible = [p for p in plan if p["live_songs"] > 0 and not p["whole_child_root"]
                and p["live_fraction_estimate"] <= args.max_live_fraction
                and p["reclaim_bytes_estimate"] >= args.min_reclaim_bytes
                and int(p["snapshot_id"]) != resume
                and (p["instrument"], int(p["snapshot_id"])) not in retire.NEVER]
    eligible.sort(key=lambda p: p["reclaim_bytes_estimate"], reverse=True)
    print(f"attached children={len(plan)} total_gib={sum(p['total_bytes'] for p in plan) / 1024**3:.1f}")
    print(f"compaction candidates={len(eligible)} total_gib={sum(p['total_bytes'] for p in eligible) / 1024**3:.1f} "
          f"reclaim_estimate_gib={sum(p['reclaim_bytes_estimate'] for p in eligible) / 1024**3:.1f}")
    for p in eligible[: args.show]:
        print(f"  {p['relation']} songs={p['live_songs']} live~{p['live_fraction_estimate']:.2f} "
              f"total_mib={p['total_bytes'] / 1024**2:.0f} reclaim_mib~{p['reclaim_bytes_estimate'] / 1024**2:.0f}")
    if args.output:
        pathlib.Path(args.output).write_text(json.dumps(eligible, indent=1))
    return 0


def command_compact(args: argparse.Namespace) -> int:
    args.window = None if args.no_window else (lambda: retire.probe_window(args.service_info_url))
    if args.window is not None:
        is_open, why = args.window()
        if not is_open:
            print(f"outside the network-bound fetch window ({why}); nothing to do", flush=True)
            return 0
    compaction_root().mkdir(parents=True, exist_ok=True)
    retire.LOCK_PATH.parent.mkdir(parents=True, exist_ok=True)
    with retire.LOCK_PATH.open("w") as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            raise TransientRefusal("another retirement or compaction run holds the lock")
        resume = configured_resume_scrape_id()
        children = attached_children()
        if args.only:
            children = [c for c in children if c["relation"] in set(args.only)]
            if len(children) != len(set(args.only)):
                raise RetirementError("--only names a relation that is not an attached generation child")
        children.sort(key=lambda c: int(c["total_bytes"]), reverse=True)
        processed = 0
        for child in children:
            if processed >= args.limit:
                break
            if args.stop_file and pathlib.Path(args.stop_file).exists():
                print("stop file present; ending run", flush=True)
                break
            if args.window is not None:
                is_open, why = args.window()
                if not is_open:
                    print(f"window closed ({why}); ending run", flush=True)
                    break
            retire.preflight()
            if compact_child(child, args, processed, resume) is not None:
                processed += 1
                if args.pause_seconds:
                    time.sleep(args.pause_seconds)
        print(f"run complete processed={processed}", flush=True)
    return 0


def command_restore(args: argparse.Namespace) -> int:
    entries = [json.loads(line) for line in manifest_path().read_text().splitlines()]
    matches = [e for e in entries if e["relation"] == args.relation and e["state"] == "compacted"]
    if not matches:
        raise RetirementError("relation has no compacted manifest entry")
    entry = matches[-1]
    if any(e["relation"] == args.relation and e["state"] == "restored" and e["new_oid"] == entry["new_oid"]
           for e in entries):
        raise RetirementError("this compaction was already restored")
    path = pathlib.Path(entry["archive_path"])
    if retire.sha256_file(path) != entry["archive_sha256"]:
        raise RetirementError("archive checksum does not match the manifest")
    rel = entry["relation"]
    current = json.loads(retire.psql(
        f"SELECT json_build_object('oid', oid::bigint, 'relfilenode', relfilenode::bigint) "
        f"FROM pg_class WHERE oid = to_regclass({quote('public.' + rel)});") or "null")
    if not current or current["oid"] != entry["new_oid"]:
        raise RetirementError(f"{rel} is not the compacted table recorded in the manifest")
    rows, fingerprint = table_fingerprint(rel)
    if rows != entry["live_rows"] or fingerprint != entry["live_fingerprint"]:
        raise RetirementError(f"{rel} no longer matches its compacted fingerprint")
    fill = f"{rel}_rfill"
    if retire.psql(f"SELECT to_regclass({quote('public.' + fill)}) IS NOT NULL;") == "t":
        raise RetirementError(f"{fill} already exists; refusing to overwrite")
    retire.psql(f"CREATE TABLE public.{fill} (LIKE public.{rel} INCLUDING DEFAULTS);")
    try:
        result = subprocess.run(
            ["docker", "run", "--rm", "--network", f"container:{retire.PG_CONTAINER}",
             "-v", f"{path}:/archive/child.dump:ro", "--entrypoint", "bash", retire.DRILL_IMAGE, "-c",
             "set -euo pipefail; pg_restore -f - --data-only /archive/child.dump "
             f"| sed -E '0,/^COPY public\\.{rel} /s//COPY public.{fill} /' "
             f"| psql -h 127.0.0.1 -U {retire.DB_USER} -d {retire.DB_NAME} -X -q -v ON_ERROR_STOP=1"],
            capture_output=True, text=True, timeout=7200)
        if result.returncode != 0:
            raise RetirementError(f"archive reload failed: {result.stderr.strip()[-500:]}")
        fill_rows, fill_fingerprint = table_fingerprint(fill)
        if fill_rows != entry["rows"] or fill_fingerprint != entry["content_fingerprint"]:
            raise RetirementError("reloaded archive does not match the archived fingerprint")
        retire.psql(f"""
BEGIN;
SET LOCAL statement_timeout = '30min';
INSERT INTO public.{rel} SELECT * FROM public.{fill} f WHERE NOT (f.song_id = ANY({text_array(entry['live_songs'])}));
COMMIT;""", timeout=2000)
        restored_rows, restored_fingerprint = table_fingerprint(rel)
        if restored_rows != entry["rows"] or restored_fingerprint != entry["content_fingerprint"]:
            raise RetirementError("restored child does not match the archived fingerprint")
    finally:
        retire.psql(f"DROP TABLE IF EXISTS public.{fill} RESTRICT;")
    retire.append_manifest(manifest_path(), {**entry, "state": "restored", "at": retire.utcnow()})
    print(f"restored {rel} rows={entry['live_rows']}->{entry['rows']}", flush=True)
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    for name in ("plan", "compact"):
        cmd = sub.add_parser(name)
        cmd.add_argument("--max-live-fraction", type=float, default=DEFAULT_MAX_LIVE_FRACTION)
        cmd.add_argument("--min-reclaim-bytes", type=int, default=DEFAULT_MIN_RECLAIM_BYTES)
        if name == "plan":
            cmd.add_argument("--show", type=int, default=20)
            cmd.add_argument("--output")
        else:
            cmd.add_argument("--limit", type=int, default=1)
            cmd.add_argument("--only", action="append")
            cmd.add_argument("--drill-every", type=int, default=10)
            cmd.add_argument("--pause-seconds", type=float, default=3)
            cmd.add_argument("--stop-file")
            cmd.add_argument("--service-info-url", default=retire.SERVICE_INFO_URL)
            cmd.add_argument("--no-window", action="store_true",
                             help="skip the network-bound fetch window check (isolated drills only)")
    restore = sub.add_parser("restore")
    restore.add_argument("--relation", required=True)
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        if args.command == "plan":
            return command_plan(args)
        if args.command == "compact":
            return command_compact(args)
        return command_restore(args)
    except TransientRefusal as error:
        print(f"deferred: {error}", file=sys.stderr, flush=True)
        return 3
    except RetirementError as error:
        print(f"refused: {error}", file=sys.stderr, flush=True)
        return 2


if __name__ == "__main__":
    sys.exit(main())
