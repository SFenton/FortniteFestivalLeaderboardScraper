#!/usr/bin/env python3
"""Disposable-PostgreSQL drill for postgres-rank-history-retention-rewrite.py.

Starts a throwaway postgres:17 container with the production rank-history shape
(nine LIST partitions with a dropped column, composite table with its BRIN
index), seeds 60 days of history, and runs build, a write between build and
swap, swap, rollback of one table, archive, and drop. It checks that every
table keeps exactly the rows the worker's retention keeps (each row after the
cutoff plus each account's newest row at or before it), including rows written
after the build, and that partitions, indexes, and retention state end up in
place. Production is never touched.

    PYTHONDONTWRITEBYTECODE=1 python3 tools/postgres-rank-history-retention-rewrite-drill.py
"""

from __future__ import annotations

import os
import pathlib
import subprocess
import sys
import tempfile
import time

TOOL = pathlib.Path(__file__).with_name("postgres-rank-history-retention-rewrite.py")
CONTAINER = f"fst-rh-drill-{os.getpid()}"
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
RETENTION = 30

SCHEMA = """
CREATE TABLE rank_history (
    account_id text NOT NULL, instrument text NOT NULL, snapshot_date date NOT NULL,
    adjusted_skill_rank integer NOT NULL, weighted_rank integer NOT NULL, fc_rate_rank integer NOT NULL,
    total_score_rank integer NOT NULL, max_score_percent_rank integer NOT NULL,
    adjusted_skill_rating real, weighted_rating real, fc_rate real, total_score integer,
    max_score_percent real, songs_played integer, coverage real, full_combo_count integer,
    raw_max_score_percent real, schema_version smallint DEFAULT 1 NOT NULL,
    raw_weighted_rating real, raw_skill_rating real, snapshot_taken_at timestamptz,
    PRIMARY KEY (account_id, instrument, snapshot_date)
) PARTITION BY LIST (instrument);
""" + "".join(
    f"CREATE TABLE {t} PARTITION OF rank_history FOR VALUES IN ('{i}') WITH (fillfactor=70);\n"
    for t, i in PARTITIONS.items()) + """
ALTER TABLE rank_history ADD COLUMN raw_fc_rate real;
ALTER TABLE rank_history DROP COLUMN raw_fc_rate;
CREATE TABLE composite_rank_history (
    account_id text NOT NULL, snapshot_date date NOT NULL, composite_rank integer NOT NULL,
    composite_rating real, instruments_played integer, total_songs_played integer,
    PRIMARY KEY (account_id, snapshot_date)
) WITH (fillfactor=70);
CREATE INDEX ix_crh_retention_cutoff_brin ON composite_rank_history
    USING brin (snapshot_date) WITH (pages_per_range = 128, autosummarize = on);
-- 60 days; account k has a row on day d when (k + d) % (k % 5 + 1) = 0, so some
-- accounts write daily and others rarely, and a few stop before the cutoff.
INSERT INTO rank_history (account_id, instrument, snapshot_date, adjusted_skill_rank, weighted_rank,
    fc_rate_rank, total_score_rank, max_score_percent_rank, total_score, snapshot_taken_at)
SELECT 'acct' || k, i, current_date - d, k, k, k, k + d, k, k * 100 + d, now()
FROM generate_series(1, 400) k, generate_series(0, 60) d,
     unnest(ARRAY[""" + ",".join(f"'{i}'" for i in PARTITIONS.values()) + """]) i
WHERE (k + d) % (k % 5 + 1) = 0 AND NOT (k % 37 = 0 AND d < 40);
INSERT INTO composite_rank_history (account_id, snapshot_date, composite_rank, composite_rating)
SELECT 'acct' || k, current_date - d, k + d, k / 10.0
FROM generate_series(1, 400) k, generate_series(0, 60) d
WHERE (k + d) % (k % 4 + 1) = 0 AND NOT (k % 41 = 0 AND d < 45);
"""


def sh(args: list[str], **kwargs) -> subprocess.CompletedProcess:
    return subprocess.run(args, capture_output=True, text=True, **kwargs)


def psql(sql: str) -> str:
    result = sh(["docker", "exec", "-i", CONTAINER, "psql", "-X", "-q", "-At", "-v", "ON_ERROR_STOP=1",
                 "-U", "fst", "-d", "fstservice"], input=sql, timeout=300)
    if result.returncode != 0:
        raise AssertionError(result.stderr)
    return result.stdout.strip()


def tool(*args: str, root: pathlib.Path) -> str:
    env = dict(os.environ, FST_RH_PG_CONTAINER=CONTAINER, FST_RH_ARCHIVE_ROOT=str(root),
               FST_RH_LOCK_PATH=str(root / ".drill.lock"), PYTHONDONTWRITEBYTECODE="1")
    result = sh([sys.executable, str(TOOL), *args, "--window", "any"], env=env, timeout=1800)
    if result.returncode != 0:
        raise AssertionError(f"tool {args} failed: {result.stdout}\n{result.stderr}")
    return result.stdout


def kept_fingerprint(table: str, key: str) -> str:
    """Fingerprint of the rows retention keeps, computed from the given table."""
    return psql(f"""
SELECT count(*) || '|' || sum(hashtextextended(t::text, 0)) FROM {table} t
WHERE snapshot_date > current_date - {RETENTION}
   OR snapshot_date = (SELECT max(snapshot_date) FROM {table} b
                       WHERE b.account_id = t.account_id {key} AND b.snapshot_date <= current_date - {RETENTION});""")


def full_fingerprint(table: str) -> str:
    return psql(f"SELECT count(*) || '|' || coalesce(sum(hashtextextended(t::text, 0)), 0) FROM {table} t;")


def main() -> int:
    started = sh(["docker", "run", "-d", "--rm", "--name", CONTAINER, "--network", "none",
                  "-e", "POSTGRES_USER=fst", "-e", "POSTGRES_DB=fstservice",
                  "-e", "POSTGRES_HOST_AUTH_METHOD=trust", "postgres:17"])
    assert started.returncode == 0, started.stderr
    try:
        for _ in range(60):
            if sh(["docker", "exec", CONTAINER, "pg_isready", "-U", "fst", "-d", "fstservice"]).returncode == 0:
                break
            time.sleep(1)
        time.sleep(2)
        psql(SCHEMA)
        tables = [*PARTITIONS, "composite_rank_history"]
        keys = {t: "AND b.instrument = t.instrument" for t in PARTITIONS} | {"composite_rank_history": ""}
        with tempfile.TemporaryDirectory(prefix="fst-rh-drill-") as tmp:
            root = pathlib.Path(tmp)
            tool("build", root=root)
            # The current day's rows change between build and swap; the swap must copy them.
            psql("""
UPDATE rank_history SET total_score_rank = total_score_rank + 1000 WHERE snapshot_date = current_date;
INSERT INTO rank_history (account_id, instrument, snapshot_date, adjusted_skill_rank, weighted_rank,
    fc_rate_rank, total_score_rank, max_score_percent_rank)
SELECT 'late' || k, 'Solo_Bass', current_date, k, k, k, k, k FROM generate_series(1, 5) k;
INSERT INTO composite_rank_history (account_id, snapshot_date, composite_rank)
SELECT 'late' || k, current_date, k FROM generate_series(1, 5) k;""")
            expected = {t: kept_fingerprint(t, keys[t]) for t in tables}
            originals = {t: full_fingerprint(t) for t in tables}
            tool("swap", root=root)
            for t in tables:
                assert full_fingerprint(t) == expected[t], f"{t}: replacement differs from the retained rows"
                assert full_fingerprint(f"{t}_rtn_old") == originals[t], f"{t}: original changed"
            assert psql("SELECT count(*) FROM pg_inherits WHERE inhparent = 'rank_history'::regclass;") == "9"
            assert psql("SELECT indisvalid FROM pg_index WHERE indexrelid = 'ix_rh_retention_cutoff_brin'::regclass;") == "t"
            assert psql("SELECT count(*) FROM pg_constraint WHERE conname LIKE '%_rtn_bound';") == "0"
            assert psql("SELECT count(*) FROM rank_history_retention_state "
                        f"WHERE retained_cutoff = current_date - {RETENTION};") == "10"
            assert psql("SELECT count(*) FROM rank_history WHERE account_id LIKE 'late%';") == "5"
            # Routing through the parent still works after the swap.
            psql("INSERT INTO rank_history (account_id, instrument, snapshot_date, adjusted_skill_rank, weighted_rank, "
                 "fc_rate_rank, total_score_rank, max_score_percent_rank) "
                 "VALUES ('post', 'Solo_PeripheralDrums', current_date, 1, 1, 1, 1, 1);")
            tool("rollback", "rank_history_pro_drums", root=root)
            assert psql("SELECT count(*) FROM rank_history_pro_drums WHERE account_id = 'post';") == "1"
            assert psql("SELECT count(*) FROM rank_history_pro_drums;") == str(int(originals["rank_history_pro_drums"].split("|")[0]) + 1)
            assert psql("SELECT count(*) FROM rank_history_retention_state WHERE scope = 'solo:Solo_PeripheralDrums';") == "0"
            assert psql("SELECT count(*) FROM pg_inherits WHERE inhparent = 'rank_history'::regclass;") == "9"
            assert psql("SELECT indisvalid FROM pg_index WHERE indexrelid = 'ix_rh_retention_cutoff_brin'::regclass;") == "t"
            assert psql("SELECT count(*) FROM pg_inherits WHERE inhparent = 'ix_rh_retention_cutoff_brin'::regclass "
                        "AND inhrelid = 'rank_history_pro_drums_snapshot_date_brin'::regclass;") == "1"
            assert psql("SELECT count(*) FROM pg_constraint WHERE conname LIKE '%_rtn_bound';") == "0"
            tool("archive", root=root)
            tool("drop", root=root)
            for t in tables:
                assert psql(f"SELECT to_regclass('public.{t}_rtn_old') IS NULL;") == "t", f"{t}: old table left"
            assert psql("SELECT to_regclass('public.rank_history_pro_drums_rtn_new') IS NOT NULL;") == "t"
            dumps = sorted(p.name for p in root.glob("*.dump"))
            assert len(dumps) == 9, dumps
        print("drill passed: rewrite, late writes, swap, rollback, archive, and drop behave as specified")
        return 0
    finally:
        sh(["docker", "rm", "-f", CONTAINER])


if __name__ == "__main__":
    sys.exit(main())
