import datetime
import importlib.util
import json
import pathlib
import sys
import unittest

TOOLS_DIR = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(TOOLS_DIR))

from fst_network_canary_logic import (  # noqa: E402
    build_payload_control_workload,
    evaluate_gate,
    evaluate_payload_control_pairs,
    plan_distinct_alternates,
    select_payload_control_scopes,
)


def load_runner():
    spec = importlib.util.spec_from_file_location(
        "fst_network_bounded_canary",
        TOOLS_DIR / "fst-network-bounded-canary.py",
    )
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class FstNetworkBoundedCanaryTests(unittest.TestCase):
    def test_names_smallest_concurrency_only_candidate(self):
        runner = load_runner()

        self.assertEqual((800, 32, 5), runner.PROFILES["candidate-800-32-5"])
        self.assertEqual((800, 32, 4), runner.PROFILES["candidate-800-32-4"])

    def test_selects_payload_controls_across_instruments(self):
        workload = [
            {"instrument": "B", "scopeHash": "b1"},
            {"instrument": "A", "scopeHash": "a1"},
            {"instrument": "B", "scopeHash": "b2"},
            {"instrument": "A", "scopeHash": "a2"},
        ]

        selected = select_payload_control_scopes(workload, 4)
        paired = build_payload_control_workload(workload, 4)

        self.assertEqual(["a1", "b1", "a2", "b2"], [
            item["scopeHash"] for item in selected
        ])
        self.assertEqual(8, len(paired))
        self.assertEqual(paired[0], paired[1])

    def test_rejected_1600_chain_gets_fresh_second_alternates(self):
        fixture_path = (
            TOOLS_DIR
            / "testdata"
            / "fst-network-canary"
            / "rejected-1600-recovery.json"
        )
        fixture = json.loads(fixture_path.read_text())
        unresolved = fixture["primaryFailures"]
        proxies = [f"pia-gluetun-{index}" for index in range(1, 31)]

        first_order = plan_distinct_alternates(proxies, unresolved)
        for item, result in zip(
            unresolved,
            fixture["firstAlternateResults"],
        ):
            self.assertEqual(result["proxy"], first_order.pop(0))
            item["attemptedProxies"].append(result["proxy"])

        second_order = plan_distinct_alternates(proxies, unresolved)

        self.assertEqual(["pia-gluetun-3", "pia-gluetun-4"], second_order[:2])
        for item, proxy in zip(unresolved, second_order):
            self.assertNotIn(proxy, item["attemptedProxies"])

    def test_payload_pairs_require_distinct_near_simultaneous_matches(self):
        started = datetime.datetime(2026, 7, 29, tzinfo=datetime.timezone.utc)
        results = [
            {
                "scopeHash": "scope",
                "proxy": "pia-gluetun-1",
                "category": "valid_epic_json",
                "entriesSha256": "same",
                "startedAtUtc": started.isoformat(),
            },
            {
                "scopeHash": "scope",
                "proxy": "pia-gluetun-2",
                "category": "valid_epic_json",
                "entriesSha256": "same",
                "startedAtUtc": (
                    started + datetime.timedelta(milliseconds=40)
                ).isoformat(),
            },
        ]

        evaluation = evaluate_payload_control_pairs(results, 250)

        self.assertEqual(0, evaluation["failedPairCount"])
        self.assertTrue(evaluation["pairs"][0]["passed"])

    def test_payload_difference_fails_without_using_live_variant_count(self):
        started = datetime.datetime(2026, 7, 29, tzinfo=datetime.timezone.utc)
        controls = evaluate_payload_control_pairs(
            [
                {
                    "scopeHash": "scope",
                    "proxy": "pia-gluetun-1",
                    "category": "valid_epic_json",
                    "entriesSha256": "left",
                    "startedAtUtc": started.isoformat(),
                },
                {
                    "scopeHash": "scope",
                    "proxy": "pia-gluetun-2",
                    "category": "valid_epic_json",
                    "entriesSha256": "right",
                    "startedAtUtc": started.isoformat(),
                },
            ],
            250,
        )

        _, reasons = evaluate_gate(
            useful_rps=40,
            prior_useful_rps=35.95782174836861,
            minimum_improvement_percent=10,
            unrecovered=0,
            retry_amplification=1.01,
            combined_429_503_percent=0,
            three_bad_windows=False,
            retained_percent=100,
            shared_state_unchanged=True,
            public_health_failures=[],
            payload_control=controls,
            peak_memory_bytes=600_000_000,
            maximum_peak_memory_bytes=805_306_368,
            peak_pids=220,
            maximum_peak_pids=300,
            scratch_bytes_after=0,
        )

        self.assertIn("matched_payload_control_difference", reasons)

    def test_candidate_gate_passes_at_exact_target(self):
        controls = {"failedPairCount": 0}
        prior = 35.95782174836861

        improvement, reasons = evaluate_gate(
            useful_rps=prior * 1.10,
            prior_useful_rps=prior,
            minimum_improvement_percent=10,
            unrecovered=0,
            retry_amplification=1.01,
            combined_429_503_percent=0,
            three_bad_windows=False,
            retained_percent=100,
            shared_state_unchanged=True,
            public_health_failures=[],
            payload_control=controls,
            peak_memory_bytes=600_000_000,
            maximum_peak_memory_bytes=805_306_368,
            peak_pids=220,
            maximum_peak_pids=300,
            scratch_bytes_after=0,
        )

        self.assertAlmostEqual(10, improvement)
        self.assertEqual([], reasons)

    def test_resource_limits_are_gating(self):
        _, reasons = evaluate_gate(
            useful_rps=40,
            prior_useful_rps=35.95782174836861,
            minimum_improvement_percent=10,
            unrecovered=0,
            retry_amplification=1.01,
            combined_429_503_percent=0,
            three_bad_windows=False,
            retained_percent=100,
            shared_state_unchanged=True,
            public_health_failures=[],
            payload_control={"failedPairCount": 0},
            peak_memory_bytes=805_306_369,
            maximum_peak_memory_bytes=805_306_368,
            peak_pids=301,
            maximum_peak_pids=300,
            scratch_bytes_after=1,
        )

        self.assertIn("peak_memory_above_limit", reasons)
        self.assertIn("peak_pids_above_limit", reasons)
        self.assertIn("scratch_residue_after_canary", reasons)


if __name__ == "__main__":
    unittest.main()
