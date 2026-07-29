import datetime

VALID_CATEGORY = "valid_epic_json"


def select_payload_control_scopes(workload, count):
    if count <= 0:
        return []

    by_instrument = {}
    for item in workload:
        by_instrument.setdefault(item["instrument"], []).append(item)

    selected = []
    instruments = sorted(by_instrument)
    offset = 0
    while len(selected) < count:
        added = False
        for instrument in instruments:
            items = by_instrument[instrument]
            if offset < len(items):
                selected.append(items[offset])
                added = True
                if len(selected) == count:
                    break
        if not added:
            break
        offset += 1
    return selected


def build_payload_control_workload(workload, count):
    pairs = []
    for item in select_payload_control_scopes(workload, count):
        pairs.extend([dict(item), dict(item)])
    return pairs


def plan_distinct_alternates(proxies, unresolved):
    if len(unresolved) > len(proxies):
        raise ValueError("one recovery batch cannot exceed the proxy count")

    attempted_by_batch = {
        proxy
        for item in unresolved
        for proxy in item.get("attemptedProxies", [])
    }
    chosen = []
    used = set()
    for item in unresolved:
        attempted = set(item.get("attemptedProxies", []))
        available = [
            proxy
            for proxy in proxies
            if proxy not in attempted_by_batch and proxy not in used
        ]
        if not available:
            available = [
                proxy
                for proxy in proxies
                if proxy not in attempted and proxy not in used
            ]
        if not available:
            raise ValueError(
                f"no distinct alternate remains for request {item['originalIndex']}"
            )
        selected = available[0]
        chosen.append(selected)
        used.add(selected)

    return chosen + [proxy for proxy in proxies if proxy not in used]


def evaluate_payload_control_pairs(results, maximum_start_skew_ms=250):
    if len(results) % 2:
        raise ValueError("payload control results must contain complete pairs")

    pairs = []
    for pair_index in range(0, len(results), 2):
        left = results[pair_index]
        right = results[pair_index + 1]
        scope_match = left.get("scopeHash") == right.get("scopeHash")
        distinct_proxies = left.get("proxy") != right.get("proxy")
        valid = (
            left.get("category") == VALID_CATEGORY
            and right.get("category") == VALID_CATEGORY
        )
        fingerprint_match = (
            valid
            and left.get("entriesSha256")
            and left.get("entriesSha256") == right.get("entriesSha256")
        )

        start_skew_ms = None
        left_started = left.get("startedAtUtc")
        right_started = right.get("startedAtUtc")
        if left_started and right_started:
            left_time = datetime.datetime.fromisoformat(left_started)
            right_time = datetime.datetime.fromisoformat(right_started)
            start_skew_ms = abs((right_time - left_time).total_seconds()) * 1000

        start_skew_ok = (
            start_skew_ms is not None
            and start_skew_ms <= maximum_start_skew_ms
        )
        pair_passed = (
            scope_match
            and distinct_proxies
            and valid
            and fingerprint_match
            and start_skew_ok
        )
        pairs.append(
            {
                "pairIndex": pair_index // 2,
                "scopeHash": left.get("scopeHash"),
                "leftProxy": left.get("proxy"),
                "rightProxy": right.get("proxy"),
                "scopeMatch": scope_match,
                "distinctProxies": distinct_proxies,
                "valid": valid,
                "fingerprintMatch": fingerprint_match,
                "startSkewMilliseconds": start_skew_ms,
                "startSkewPassed": start_skew_ok,
                "passed": pair_passed,
            }
        )

    return {
        "pairCount": len(pairs),
        "failedPairCount": sum(not pair["passed"] for pair in pairs),
        "fingerprintDifferenceCount": sum(
            pair["valid"] and not pair["fingerprintMatch"] for pair in pairs
        ),
        "invalidPairCount": sum(not pair["valid"] for pair in pairs),
        "sameProxyPairCount": sum(not pair["distinctProxies"] for pair in pairs),
        "startSkewFailureCount": sum(
            not pair["startSkewPassed"] for pair in pairs
        ),
        "pairs": pairs,
    }


def evaluate_gate(
    *,
    useful_rps,
    prior_useful_rps,
    minimum_improvement_percent,
    unrecovered,
    retry_amplification,
    combined_429_503_percent,
    three_bad_windows,
    retained_percent,
    shared_state_unchanged,
    public_health_failures,
    payload_control,
    peak_memory_bytes,
    maximum_peak_memory_bytes,
    peak_pids,
    maximum_peak_pids,
    scratch_bytes_after,
    calibration_step=False,
):
    improvement_percent = (
        100 * (useful_rps - prior_useful_rps) / prior_useful_rps
    )
    reasons = []
    if unrecovered:
        reasons.append("unrecovered_responses")
    if not shared_state_unchanged:
        reasons.append("shared_state_difference")
    if payload_control["failedPairCount"]:
        reasons.append("matched_payload_control_difference")
    if (
        not calibration_step
        and improvement_percent < minimum_improvement_percent
    ):
        reasons.append("useful_rps_improvement_below_10_percent")
    if retry_amplification > 1.50:
        reasons.append("retry_amplification_above_1_50")
    if combined_429_503_percent > 5:
        reasons.append("combined_429_503_above_5_percent")
    if three_bad_windows:
        reasons.append("three_consecutive_minute_windows_above_10_percent")
    if retained_percent < 80:
        reasons.append("healthy_exit_retention_below_80_percent")
    if public_health_failures:
        reasons.append("public_health_failure")
    if peak_memory_bytes > maximum_peak_memory_bytes:
        reasons.append("peak_memory_above_limit")
    if peak_pids > maximum_peak_pids:
        reasons.append("peak_pids_above_limit")
    if scratch_bytes_after:
        reasons.append("scratch_residue_after_canary")
    return improvement_percent, reasons
