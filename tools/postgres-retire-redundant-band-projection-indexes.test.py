#!/usr/bin/env python3
import importlib.util
import pathlib
import unittest

SPEC = importlib.util.spec_from_file_location(
    "retire_indexes", pathlib.Path(__file__).with_name("postgres-retire-redundant-band-projection-indexes.py"))
tool = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(tool)

FALLBACK_DEF = ("CREATE INDEX current_band_leaderboard_entr_band_type_team_key_song_id_ra_idx ON "
                "public.current_band_leaderboard_entries_duets USING btree "
                "(band_type, team_key, song_id, ranking_scope, scope_combo_id)")


def row(**overrides):
    spec = tool.INDEXES[0]
    value = {**spec, "present": True, "definition": spec["definition"], "valid": True, "ready": True,
             "attached": False, "bytes": 10, "scans": 5,
             "fallback": {"name": "fb", "valid": True, "ready": True, "definition": FALLBACK_DEF}}
    value.update(overrides)
    return tool.annotate([value])[0]


class Patch:
    def __init__(self, target, **values):
        self.target, self.values, self.saved = target, values, {}

    def __enter__(self):
        for name, value in self.values.items():
            self.saved[name] = getattr(self.target, name)
            setattr(self.target, name, value)

    def __exit__(self, *exc):
        for name, value in self.saved.items():
            setattr(self.target, name, value)


class BlockerTests(unittest.TestCase):
    def test_specs_cover_each_band_partition(self):
        self.assertEqual([s["name"] for s in tool.INDEXES],
                         ["ix_cble_duets_team_scope_generation", "ix_cble_trios_team_scope_generation",
                          "ix_cble_quad_team_scope_generation"])
        for spec in tool.INDEXES:
            self.assertTrue(spec["definition"].endswith(
                "USING btree (band_type, team_key, song_id, ranking_scope, scope_combo_id, projection_generation)"))

    def test_matching_index_with_valid_fallback_is_clear(self):
        self.assertIsNone(row()["blocker"])
        self.assertIsNone(row(present=False, definition=None)["blocker"])

    def test_missing_or_invalid_fallback_blocks(self):
        self.assertIn("fallback", row(fallback=None)["blocker"])
        self.assertIn("fallback", row(fallback={"valid": False, "ready": True, "definition": FALLBACK_DEF})["blocker"])
        self.assertIn("fallback", row(fallback={"valid": True, "ready": True,
                                                "definition": FALLBACK_DEF.replace("scope_combo_id)", "x)")})["blocker"])

    def test_changed_definition_or_attachment_blocks(self):
        self.assertIn("definition differs", row(definition="CREATE INDEX other ON t USING btree (a)")["blocker"])
        self.assertIn("attached", row(attached=True)["blocker"])


class ApplyTests(unittest.TestCase):
    def test_outside_window_does_nothing(self):
        calls = []
        with Patch(tool.retire, probe_window=lambda url: (False, "phase post.rivals"),
                   psql=lambda sql, timeout=900: calls.append(sql) or ""):
            self.assertEqual(tool.main(["apply"]), 0)
        self.assertEqual(calls, [])

    def test_blockers_refuse_before_any_drop(self):
        drops = []
        with Patch(tool.retire, probe_window=lambda url: (True, "fetching"), preflight=lambda: None), \
                Patch(tool, inspect=lambda: [{k: v for k, v in row(fallback=None).items() if k != "blocker"}],
                      run_concurrently=lambda sql, sleep=None: drops.append(sql)):
            self.assertEqual(tool.main(["apply"]), 2)
        self.assertEqual(drops, [])

    def test_lock_contention_retries_then_defers(self):
        calls = []

        def busy(sql, timeout=900):
            calls.append(sql)
            raise tool.RetirementError("psql failed: ERROR:  canceling statement due to lock timeout")

        with Patch(tool.retire, psql=busy):
            with self.assertRaises(tool.TransientRefusal):
                tool.run_concurrently("DROP INDEX CONCURRENTLY IF EXISTS public.x;", sleep=lambda _: None)
        self.assertEqual(len(calls), tool.ATTEMPTS)
        self.assertIn("lock_timeout = '2s'", calls[0])

    def test_non_lock_errors_are_not_retried(self):
        calls = []

        def broken(sql, timeout=900):
            calls.append(sql)
            raise tool.RetirementError("psql failed: ERROR:  permission denied")

        with Patch(tool.retire, psql=broken):
            with self.assertRaises(tool.RetirementError):
                tool.run_concurrently("DROP INDEX CONCURRENTLY IF EXISTS public.x;", sleep=lambda _: None)
        self.assertEqual(len(calls), 1)


class RollbackTests(unittest.TestCase):
    def test_rollback_rebuilds_missing_index_concurrently(self):
        statements = []
        states = iter([[{k: v for k, v in row(present=False, definition=None).items() if k != "blocker"}],
                       [{k: v for k, v in row().items() if k != "blocker"}]])
        with Patch(tool, inspect=lambda: next(states), record=lambda entry: None,
                   run_concurrently=lambda sql, sleep=None: statements.append(sql) or 1):
            self.assertEqual(tool.main(["rollback"]), 0)
        self.assertEqual(statements, [
            "CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_cble_duets_team_scope_generation ON "
            "public.current_band_leaderboard_entries_duets USING btree "
            "(band_type, team_key, song_id, ranking_scope, scope_combo_id, projection_generation);"])

    def test_rollback_refuses_an_invalid_leftover(self):
        with Patch(tool, inspect=lambda: [{k: v for k, v in row(valid=False).items() if k != "blocker"}]):
            self.assertEqual(tool.main(["rollback"]), 2)


if __name__ == "__main__":
    unittest.main()
