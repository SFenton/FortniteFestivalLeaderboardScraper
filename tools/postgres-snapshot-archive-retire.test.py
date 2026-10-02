#!/usr/bin/env python3
import importlib.util
import json
import pathlib
import tempfile
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
            with self.assertRaises(retire.RetirementError) as raised:
                retire.detach_and_drop(retire.parse_child(IDENTITY), sleep=lambda _: None)
            self.assertNotIsInstance(raised.exception, retire.TransientRefusal)
        finally:
            retire.psql = original

    def test_exhausted_lock_contention_is_transient(self):
        def fake_psql(sql, timeout=900):
            raise retire.RetirementError("psql failed: ERROR:  canceling statement due to lock timeout")

        original = retire.psql
        retire.psql = fake_psql
        try:
            with self.assertRaises(retire.TransientRefusal):
                retire.detach_and_drop(retire.parse_child(IDENTITY), sleep=lambda _: None)
        finally:
            retire.psql = original


def info(status="updating", phase="scrape.leaderboards", sub="fetching_leaderboards"):
    return {"currentUpdate": {"scrapeId": 1457, "status": status, "phaseId": phase, "subOperation": sub}}


class WindowTests(unittest.TestCase):
    def test_only_network_bound_fetch_sub_operations_open_the_window(self):
        for sub in ("fetching_leaderboards", "fetching_pages", "awaiting_band"):
            with self.subTest(sub=sub):
                self.assertTrue(retire.network_bound_window(info(sub=sub))[0])

    def test_flush_index_idle_and_post_processing_close_the_window(self):
        for payload in (
            info(sub="flushing_band"), info(sub="draining_solo_writes"), info(sub="creating_band_indexes"),
            info(sub="dropping_solo_indexes"), info(sub="some_future_step"), info(sub=None),
            info(phase="post.band_maintenance", sub="fetching_leaderboards"),
            info(status="idle"), {"currentUpdate": None}, {},
        ):
            with self.subTest(payload=payload):
                self.assertFalse(retire.network_bound_window(payload)[0])

    def test_unreachable_service_info_is_never_a_window(self):
        original = retire.fetch_service_info

        def boom(url, timeout=10):
            raise OSError("connection refused")

        retire.fetch_service_info = boom
        try:
            is_open, why = retire.probe_window("http://127.0.0.1:1/x")
        finally:
            retire.fetch_service_info = original
        self.assertFalse(is_open)
        self.assertIn("unavailable", why)


class AutoTests(unittest.TestCase):
    HASH = "a" * 64

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.disable = pathlib.Path(self.tmp.name) / "AUTO_DISABLED"
        self.saved = {name: getattr(retire, name) for name in
                      ("probe_window", "select_auto_cycle", "command_retire", "preflight")}
        self.retire_calls = []
        retire.preflight = lambda: None
        retire.probe_window = lambda url: (True, "scrape 1457 scrape.leaderboards/fetching_leaderboards")
        retire.select_auto_cycle = lambda hours: {
            "cycle_id": 112, "candidate_identity_hash": self.HASH, "trigger_scrape_id": 1456,
            "trigger_publication_id": 393}

    def tearDown(self):
        for name, value in self.saved.items():
            setattr(retire, name, value)
        self.tmp.cleanup()

    def args(self):
        return retire.build_parser().parse_args(["auto", "--disable-file", str(self.disable)])

    def fake_retire(self, outcome=0):
        def run(args, window=None):
            self.retire_calls.append((args, window))
            if isinstance(outcome, BaseException):
                raise outcome
            return outcome
        retire.command_retire = run

    def test_disable_file_short_circuits(self):
        self.disable.write_text("{}")
        self.fake_retire()
        self.assertEqual(retire.command_auto(self.args()), 0)
        self.assertEqual(self.retire_calls, [])

    def test_outside_window_and_missing_cycle_do_nothing(self):
        self.fake_retire()
        retire.probe_window = lambda url: (False, "phase post.rivals")
        self.assertEqual(retire.command_auto(self.args()), 0)
        retire.probe_window = lambda url: (True, "fetching")
        retire.select_auto_cycle = lambda hours: None
        self.assertEqual(retire.command_auto(self.args()), 0)
        self.assertEqual(self.retire_calls, [])
        self.assertFalse(self.disable.exists())

    def test_binds_selected_cycle_hash_and_rechecks_window(self):
        self.fake_retire()
        self.assertEqual(retire.command_auto(self.args()), 0)
        (args, window), = self.retire_calls
        self.assertEqual((args.cycle, args.expected_candidate_hash, args.only, args.archive_only),
                         (112, self.HASH, None, False))
        self.assertIsNotNone(window)
        self.assertTrue(window()[0])
        self.disable.write_text("{}")
        self.assertEqual(window(), (False, "AUTO_DISABLED present"))

    def test_transient_refusal_defers_without_tripping(self):
        self.fake_retire(retire.TransientRefusal("12 lock waits are active"))
        self.assertEqual(retire.command_auto(self.args()), 0)
        self.assertFalse(self.disable.exists())

    def test_integrity_failure_trips_and_disables_later_runs(self):
        self.fake_retire(retire.RetirementError("archive holds 1 rows, table held 2"))
        with self.assertRaises(retire.RetirementError):
            retire.command_auto(self.args())
        record = json.loads(self.disable.read_text())
        self.assertEqual(record["cycle_id"], 112)
        self.assertIn("archive holds 1 rows", record["error"])
        self.retire_calls.clear()
        self.fake_retire()
        self.assertEqual(retire.command_auto(self.args()), 0)
        self.assertEqual(self.retire_calls, [])

    def test_selection_failures_trip_and_main_exits_non_zero(self):
        self.fake_retire()

        def broken_selection(hours):
            raise retire.RetirementError("psql failed: relation does not exist")

        retire.select_auto_cycle = broken_selection
        self.assertEqual(retire.main(["auto", "--disable-file", str(self.disable)]), 2)
        record = json.loads(self.disable.read_text())
        self.assertIsNone(record["cycle_id"])
        self.assertIn("relation does not exist", record["error"])
        self.assertEqual(self.retire_calls, [])

    def test_malformed_selection_trips(self):
        self.fake_retire()
        retire.select_auto_cycle = lambda hours: {"cycle_id": 112}
        with self.assertRaises(KeyError):
            retire.command_auto(self.args())
        self.assertEqual(json.loads(self.disable.read_text())["cycle_id"], 112)
        self.assertEqual(self.retire_calls, [])

    def test_preflight_refusal_defers_before_selection(self):
        self.fake_retire()

        def unhealthy():
            raise retire.TransientRefusal("fst-postgres is not running and healthy (running|starting)")

        def must_not_select(hours):
            raise AssertionError("selection ran after a preflight refusal")

        retire.preflight = unhealthy
        retire.select_auto_cycle = must_not_select
        self.assertEqual(retire.command_auto(self.args()), 0)
        self.assertFalse(self.disable.exists())

    def test_retire_stops_before_touching_a_child_when_window_closes(self):
        saved = {name: getattr(retire, name) for name in
                 ("load_cycle", "cycle_dir", "LOCK_PATH")}
        retire.command_retire = self.saved["command_retire"]
        retire.load_cycle = lambda cycle, digest: [retire.parse_child(IDENTITY)]
        retire.cycle_dir = lambda cycle: pathlib.Path(self.tmp.name) / f"cycle-{cycle}"
        retire.LOCK_PATH = pathlib.Path(self.tmp.name) / ".lock"

        def must_not_run():
            raise AssertionError("preflight ran after the window closed")

        retire.preflight = must_not_run
        try:
            args = retire.build_parser().parse_args(
                ["retire", "--cycle", "112", "--expected-candidate-hash", self.HASH, "--limit", "5"])
            self.assertEqual(retire.command_retire(args, window=lambda: (False, "sub-operation flushing_band")), 0)
        finally:
            for name, value in saved.items():
                setattr(retire, name, value)


if __name__ == "__main__":
    unittest.main()
