import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { readFile } from "node:fs/promises";
import test from "node:test";

const scriptUrl = new URL("./postgres-tier1-replay-drill.sh", import.meta.url);

test("Tier-1 replay drill shell is valid", () => {
  execFileSync("bash", ["-n", scriptUrl.pathname], {
    stdio: "pipe",
  });
});

test("Tier-1 replay drill keeps Docker and storage isolated", async () => {
  const source = await readFile(scriptUrl, "utf8");

  assert.match(source, /--network none/);
  assert.match(source, /--network "container:\$container"/);
  assert.match(source, /\/mnt\/docker-storage\/Docker\/FestivalServiceTracker\/fst-data\/(?:evidence|replay)\//);
  assert.match(source, /FST_REPLAY_APPROVED_ROOT/);
  assert.match(source, /FST_REPLAY_APPROVED_DEVICE/);
  assert.match(source, /--no-publication/);
  assert.match(source, /--replay-profile "\$profile"/);
  assert.match(source, /baseline_profile="deterministic-v1"/);
  assert.match(source, /candidate_profile="deterministic-v1"/);
  assert.match(source, /production-option-parity-v1/);
  assert.match(source, /production-option-parity-batched-member-stats-v1/);
  assert.match(source, /-v "\$view:\$root:ro"/);
  assert.match(source, /-v "\$input_root:\$input_root:ro"/);
  assert.match(source, /-v "\$baseline_work:\$baseline_work:ro"/);
  assert.match(source, /-v "\$candidate_work:\$candidate_work:ro"/);
  assert.match(source, /"\$baseline_digest" \\\n  --replay-compare-baseline/);
  assert.match(source, /pgdata="\$scratch_root\//);
  assert.match(source, /NOSUPERUSER/);
  assert.match(source, /productionComparableTiming == false/);
  assert.match(source, /\.version == 3/);
  assert.match(source, /successfulScopeTransactions/);
  assert.match(source, /derivedSuccessfulScopeCommandExecutions/);
  assert.match(source, /derivedSuccessfulScopeRoundTrips/);
  assert.match(source, /derivedMemberStatsAggregationPasses/);
  assert.match(source, /derivedMemberStatsAggregationPassDeltaPercent/);
  assert.match(source, /baselineScopeTransactions/);
  assert.match(source, /candidateDerivedScopeRoundTrips/);
  assert.match(
    source,
    /candidateDerivedMemberStatsAggregationPasses </,
  );
  assert.doesNotMatch(source, /productionComparableTiming == true/);
  assert.doesNotMatch(source, /(?:^|\s)-p\s+[0-9]/m);
  assert.doesNotMatch(source, /--publish/);
  assert.doesNotMatch(source, /docker\.sock/);
  assert.doesNotMatch(source, /\/tmp(?:\/|\s|")/);
  assert.doesNotMatch(source, /fst-postgres/);
  assert.doesNotMatch(source, /docker compose/);
});
