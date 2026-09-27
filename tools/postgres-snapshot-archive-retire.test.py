#!/usr/bin/env python3
import importlib.util
import pathlib
import unittest

SPEC = importlib.util.spec_from_file_location(
    "retire", pathlib.Path(__file__).with_name("postgres-snapshot-archive-retire.py"))
retire = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(retire)

IDENTITY = ("Solo_Bass|public|leaderboard_entries_snapshot_solo_bass|278822940|319182059|"
            "LIST (snapshot_id)|FOR VALUES IN ('Solo_Bass')|public|"
            "leaderboard_entries_snapshot_solo_bass_s1302|1302|319182066|319182066|FOR VALUES IN ('1302')")


def eligible_row(**overrides):
    row = {"present": True, "identity_ok": True, "relispartition": True, "parent_ok": True,
           "live_scope": False, "active_state": False, "projection_source": False,
           "ever_held": False, "has_trigger": False, "surface_binding": False,
           "instrument": "Solo_Bass", "snapshot_id": 1302}
    row.update(overrides)
    return row


class ParseChildTests(unittest.TestCase):
    def test_parses_planner_identity(self):
        child = retire.parse_child(IDENTITY)
        self.assertEqual(child["relation"], "leaderboard_entries_snapshot_solo_bass_s1302")
        self.assertEqual(child["parent_relation"], "leaderboard_entries_snapshot_solo_bass")
        self.assertEqual((child["snapshot_id"], child["oid"], child["relfilenode"]), (1302, 319182066, 319182066))

    def test_rejects_unexpected_shape_and_unsafe_identifiers(self):
        with self.assertRaises(retire.RetirementError):
            retire.parse_child("Solo_Bass|public")
        with self.assertRaises(retire.RetirementError):
            retire.parse_child(IDENTITY.replace("leaderboard_entries_snapshot_solo_bass_s1302", "x; DROP TABLE y"))

    def test_rejects_children_outside_the_snapshot_root(self):
        with self.assertRaises(retire.RetirementError):
            retire.parse_child(IDENTITY.replace("leaderboard_entries_snapshot_solo_bass|", "band_members|", 1))


class EligibilityTests(unittest.TestCase):
    def test_all_clear_row_is_eligible(self):
        self.assertEqual(retire.is_eligible(eligible_row()), (True, "eligible"))

    def test_each_live_root_blocks(self):
        for key, value, reason in [
            ("present", False, "missing"),
            ("identity_ok", False, "oid-or-relfilenode-changed"),
            ("relispartition", False, "not-attached"),
            ("parent_ok", None, "parent-changed"),
            ("live_scope", True, "bound-by-live-publication"),
            ("active_state", True, "active-snapshot-state"),
            ("projection_source", True, "solo-projection-source"),
            ("ever_held", True, "retention-hold-history"),
            ("has_trigger", True, "has-trigger"),
            ("surface_binding", True, "publication-surface-binding"),
        ]:
            with self.subTest(key=key):
                self.assertEqual(retire.is_eligible(eligible_row(**{key: value})), (False, reason))

    def test_operator_excluded_child_is_never_eligible(self):
        self.assertEqual(retire.is_eligible(eligible_row(snapshot_id=1308)), (False, "operator-excluded"))

    def test_hash_must_be_hex(self):
        with self.assertRaises(retire.RetirementError):
            retire.load_cycle(95, "not-a-hash")


class LockRetryTests(unittest.TestCase):
    def test_detach_retries_lock_timeouts_then_drops(self):
        calls = []
        responses = iter([
            retire.RetirementError("psql failed: ERROR:  canceling statement due to lock timeout"),
            "", "f", ""])

        def fake_psql(sql, timeout=900):
            calls.append(sql)
            value = next(responses)
            if isinstance(value, Exception):
                raise value
            return value

        original = retire.psql
        retire.psql = fake_psql
        try:
            attempts = retire.detach_and_drop(retire.parse_child(IDENTITY), sleep=lambda _: None)
        finally:
            retire.psql = original
        self.assertEqual(attempts, 2)
        self.assertIn("DETACH PARTITION", calls[1])
        self.assertNotIn("CONCURRENTLY", calls[1])
        self.assertIn("DROP TABLE public.leaderboard_entries_snapshot_solo_bass_s1302 RESTRICT", calls[3])

    def test_detach_does_not_retry_other_errors(self):
        def fake_psql(sql, timeout=900):
            raise retire.RetirementError("psql failed: ERROR:  permission denied")

        original = retire.psql
        retire.psql = fake_psql
        try:
            with self.assertRaises(retire.RetirementError):
                retire.detach_and_drop(retire.parse_child(IDENTITY), sleep=lambda _: None)
        finally:
            retire.psql = original


if __name__ == "__main__":
    unittest.main()
