#!/usr/bin/env python3
import argparse
import importlib.util
import pathlib
import unittest

SPEC = importlib.util.spec_from_file_location(
    "rewrite", pathlib.Path(__file__).with_name("postgres-rank-history-retention-rewrite.py"))
rewrite = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(rewrite)

ENTRY = {"build_day": "2026-10-11", "cutoff": "2026-09-11"}


class Patch:
    def __init__(self, module, **values):
        self.module, self.values, self.saved = module, values, {}

    def __enter__(self):
        for name, value in self.values.items():
            self.saved[name] = getattr(self.module, name)
            setattr(self.module, name, value)
        return self

    def __exit__(self, *exc):
        for name, value in self.saved.items():
            setattr(self.module, name, value)


class NamesTests(unittest.TestCase):
    def test_partition_and_composite_scopes_match_the_worker(self):
        self.assertEqual(rewrite.names("rank_history_solo_bass")["scope"], "solo:Solo_Bass")
        self.assertEqual(rewrite.names("composite_rank_history")["scope"], "composite")

    def test_composite_keeps_its_existing_brin_name(self):
        self.assertEqual(rewrite.names("composite_rank_history")["brin"], "ix_crh_retention_cutoff_brin")
        self.assertEqual(rewrite.names("rank_history_pro_bass")["brin"], "rank_history_pro_bass_snapshot_date_brin")

    def test_tables_cover_every_partition_and_composite_last(self):
        self.assertEqual(len(rewrite.TABLES), 10)
        self.assertEqual(rewrite.TABLES[-1], "composite_rank_history")
        self.assertEqual(set(rewrite.TABLES[:-1]), set(rewrite.PARTITIONS))

    def test_selected_rejects_unknown_tables(self):
        with self.assertRaises(rewrite.RetirementError):
            rewrite.selected(argparse.Namespace(tables=["rank_history_latest"]))
        self.assertEqual(rewrite.selected(argparse.Namespace(tables=None)), rewrite.TABLES)


class SwapSqlTests(unittest.TestCase):
    def test_partition_swap_is_one_lock_bounded_transaction_that_revalidates_the_parent_brin(self):
        sql = rewrite.swap_sql("rank_history_solo_bass", ENTRY)
        lines = sql.splitlines()
        self.assertEqual(lines[0], "BEGIN;")
        self.assertEqual(lines[-1], "COMMIT;")
        self.assertIn("SET LOCAL lock_timeout = '500ms';", sql)
        self.assertIn("LOCK TABLE public.rank_history_solo_bass IN SHARE MODE;", sql)
        self.assertIn("WHERE snapshot_date >= '2026-10-11'::date;", sql)
        order = [sql.index(s) for s in (
            "LOCK TABLE", "INSERT INTO public.rank_history_solo_bass_rtn_new",
            "DETACH PARTITION public.rank_history_solo_bass;",
            "ATTACH PARTITION public.rank_history_solo_bass FOR VALUES IN ('Solo_Bass');",
            "ALTER INDEX public.ix_rh_retention_cutoff_brin ATTACH PARTITION "
            "public.rank_history_solo_bass_snapshot_date_brin;",
            "INSERT INTO rank_history_retention_state")]
        self.assertEqual(order, sorted(order))
        self.assertIn("('solo:Solo_Bass', '2026-09-11'::date, now())", sql)
        self.assertIn("GREATEST(", sql)

    def test_swap_locks_the_replacement_first_and_only_waits_long_for_locks_readers_ignore(self):
        sql = rewrite.swap_sql("rank_history_solo_bass", ENTRY)
        order = [sql.index(s) for s in (
            "SET LOCAL lock_timeout = '3s';",
            "LOCK TABLE public.rank_history_solo_bass_rtn_new IN ACCESS EXCLUSIVE MODE;",
            "LOCK TABLE public.rank_history_solo_bass IN SHARE MODE;",
            "SET LOCAL lock_timeout = '500ms';",
            "DETACH PARTITION")]
        self.assertEqual(order, sorted(order))
        self.assertIn("ALTER TABLE public.rank_history_solo_bass RESET (autovacuum_enabled);", sql)

    def test_composite_swap_renames_without_partition_ddl(self):
        sql = rewrite.swap_sql("composite_rank_history", ENTRY)
        self.assertNotIn("PARTITION", sql)
        self.assertIn("ALTER INDEX public.composite_rank_history_rtn_new_brin RENAME TO ix_crh_retention_cutoff_brin;",
                      sql)
        self.assertIn("ALTER INDEX public.ix_crh_retention_cutoff_brin RENAME TO composite_rank_history_rtn_old_brin;",
                      sql)


class RollbackSqlTests(unittest.TestCase):
    def test_partition_rollback_upserts_new_rows_and_reattaches_the_prepared_original(self):
        with Patch(rewrite, psql=lambda sql, timeout=900: "account_id, instrument, snapshot_date, weighted_rank"):
            sql = rewrite.rollback_sql("rank_history_pro_drums", ENTRY)
        self.assertIn("ON CONFLICT (account_id, instrument, snapshot_date) DO UPDATE SET "
                      "weighted_rank = EXCLUDED.weighted_rank;", sql)
        self.assertIn("ALTER INDEX public.rank_history_pro_drums_rtn_old_brin RENAME TO "
                      "rank_history_pro_drums_snapshot_date_brin;", sql)
        self.assertLess(sql.index("ATTACH PARTITION public.rank_history_pro_drums FOR VALUES"),
                        sql.index("ALTER INDEX public.ix_rh_retention_cutoff_brin ATTACH PARTITION"))
        self.assertIn("DELETE FROM rank_history_retention_state WHERE scope = 'solo:Solo_PeripheralDrums';", sql)


class RetryTests(unittest.TestCase):
    def test_lock_timeouts_retry_then_refuse(self):
        calls, sleeps = [], []

        def failing(sql, timeout=900):
            calls.append(sql)
            raise rewrite.RetirementError("ERROR: canceling statement due to lock timeout")

        with Patch(rewrite, psql=failing):
            with self.assertRaises(rewrite.TransientRefusal):
                rewrite.run_with_lock_retries("SQL", "swap", timeout=10, sleep=sleeps.append)
        self.assertEqual(len(calls), rewrite.SWAP_ATTEMPTS)
        self.assertEqual(len(sleeps), rewrite.SWAP_ATTEMPTS - 1)

    def test_other_errors_are_not_retried(self):
        calls = []

        def failing(sql, timeout=900):
            calls.append(sql)
            raise rewrite.RetirementError("ERROR: relation does not exist")

        with Patch(rewrite, psql=failing):
            with self.assertRaises(rewrite.RetirementError):
                rewrite.run_with_lock_retries("SQL", "swap", timeout=10, sleep=lambda _: None)
        self.assertEqual(len(calls), 1)

    def test_success_reports_the_attempt(self):
        results = iter([rewrite.RetirementError("deadlock detected"), "42"])

        def flaky(sql, timeout=900):
            value = next(results)
            if isinstance(value, Exception):
                raise value
            return value

        with Patch(rewrite, psql=flaky):
            self.assertEqual(rewrite.run_with_lock_retries("SQL", "swap", 10, sleep=lambda _: None), ("42", 2))


class WindowTests(unittest.TestCase):
    def test_closed_window_is_transient(self):
        with Patch(rewrite.retire, probe_window=lambda url: (False, "phase post.compute_rankings")):
            with self.assertRaises(rewrite.TransientRefusal):
                rewrite.require_window("fetch")
        self.assertEqual(rewrite.require_window("any"), "window check disabled")


if __name__ == "__main__":
    unittest.main()
