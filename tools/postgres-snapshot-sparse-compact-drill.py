#!/usr/bin/env python3
"""Isolated end-to-end drill for postgres-snapshot-sparse-compact.py.

Starts a throwaway postgres:17 container that mirrors the production snapshot
partition layout (root LIST(instrument) -> instrument LIST(snapshot_id) with a
DEFAULT child and root-level indexes) plus the liveness-root tables, then runs
plan, compact, reader/topology checks, restore, and a second compaction. The
production database is never touched; archives go under --work-root.

  tools/postgres-snapshot-sparse-compact-drill.py --work-root <dir on the FST drive>
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import pathlib
import subprocess
import sys
import time

_SPEC = importlib.util.spec_from_file_location(
    "snapshot_sparse_compact", pathlib.Path(__file__).with_name("postgres-snapshot-sparse-compact.py"))
compact = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(compact)
retire = compact.retire

CONTAINER = "fst-sparse-compact-drill"
DB = "drill"

SCHEMA = """
CREATE TABLE scrape_publication_state (id boolean PRIMARY KEY, current_publication_id bigint,
  previous_publication_id bigint, working_publication_id bigint);
CREATE TABLE publication_generations (publication_id bigint PRIMARY KEY, scrape_id bigint, status text);
CREATE TABLE leaderboard_snapshot_state (song_id text, instrument text, active_snapshot_id bigint);
CREATE TABLE solo_current_projection_scope (song_id text, instrument text, source_snapshot_id bigint);
CREATE TABLE leaderboard_published_scope_source (published_scrape_id bigint, song_id text, instrument text,
  source_snapshot_id bigint, row_count bigint);
CREATE TABLE scrape_log (id bigint PRIMARY KEY, status text);
CREATE TABLE scrape_writer_failures (instrument text, scrape_id bigint, replayed_at timestamptz);
CREATE TABLE snapshot_generation_retention_holds (instrument text, snapshot_id bigint);
CREATE TABLE publication_surface_bindings (publication_id bigint, binding_json jsonb);

CREATE TABLE leaderboard_entries_snapshot (
  snapshot_id bigint NOT NULL, song_id text NOT NULL, instrument text NOT NULL, account_id text NOT NULL,
  score integer NOT NULL, accuracy integer, is_full_combo boolean, stars integer, season integer,
  percentile real, rank integer DEFAULT 0, source text NOT NULL DEFAULT 'scrape', difficulty integer DEFAULT -1,
  api_rank integer, end_time text, band_members_json jsonb, band_score integer, base_score integer,
  instrument_bonus integer, overdrive_bonus integer, instrument_combo text,
  first_seen_at timestamptz NOT NULL, last_updated_at timestamptz NOT NULL,
  PRIMARY KEY (snapshot_id, song_id, instrument, account_id)
) PARTITION BY LIST (instrument);
CREATE INDEX ON leaderboard_entries_snapshot (snapshot_id, song_id, instrument, score DESC);
CREATE TABLE leaderboard_entries_snapshot_solo_guitar PARTITION OF leaderboard_entries_snapshot
  FOR VALUES IN ('Solo_Guitar') PARTITION BY LIST (snapshot_id);
CREATE TABLE leaderboard_entries_snapshot_solo_guitar_default PARTITION OF leaderboard_entries_snapshot_solo_guitar DEFAULT;
CREATE TABLE leaderboard_entries_snapshot_solo_guitar_s100 PARTITION OF leaderboard_entries_snapshot_solo_guitar FOR VALUES IN (100);
CREATE TABLE leaderboard_entries_snapshot_solo_guitar_s101 PARTITION OF leaderboard_entries_snapshot_solo_guitar FOR VALUES IN (101);

INSERT INTO leaderboard_entries_snapshot
SELECT sid, song, 'Solo_Guitar', 'acct_' || n, 100000 - n, 95, n % 7 = 0, 5, 15, n / 1000.0, n, 'scrape', 3, n,
       '2026-10-0' || (1 + n % 2) || 'T00:00:00Z', NULL, NULL, NULL, NULL, NULL, NULL, now(), now()
FROM (VALUES (100, 'song_a', 50), (100, 'song_b', 2000), (100, 'song_c', 3000),
             (101, 'song_b', 2100), (101, 'song_c', 3100)) v(sid, song, cnt),
     generate_series(1, v.cnt) n;

INSERT INTO scrape_log VALUES (100, 'completed'), (101, 'completed');
INSERT INTO publication_generations VALUES (1, 101, 'current');
INSERT INTO scrape_publication_state VALUES (true, 1, NULL, NULL);
INSERT INTO leaderboard_published_scope_source VALUES
  (101, 'song_a', 'Solo_Guitar', 100, 50), (101, 'song_b', 'Solo_Guitar', 101, 2100),
  (101, 'song_c', 'Solo_Guitar', 101, 3100);
INSERT INTO leaderboard_snapshot_state VALUES
  ('song_a', 'Solo_Guitar', 100), ('song_b', 'Solo_Guitar', 101), ('song_c', 'Solo_Guitar', 101);
INSERT INTO solo_current_projection_scope VALUES ('song_a', 'Solo_Guitar', 100);
ANALYZE;
"""


def sh(args: list[str], **kwargs) -> subprocess.CompletedProcess:
    return subprocess.run(args, capture_output=True, text=True, **kwargs)


def q(sql: str) -> str:
    return retire.psql(sql)


def expect(condition: bool, message: str) -> None:
    if not condition:
        raise SystemExit(f"DRILL FAILED: {message}")
    print(f"ok: {message}", flush=True)


def run(args: list[str]) -> str:
    result = sh([sys.executable, str(pathlib.Path(__file__).with_name("postgres-snapshot-sparse-compact.py")), *args])
    print(result.stdout.strip(), result.stderr.strip(), flush=True)
    return result.stdout + result.stderr


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--work-root", required=True)
    args = parser.parse_args()
    work = pathlib.Path(args.work_root)
    archive_root = work / "archives"
    archive_root.mkdir(parents=True, exist_ok=True)

    sh(["docker", "rm", "-f", CONTAINER])
    started = sh(["docker", "run", "-d", "--name", CONTAINER, "--network", "none",
                  "-e", "POSTGRES_HOST_AUTH_METHOD=trust", "-e", f"POSTGRES_DB={DB}", retire.DRILL_IMAGE])
    if started.returncode != 0:
        raise SystemExit(started.stderr)
    try:
        for _ in range(60):
            if sh(["docker", "exec", CONTAINER, "pg_isready", "-q", "-U", "postgres", "-d", DB]).returncode == 0:
                break
            time.sleep(1)
        time.sleep(2)
        retire.PG_CONTAINER, retire.DB_USER, retire.DB_NAME = CONTAINER, "postgres", DB
        q(SCHEMA)

        # Drive the CLI in-process so the module overrides apply.
        retire.ARCHIVE_ROOT = archive_root
        retire.LOCK_PATH = archive_root / ".retirement.lock"
        retire.preflight = lambda: None
        retire.probe_window = lambda url: (True, "isolated drill")
        compact.worker_configuration = lambda: {"container_id": "drill", "resume_scrape_id": 0}

        def cli(*argv: str) -> int:
            return compact.main(list(argv))

        original_rows, original_fp = compact.table_fingerprint("leaderboard_entries_snapshot_solo_guitar_s100")
        expect(original_rows == 5050, "seeded child s100 has 5,050 rows")
        expect(cli("plan", "--min-reclaim-bytes", "0", "--output", str(work / "plan.json")) == 0, "plan runs")
        plan = json.loads((work / "plan.json").read_text())
        expect([p["relation"] for p in plan] == ["leaderboard_entries_snapshot_solo_guitar_s100"],
               "plan selects only the sparse child")

        expect(cli("compact", "--only", "leaderboard_entries_snapshot_solo_guitar_s101",
                   "--min-reclaim-bytes", "0") == 0, "dense child run completes")
        expect(int(q("SELECT count(*) FROM leaderboard_entries_snapshot_solo_guitar_s101")) == 5200,
               "dense child is left untouched")

        expect(cli("compact", "--only", "leaderboard_entries_snapshot_solo_guitar_s100",
                   "--min-reclaim-bytes", "0", "--drill-every", "1") == 0, "sparse compaction runs")
        expect(int(q("SELECT count(*) FROM leaderboard_entries_snapshot WHERE snapshot_id = 100")) == 50,
               "root reads see only the live song's rows")
        expect(q("SELECT string_agg(DISTINCT song_id, ',') FROM leaderboard_entries_snapshot_solo_guitar_s100") == "song_a",
               "compacted child holds only song_a")
        expect(int(q("SELECT count(*) FROM leaderboard_entries_snapshot_solo_guitar_default")) == 0,
               "DEFAULT child stays empty")
        topology = json.loads(q("""
SELECT json_build_object(
  'attached', c.relispartition, 'bound', pg_get_expr(c.relpartbound, c.oid),
  'adopted', (SELECT count(*) FROM pg_index ix JOIN pg_inherits ii ON ii.inhrelid = ix.indexrelid
              WHERE ix.indrelid = c.oid),
  'checks', (SELECT count(*) FROM pg_constraint WHERE conrelid = c.oid AND contype = 'c'),
  'leftovers', (SELECT count(*) FROM pg_class WHERE relname ~ '_(cnew|cold|rfill)$'))
FROM pg_class c WHERE c.relname = 'leaderboard_entries_snapshot_solo_guitar_s100';"""))
        expect(topology == {"attached": True, "bound": "FOR VALUES IN ('100')", "adopted": 2, "checks": 0,
                            "leftovers": 0}, f"partition bound, adopted indexes, no leftovers: {topology}")
        plan_score = q("EXPLAIN SELECT score FROM leaderboard_entries_snapshot WHERE snapshot_id = 100 "
                       "AND song_id = 'song_a' AND instrument = 'Solo_Guitar' ORDER BY score DESC LIMIT 5")
        expect("s100" in plan_score, "planner prunes to the compacted child")
        manifest = [json.loads(l) for l in (archive_root / "compaction" / "manifest.jsonl").read_text().splitlines()]
        compacted = [e for e in manifest if e["state"] == "compacted"]
        expect(len(compacted) == 1 and compacted[0]["rows"] == 5050 and compacted[0]["live_rows"] == 50
               and compacted[0]["restore_drill"] == "passed", "manifest records the verified archive")

        expect(cli("restore", "--relation", "leaderboard_entries_snapshot_solo_guitar_s100") == 0, "restore runs")
        restored_rows, restored_fp = compact.table_fingerprint("leaderboard_entries_snapshot_solo_guitar_s100")
        expect((restored_rows, restored_fp) == (original_rows, original_fp),
               "restore reproduces the original content fingerprint")
        expect(cli("restore", "--relation", "leaderboard_entries_snapshot_solo_guitar_s100") == 2,
               "a second restore of the same compaction is refused")

        q("INSERT INTO solo_current_projection_scope VALUES ('song_b', 'Solo_Guitar', 100);")
        q("INSERT INTO leaderboard_entries_snapshot_solo_guitar_default SELECT 999, 'song_z', 'Solo_Guitar', 'acct_z', 1, "
          "NULL, NULL, NULL, NULL, NULL, 0, 'scrape', -1, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, now(), now();")
        expect(cli("compact", "--only", "leaderboard_entries_snapshot_solo_guitar_s100",
                   "--min-reclaim-bytes", "0") == 2, "a non-empty DEFAULT partition refuses the swap")
        expect(int(q("SELECT count(*) FROM leaderboard_entries_snapshot_solo_guitar_s100")) == 5050
               and q("SELECT count(*) FROM pg_class WHERE relname ~ '_(cnew|cold)$'") == "0",
               "a refused swap leaves the child and no replacement behind")
        q("DELETE FROM leaderboard_entries_snapshot_solo_guitar_default;")
        expect(cli("compact", "--only", "leaderboard_entries_snapshot_solo_guitar_s100",
                   "--min-reclaim-bytes", "0") == 0, "re-compaction after restore runs")
        expect(q("SELECT string_agg(DISTINCT song_id, ',' ORDER BY song_id) FROM leaderboard_entries_snapshot_solo_guitar_s100")
               == "song_a,song_b", "a newly live song is retained")
        print("DRILL PASSED", flush=True)
        return 0
    finally:
        sh(["docker", "rm", "-f", CONTAINER])


if __name__ == "__main__":
    sys.exit(main())
