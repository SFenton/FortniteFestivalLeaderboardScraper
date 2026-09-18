import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import {
  chmod,
  mkdtemp,
  readFile,
  rm,
  writeFile
} from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { promisify } from "node:util";
import { describe, it } from "node:test";
import { fileURLToPath } from "node:url";

const execFileAsync = promisify(execFile);
const wrapperPath = fileURLToPath(
  new URL("fst-worker-dual-lane-runonce.sh", import.meta.url)
);

describe("fst-worker dual-lane run-once profiles", () => {
  it("passes acquisition-checkpoint terminalization the accepted baseline environment", async () => {
    const root = await mkdtemp(
      path.join(os.tmpdir(), "fst-worker-dual-lane-runonce-test-")
    );
    const wrapperCopy = path.join(root, "fst-worker-dual-lane-runonce.sh");
    const guardCopy = path.join(root, "fst-worker-compose-guard.sh");
    const capturePath = path.join(root, "capture.json");
    try {
      await writeFile(wrapperCopy, await readFile(wrapperPath));
      await writeFile(
        guardCopy,
        `#!/usr/bin/env bash
set -euo pipefail
python3 - "$FST_DUAL_LANE_CAPTURE" "$@" <<'PY'
import json
import os
import sys

keys = [
    "RUN_ONCE",
    "FST_WORKER_IMAGE",
    "ENABLED_PHASES",
    "PIA_MAX_REQUESTS_PER_SECOND",
    "PIA_PROXY_MAX_REQUESTS_PER_SECOND_PER_ENDPOINT",
    "PIA_PROXY_MAX_CONCURRENT_REQUESTS_PER_ENDPOINT",
    "PIA_INITIAL_DOP",
    "PIA_INITIAL_CDN_LEARNED_MAX_DOP",
    "PIA_DEGREE_OF_PARALLELISM",
    "PIA_PAGE_CONCURRENCY",
    "ENABLE_AUTOMATIC_PATH_GENERATION",
    "REGISTERED_USER_REFRESH_TIMEOUT",
    "REGISTERED_PLAYER_BAND_DISCOVERY_TIMEOUT",
    "REGISTERED_BAND_TARGETED_PROCESSING_TIMEOUT",
    "IMPROVEMENT_NOTIFICATIONS_ENABLED",
    "IMPROVEMENT_NOTIFICATIONS_SCOPE",
    "IMPROVEMENT_NOTIFICATIONS_INCLUDE_PLAYERS",
    "IMPROVEMENT_NOTIFICATIONS_INCLUDE_BANDS",
    "IMPROVEMENT_NOTIFICATIONS_INCLUDE_SONG_EVENTS",
    "IMPROVEMENT_NOTIFICATIONS_INCLUDE_RANKINGS",
    "IMPROVEMENT_NOTIFICATIONS_REFRESH_SOLO_PROJECTION",
    "IMPROVEMENT_NOTIFICATIONS_REFRESH_ALL_SOLO_SCOPES_WHEN_NO_IMPACTED_SCOPES",
    "SKIP_UNCHANGED_PHYSICAL_LEADERBOARD_SNAPSHOTS",
    "USE_LEADERBOARD_SCOPE_FINGERPRINTS",
    "USE_SNAPSHOT_OVERLAY_WORKER_READERS",
    "BAND_CURRENT_PROJECTION_USE_BATCHED_MEMBER_STATS_AGGREGATION",
];
with open(sys.argv[1], "w", encoding="utf-8") as handle:
    json.dump({
        "args": sys.argv[2:],
        "environment": {key: os.environ.get(key) for key in keys},
    }, handle)
PY
`
      );
      await chmod(wrapperCopy, 0o755);
      await chmod(guardCopy, 0o755);

      await execFileAsync(
        wrapperCopy,
        [
          "--network-profile",
          "candidate-800-32-4",
          "--data-profile",
          "acquisition-checkpoint-terminalization",
          "--expected-worker-image",
          "example.invalid/fstworker:test",
          "--config-only"
        ],
        {
          env: {
            ...process.env,
            FST_DUAL_LANE_CAPTURE: capturePath
          }
        }
      );

      const capture = JSON.parse(await readFile(capturePath, "utf8"));
      assert.deepEqual(capture.args, [
        "--compose-dir",
        "/home/sfenton/Docker/FestivalServiceTracker",
        "--throughput-profile",
        "candidate-800-32-4",
        "--data-profile",
        "acquisition-checkpoint-terminalization",
        "--expected-worker-image",
        "example.invalid/fstworker:test",
        "--check-runonce",
        "--config-only"
      ]);
      assert.deepEqual(capture.environment, {
        RUN_ONCE: "true",
        FST_WORKER_IMAGE: "example.invalid/fstworker:test",
        ENABLED_PHASES: "All",
        PIA_MAX_REQUESTS_PER_SECOND: "800",
        PIA_PROXY_MAX_REQUESTS_PER_SECOND_PER_ENDPOINT: "32",
        PIA_PROXY_MAX_CONCURRENT_REQUESTS_PER_ENDPOINT: "4",
        PIA_INITIAL_DOP: "50",
        PIA_INITIAL_CDN_LEARNED_MAX_DOP: "360",
        PIA_DEGREE_OF_PARALLELISM: "200",
        PIA_PAGE_CONCURRENCY: "50",
        ENABLE_AUTOMATIC_PATH_GENERATION: "false",
        REGISTERED_USER_REFRESH_TIMEOUT: "00:00:00",
        REGISTERED_PLAYER_BAND_DISCOVERY_TIMEOUT: "00:06:00",
        REGISTERED_BAND_TARGETED_PROCESSING_TIMEOUT: "00:05:00",
        IMPROVEMENT_NOTIFICATIONS_ENABLED: "true",
        IMPROVEMENT_NOTIFICATIONS_SCOPE: "registered",
        IMPROVEMENT_NOTIFICATIONS_INCLUDE_PLAYERS: "true",
        IMPROVEMENT_NOTIFICATIONS_INCLUDE_BANDS: "true",
        IMPROVEMENT_NOTIFICATIONS_INCLUDE_SONG_EVENTS: "true",
        IMPROVEMENT_NOTIFICATIONS_INCLUDE_RANKINGS: "true",
        IMPROVEMENT_NOTIFICATIONS_REFRESH_SOLO_PROJECTION: "true",
        IMPROVEMENT_NOTIFICATIONS_REFRESH_ALL_SOLO_SCOPES_WHEN_NO_IMPACTED_SCOPES:
          "false",
        SKIP_UNCHANGED_PHYSICAL_LEADERBOARD_SNAPSHOTS: "false",
        USE_LEADERBOARD_SCOPE_FINGERPRINTS: "true",
        USE_SNAPSHOT_OVERLAY_WORKER_READERS: "false",
        BAND_CURRENT_PROJECTION_USE_BATCHED_MEMBER_STATS_AGGREGATION: "false"
      });
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  });

  it("passes band-maintenance-progress with the optional exact default omitted", async () => {
    const root = await mkdtemp(
      path.join(os.tmpdir(), "fst-worker-dual-lane-runonce-band-test-")
    );
    const wrapperCopy = path.join(root, "fst-worker-dual-lane-runonce.sh");
    const guardCopy = path.join(root, "fst-worker-compose-guard.sh");
    const capturePath = path.join(root, "capture.json");
    try {
      await writeFile(wrapperCopy, await readFile(wrapperPath));
      await writeFile(
        guardCopy,
        `#!/usr/bin/env bash
set -euo pipefail
python3 - "$FST_DUAL_LANE_CAPTURE" "$@" <<'PY'
import json
import os
import sys

with open(sys.argv[1], "w", encoding="utf-8") as handle:
    json.dump({
        "args": sys.argv[2:],
        "bandAggregation": os.environ.get(
            "BAND_CURRENT_PROJECTION_USE_BATCHED_MEMBER_STATS_AGGREGATION")
    }, handle)
PY
`
      );
      await chmod(wrapperCopy, 0o755);
      await chmod(guardCopy, 0o755);

      await execFileAsync(
        wrapperCopy,
        [
          "--network-profile",
          "candidate-800-32-4",
          "--data-profile",
          "band-maintenance-progress",
          "--expected-worker-image",
          "example.invalid/fstworker:band-maintenance-progress",
          "--expected-worker-image-id",
          "sha256:" + "a".repeat(64),
          "--expected-worker-revision",
          "3".repeat(40),
          "--expected-worker-config-sha256",
          "4".repeat(64),
          "--config-only"
        ],
        {
          env: {
            ...process.env,
            FST_DUAL_LANE_CAPTURE: capturePath
          }
        }
      );

      const capture = JSON.parse(await readFile(capturePath, "utf8"));
      assert.deepEqual(capture.args, [
        "--compose-dir",
        "/home/sfenton/Docker/FestivalServiceTracker",
        "--throughput-profile",
        "candidate-800-32-4",
        "--data-profile",
        "band-maintenance-progress",
        "--expected-worker-image",
        "example.invalid/fstworker:band-maintenance-progress",
        "--check-runonce",
        "--expected-worker-image-id",
        "sha256:" + "a".repeat(64),
        "--expected-worker-revision",
        "3".repeat(40),
        "--expected-worker-config-sha256",
        "4".repeat(64),
        "--config-only"
      ]);
      assert.equal(capture.bandAggregation, null);
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  });

  it("propagates explicit Band aggregation drift to the guard", async () => {
    const root = await mkdtemp(
      path.join(os.tmpdir(), "fst-worker-dual-lane-runonce-band-drift-test-")
    );
    const wrapperCopy = path.join(root, "fst-worker-dual-lane-runonce.sh");
    const guardCopy = path.join(root, "fst-worker-compose-guard.sh");
    try {
      await writeFile(wrapperCopy, await readFile(wrapperPath));
      await writeFile(
        guardCopy,
        `#!/usr/bin/env bash
set -euo pipefail
if [[ "\${BAND_CURRENT_PROJECTION_USE_BATCHED_MEMBER_STATS_AGGREGATION:-}" == "true" ]]; then
  printf 'ERROR: data profile band-maintenance-progress requires Scraper__BandCurrentProjectionUseBatchedMemberStatsAggregation=false\\n' >&2
  exit 64
fi
exit 99
`
      );
      await chmod(wrapperCopy, 0o755);
      await chmod(guardCopy, 0o755);

      await assert.rejects(
        execFileAsync(
          wrapperCopy,
          [
            "--network-profile",
            "candidate-800-32-4",
            "--data-profile",
            "band-maintenance-progress",
            "--expected-worker-image",
            "example.invalid/fstworker:band-maintenance-progress",
            "--expected-worker-image-id",
            "sha256:" + "a".repeat(64),
            "--expected-worker-revision",
            "3".repeat(40),
            "--config-only"
          ],
          {
            env: {
              ...process.env,
              BAND_CURRENT_PROJECTION_USE_BATCHED_MEMBER_STATS_AGGREGATION:
                "true"
            }
          }
        ),
        (error) => {
          assert.equal(error.code, 64);
          assert.match(
            error.stderr,
            /requires Scraper__BandCurrentProjectionUseBatchedMemberStatsAggregation=false/
          );
          return true;
        }
      );
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  });

  it("rejects every alternate network profile for band-maintenance-progress", async () => {
    const root = await mkdtemp(
      path.join(os.tmpdir(), "fst-worker-dual-lane-runonce-band-reject-test-")
    );
    const wrapperCopy = path.join(root, "fst-worker-dual-lane-runonce.sh");
    const guardCopy = path.join(root, "fst-worker-compose-guard.sh");
    const capturePath = path.join(root, "capture.json");
    try {
      await writeFile(wrapperCopy, await readFile(wrapperPath));
      await writeFile(
        guardCopy,
        `#!/usr/bin/env bash
set -euo pipefail
printf 'unexpected guard invocation\n' >&2
exit 99
`
      );
      await chmod(wrapperCopy, 0o755);
      await chmod(guardCopy, 0o755);

      for (const networkProfile of [
        "candidate-1600-64-8",
        "candidate-1800-72-9",
        "candidate-2000-80-10",
        "candidate-2880-128-16"
      ]) {
        await assert.rejects(
          execFileAsync(
            wrapperCopy,
            [
              "--network-profile",
              networkProfile,
              "--data-profile",
              "band-maintenance-progress",
              "--expected-worker-image",
              "example.invalid/fstworker:band-telemetry",
              "--config-only"
            ],
            {
              env: {
                ...process.env,
                FST_DUAL_LANE_CAPTURE: capturePath
              }
            }
          ),
          (error) => {
            assert.equal(error.code, 64);
            assert.match(
              error.stderr,
              /requires network profile candidate-800-32-4/
            );
            return true;
          }
        );
      }
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  });
});
