#!/usr/bin/env python3
import argparse
import importlib.util
import pathlib
import unittest

SPEC = importlib.util.spec_from_file_location(
    "compact", pathlib.Path(__file__).with_name("postgres-snapshot-sparse-compact.py"))
compact = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(compact)
retire = compact.retire

CHILD = {"instrument": "Solo_Guitar", "parent_schema": "public",
         "parent_relation": "leaderboard_entries_snapshot_solo_guitar", "schema": "public",
         "relation": "leaderboard_entries_snapshot_solo_guitar_s1410", "snapshot_id": 1410,
         "oid": 319182277, "relfilenode": 319182277, "total_bytes": 2 * 1024**3}


def live(**overrides):
    row = {"songs": ["song_a"], "present": True, "identity_ok": True, "attached": True, "parent_ok": True,
           "total_bytes": CHILD["total_bytes"], "running_scrape": False, "publication_scrape": False,
           "writer_failure": False, "ever_held": False, "surface_binding": False, "has_trigger": False,
           "resume_scrape": False}
    row.update(overrides)
    return row


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


class BlockerTests(unittest.TestCase):
    def test_all_clear_has_no_blocker(self):
        self.assertIsNone(compact.compaction_blocker(CHILD, live()))

    def test_each_root_blocks(self):
        for key, value, reason in [
            ("present", False, "missing"),
            ("identity_ok", False, "oid-or-relfilenode-changed"),
            ("attached", False, "not-attached"),
            ("parent_ok", None, "parent-changed"),
            ("running_scrape", True, "running-scrape"),
            ("publication_scrape", True, "named-publication-scrape"),
            ("writer_failure", True, "unreplayed-writer-failure"),
            ("ever_held", True, "retention-hold-history"),
            ("surface_binding", True, "publication-surface-binding"),
            ("has_trigger", True, "has-trigger"),
            ("resume_scrape", True, "configured-resume-scrape"),
            ("songs", [], "no-live-scopes"),
        ]:
            with self.subTest(key=key):
                self.assertEqual(compact.compaction_blocker(CHILD, live(**{key: value})), reason)

    def test_operator_excluded_child_is_blocked(self):
        child = {**CHILD, "instrument": "Solo_Bass", "snapshot_id": 1308}
        self.assertEqual(compact.compaction_blocker(child, live()), "operator-excluded")


class NamingTests(unittest.TestCase):
    def test_names_are_keyed_by_old_oid_and_fit_identifiers(self):
        names = compact.object_names(CHILD)
        self.assertEqual(names["bound"], "leaderboard_entries_snapshot_solo_guitar_s1410_b319182277")
        self.assertTrue(names["index_prefix"].endswith("_k319182277_"))
        self.assertTrue(all(len(v) <= 63 for v in names.values()))

    def test_overlong_names_are_refused(self):
        with self.assertRaises(compact.RetirementError):
            compact.object_names({**CHILD, "relation": "x" * 60})

    def test_text_array_quotes_values(self):
        self.assertEqual(compact.text_array([]), "ARRAY[]::text[]")
        self.assertEqual(compact.text_array(["a'b", "c"]), "ARRAY['a''b','c']::text[]")


class BuildTests(unittest.TestCase):
    def test_replacement_mirrors_parent_indexes_and_bound(self):
        calls = []
        responses = iter([
            "f",
            '[{"primary": true, "def": "CREATE UNIQUE INDEX leaderboard_entries_snapshot_solo_guitar_pkey ON ONLY '
            'public.leaderboard_entries_snapshot_solo_guitar USING btree (snapshot_id, song_id, instrument, account_id)"},'
            ' {"primary": false, "def": "CREATE INDEX leaderboard_entries_snapshot_solo_guitar_idx ON ONLY '
            'public.leaderboard_entries_snapshot_solo_guitar USING btree (snapshot_id, song_id, instrument, score DESC)"}]',
            ""])

        def fake_psql(sql, timeout=900):
            calls.append(sql)
            return next(responses)

        with Patch(retire, psql=fake_psql):
            compact.build_replacement(CHILD, ["song_a"], "leaderboard_entries_snapshot_solo_guitar_s1410_cnew")
        ddl = calls[-1]
        self.assertIn("LIKE public.leaderboard_entries_snapshot_solo_guitar_s1410 INCLUDING DEFAULTS", ddl)
        self.assertNotIn("INCLUDING CONSTRAINTS", ddl)
        self.assertIn("WHERE song_id = ANY(ARRAY['song_a']::text[])", ddl)
        self.assertIn("CHECK (snapshot_id = 1410 AND instrument = 'Solo_Guitar')", ddl)
        self.assertIn("CREATE UNIQUE INDEX leaderboard_entries_snapshot_solo_guitar_s1410_k319182277_0 ON public."
                      "leaderboard_entries_snapshot_solo_guitar_s1410_cnew USING btree "
                      "(snapshot_id, song_id, instrument, account_id);", ddl)
        self.assertIn("PRIMARY KEY USING INDEX leaderboard_entries_snapshot_solo_guitar_s1410_k319182277_0", ddl)
        self.assertIn("USING btree (snapshot_id, song_id, instrument, score DESC);", ddl)
        self.assertTrue(ddl.strip().endswith("COMMIT;"))

    def test_attached_leftover_replacement_is_refused(self):
        responses = iter(["t", "t"])
        with Patch(retire, psql=lambda sql, timeout=900: next(responses)):
            with self.assertRaises(compact.RetirementError):
                compact.build_replacement(CHILD, ["song_a"], "x_cnew")


class SwapTests(unittest.TestCase):
    def test_swap_retries_lock_timeouts_then_succeeds(self):
        calls = []
        outcomes = iter([compact.RetirementError("psql failed: ERROR:  canceling statement due to lock timeout"), ""])

        def fake_psql(sql, timeout=900):
            calls.append(sql)
            value = next(outcomes)
            if isinstance(value, Exception):
                raise value
            return value

        with Patch(retire, psql=fake_psql):
            attempts = compact.swap_in(CHILD, ["song_a"], "rel_cnew", 0, sleep=lambda _: None)
        self.assertEqual(attempts, 2)
        sql = calls[-1]
        self.assertLess(sql.index("DETACH PARTITION"), sql.index("ATTACH PARTITION"))
        self.assertIn("FOR VALUES IN (1410)", sql)
        self.assertIn("compaction liveness changed during swap", sql)
        self.assertIn("a whole-child liveness root appeared", sql)
        self.assertIn("FROM ONLY public.leaderboard_entries_snapshot_solo_guitar_default", sql)
        self.assertIn("lock_timeout = '500ms'", sql)
        self.assertNotIn("CONCURRENTLY", sql)
        lock = sql.index("IN SHARE MODE")
        for table in compact.LIVENESS_ROOT_TABLES:
            self.assertIn(f"public.{table}", sql[:lock])
        self.assertLess(lock, sql.index("DETACH PARTITION"))
        self.assertIn("LOCK TABLE public.leaderboard_entries_snapshot_solo_guitar_s1410 IN SHARE MODE", sql)
        self.assertLess(sql.index("LOCK TABLE public.leaderboard_entries_snapshot_solo_guitar_s1410"),
                        sql.index("$guard$"))
        self.assertLess(sql.index("$guard$"), sql.index("DETACH PARTITION"))

    def test_exhausted_swap_contention_is_transient(self):
        for message in ("canceling statement due to lock timeout", "deadlock detected",
                        "canceling statement due to statement timeout"):
            def fake_psql(sql, timeout=900, message=message):
                raise compact.RetirementError(f"psql failed: ERROR:  {message}")

            with self.subTest(message=message), Patch(retire, psql=fake_psql):
                with self.assertRaises(compact.TransientRefusal):
                    compact.swap_in(CHILD, ["song_a"], "rel_cnew", 0, sleep=lambda _: None)

    def test_whole_child_guard_includes_resume_and_bindings(self):
        sql = compact.whole_child_root_sql(CHILD, 1410)
        for marker in ("status = 'running'", "live_publications WHERE scrape_id = 1410", "scrape_writer_failures",
                       "snapshot_generation_retention_holds", "publication_surface_bindings", "pg_trigger",
                       "1410 = 1410"):
            self.assertIn(marker, sql)

    def test_unreadable_worker_configuration_defers(self):
        class Failed:
            returncode, stdout = 1, ""

        with Patch(compact.subprocess, run=lambda *a, **k: Failed()):
            with self.assertRaises(compact.TransientRefusal):
                compact.configured_resume_scrape_id()

    def test_worker_configuration_reads_container_and_resume(self):
        class Inspected:
            returncode, stdout = 0, "abc123\nPATH=/bin\nScraper__ResumeScrapeId=1410\n"

        with Patch(compact.subprocess, run=lambda *a, **k: Inspected()):
            self.assertEqual(compact.worker_configuration(), {"container_id": "abc123", "resume_scrape_id": 1410})

    def test_changed_worker_configuration_defers_before_swap(self):
        dropped, swapped = [], []
        with Patch(compact, scope_liveness=lambda child, resume: live(songs=["song_a"]),
                   relation_identity=lambda relation: None,
                   measure=lambda child, songs: {"rows": 100, "live_rows": 10, "fingerprint": "1",
                                                 "live_fingerprint": "9", "songs": 3},
                   build_replacement=lambda child, songs, new: None,
                   table_fingerprint=lambda relation: (10, "9"),
                   worker_configuration=lambda: {"container_id": "new", "resume_scrape_id": 0},
                   WORKER_MUTATION_LOCK_PATH=self.lock_path(),
                   swap_in=lambda *a, **k: swapped.append(a)), \
                Patch(retire, dump_child=lambda child, path: path.write_bytes(b"x"),
                      verify_archive=lambda *a, **k: {"toc_ok": True}, append_manifest=lambda *a, **k: None,
                      psql=lambda sql, timeout=900: dropped.append(sql) or ("t" if "to_regclass" in sql else ""),
                      ARCHIVE_ROOT=pathlib.Path(self.tmp.name)):
            with self.assertRaises(compact.TransientRefusal):
                compact.compact_child(CHILD, argparse.Namespace(max_live_fraction=0.5, min_reclaim_bytes=0,
                                                                drill_every=0, window=None),
                                      0, 0, {"container_id": "old", "resume_scrape_id": 0})
        self.assertEqual(swapped, [])
        self.assertTrue(any("DROP TABLE public.leaderboard_entries_snapshot_solo_guitar_s1410_cnew" in sql
                            for sql in dropped))

    def lock_path(self):
        path = pathlib.Path(self.tmp.name) / "guard.lock"
        path.touch()
        return path

    def test_worker_mutation_fence_defers_while_a_deployment_holds_the_lock(self):
        import fcntl
        path = self.lock_path()
        with path.open("r") as holder:
            fcntl.flock(holder, fcntl.LOCK_EX | fcntl.LOCK_NB)
            with self.assertRaises(compact.TransientRefusal):
                with compact.worker_mutation_fence(path):
                    pass
        with compact.worker_mutation_fence(path):
            with path.open("r") as deployer:
                with self.assertRaises(BlockingIOError):
                    fcntl.flock(deployer, fcntl.LOCK_EX | fcntl.LOCK_NB)

    def test_missing_worker_mutation_lock_defers(self):
        with self.assertRaises(compact.TransientRefusal):
            with compact.worker_mutation_fence(pathlib.Path(self.tmp.name) / "absent.lock"):
                pass

    def setUp(self):
        import tempfile
        self.tmp = tempfile.TemporaryDirectory()

    def tearDown(self):
        self.tmp.cleanup()

    def test_interrupted_compaction_leftover_blocks_the_child(self):
        with Patch(compact, relation_identity=lambda relation: {"oid": 1, "relfilenode": 1, "attached": False}):
            with self.assertRaises(compact.RetirementError):
                compact.compact_child(CHILD, argparse.Namespace(), 0, 0)

    def test_non_lock_swap_errors_are_not_retried(self):
        calls = []

        def fake_psql(sql, timeout=900):
            calls.append(sql)
            raise compact.RetirementError("psql failed: ERROR:  compaction liveness changed during swap")

        with Patch(retire, psql=fake_psql):
            with self.assertRaises(compact.RetirementError) as raised:
                compact.swap_in(CHILD, ["song_a"], "rel_cnew", 0, sleep=lambda _: None)
        self.assertNotIsInstance(raised.exception, compact.TransientRefusal)
        self.assertEqual(len(calls), 1)


class SelectionTests(unittest.TestCase):
    def args(self, **overrides):
        values = {"max_live_fraction": 0.5, "min_reclaim_bytes": 128 * 1024**2, "drill_every": 10, "window": None}
        values.update(overrides)
        return argparse.Namespace(**values)

    def run_child(self, measured, **arg_overrides):
        dumped = []
        with Patch(compact, scope_liveness=lambda child, resume: live(songs=["song_a", "song_b"]),
                   relation_identity=lambda relation: None,
                   measure=lambda child, songs: measured), \
                Patch(retire, dump_child=lambda child, path: dumped.append(path)):
            result = compact.compact_child(CHILD, self.args(**arg_overrides), 0, 0)
        return result, dumped

    def test_dense_children_are_skipped_before_archiving(self):
        result, dumped = self.run_child({"rows": 100, "live_rows": 60, "fingerprint": "1",
                                         "live_fingerprint": "1", "songs": 3})
        self.assertIsNone(result)
        self.assertEqual(dumped, [])

    def test_small_reclaims_are_skipped_before_archiving(self):
        result, dumped = self.run_child({"rows": 100, "live_rows": 10, "fingerprint": "1",
                                         "live_fingerprint": "1", "songs": 3},
                                        min_reclaim_bytes=4 * 1024**3)
        self.assertIsNone(result)
        self.assertEqual(dumped, [])

    def test_blocked_children_are_skipped_without_measuring(self):
        measured = []
        with Patch(compact, scope_liveness=lambda child, resume: live(running_scrape=True),
                   relation_identity=lambda relation: None,
                   measure=lambda child, songs: measured.append(child)):
            self.assertIsNone(compact.compact_child(CHILD, self.args(), 0, 0))
        self.assertEqual(measured, [])


if __name__ == "__main__":
    unittest.main()
