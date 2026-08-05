import assert from "node:assert/strict";
import { execFile, spawn } from "node:child_process";
import { once } from "node:events";
import {
  chmod,
  mkdir,
  readFile,
  readdir,
  rm,
  unlink,
  writeFile
} from "node:fs/promises";
import net from "node:net";
import { describe, it } from "node:test";
import { promisify } from "node:util";
import { fileURLToPath } from "node:url";

const repoRoot = new URL("../", import.meta.url);
const execFileAsync = promisify(execFile);

async function read(relativePath) {
  return readFile(new URL(relativePath, repoRoot), "utf8");
}

describe("stored-rank filtered-read rollout package", () => {
  it("keeps service and worker roles split in both overlays", async () => {
    const [enabled, disabled, recovery] = await Promise.all([
      read("deploy/rollout/stored-rank-filtered-reads/compose.true.yml"),
      read("deploy/rollout/stored-rank-filtered-reads/compose.false.yml"),
      read("deploy/rollout/stored-rank-filtered-reads/compose.recovery.yml")
    ]);

    assert.match(
      enabled,
      /fstservice:[\s\S]*Features__UseStoredSoloProjectionRanksForFilteredReads: "true"/
    );
    assert.match(
      enabled,
      /fstservice:[\s\S]*image: \$\{FST_STORED_RANK_SERVICE_IMAGE:\?/
    );
    assert.match(
      enabled,
      /fstworker:[\s\S]*Features__UseStoredSoloProjectionRanksForFilteredReads: "false"/
    );
    assert.match(
      enabled,
      /fstservice:[\s\S]*Scraper__RolloutReadOnlyStartup: "true"/
    );
    assert.match(
      enabled,
      /fstworker:[\s\S]*Scraper__RolloutReadOnlyStartup: "false"/
    );
    assert.match(
      enabled,
      /fstservice:[\s\S]*Scraper__RolloutPostgresReadOnly: "true"/
    );
    assert.match(
      disabled,
      /fstservice:[\s\S]*Features__UseStoredSoloProjectionRanksForFilteredReads: "false"/
    );
    assert.match(
      disabled,
      /fstservice:[\s\S]*image: \$\{FST_STORED_RANK_SERVICE_IMAGE:\?/
    );
    assert.match(
      disabled,
      /fstworker:[\s\S]*Features__UseStoredSoloProjectionRanksForFilteredReads: "false"/
    );
    assert.match(
      disabled,
      /fstservice:[\s\S]*Scraper__RolloutReadOnlyStartup: "true"/
    );
    assert.match(
      disabled,
      /fstworker:[\s\S]*Scraper__RolloutReadOnlyStartup: "false"/
    );
    assert.match(
      disabled,
      /fstservice:[\s\S]*Scraper__RolloutPostgresReadOnly: "true"/
    );
    assert.match(
      recovery,
      /fstservice:[\s\S]*Features__UseStoredSoloProjectionRanksForFilteredReads: "false"/
    );
    assert.match(
      recovery,
      /fstservice:[\s\S]*Scraper__RolloutReadOnlyStartup: "false"/
    );
    assert.match(
      recovery,
      /fstworker:[\s\S]*Scraper__RolloutReadOnlyStartup: "false"/
    );
    assert.match(
      recovery,
      /fstservice:[\s\S]*Scraper__RolloutPostgresReadOnly: "false"/
    );
  });

  it("leaves every tracked default disabled", async () => {
    const [rootCompose, deployCompose, settings, developmentSettings] =
      await Promise.all([
        read("docker-compose.yml"),
        read("deploy/docker-compose.yml"),
        read("FSTService/appsettings.json"),
        read("FSTService/appsettings.Development.json")
      ]);

    for (const compose of [rootCompose, deployCompose]) {
      assert.match(
        compose,
        /USE_STORED_SOLO_PROJECTION_RANKS_FOR_FILTERED_READS:-false/
      );
    }
    for (const settingsText of [settings, developmentSettings]) {
      const settings = JSON.parse(settingsText);
      assert.equal(
        settings.Features.UseStoredSoloProjectionRanksForFilteredReads,
        false
      );
    }
  });

  it("recreates only fstservice and forces 4 TB evidence output", async () => {
    const script = await read("tools/postgres-stored-rank-rollout.sh");

    assert.match(script, /--no-deps --force-recreate --pull never fstservice/);
    assert.doesNotMatch(script, /up[^\n]*fstworker/);
    assert.match(
      script,
      /\/mnt\/docker-storage\/Docker\/FestivalServiceTracker\/fst-data\/evidence/
    );
    assert.doesNotMatch(script, /mktemp|\/tmp|\/var\/tmp/);
    assert.match(
      script,
      /verify_container_env[\s\\\n]+fstworker[\s\\\n]+Features__UseStoredSoloProjectionRanksForFilteredReads[\s\\\n]+false/
    );
    assert.match(script, /stats[\s\\\n]+--no-stream/);
    assert.match(script, /df -P "\$FST_STORED_RANK_EVIDENCE_ROOT"/);
    assert.match(script, /runtime-preflight/);
    assert.match(script, /role-verification/);
    assert.match(script, /run_tool guard/);
    assert.match(script, /--warm-request-starts-per-second/);
    assert.match(script, /WARM_REQUEST_STARTS_PER_SECOND:-80/);
    assert.match(script, /--connect-timeout/);
    assert.match(script, /--max-time/);
    assert.match(script, /PUBLIC_PATH_TOTAL_TIMEOUT_SECONDS:-180/);
    assert.match(script, /trap rollback_on_exit EXIT/);
    assert.doesNotMatch(script, /rollback_service\s*\|\|\s*true/);
    assert.match(script, /run_docker_query_bounded/);
    assert.match(script, /run_docker_recreate_bounded/);
    assert.match(script, /timeout[\s\\\n]+--kill-after=2s/);
    assert.match(script, /EXPECTED_FSTSERVICE_IMAGE/);
    assert.match(script, /EXPECTED_FST_EVIDENCE_DEVICE/);
    assert.match(script, /acquire_rollout_lock/);
    assert.match(script, /analysis-provisional\.json/);
    assert.match(script, /finalize-acceptance/);
    assert.match(script, /RECOVERY_OVERLAY/);
    assert.match(script, /--recovery-evidence "\$LAST_RECOVERY_VERIFICATION_PATH"/);
    assert.match(script, /verify_final_recovery_state/);
    assert.match(script, /capture_final_acceptance_snapshot/);
    assert.match(script, /--final-evidence "\$LAST_FINAL_VERIFICATION_PATH"/);
    assert.match(script, /--final-quiescence-sha256/);
    assert.match(script, /verify_worker_pin/);
    assert.match(script, /resolve_database_target_binding/);
    assert.match(script, /verify_database_target_binding/);
    assert.match(script, /run_tool db-attest/);
    assert.match(
      script,
      /attest_database_target "before-api-\$sequence-\$variant"[\s\S]*?run_tool api-capture/
    );
    assert.match(
      script,
      /attest_database_target "before-benchmark-\$sequence-\$variant"[\s\S]*?run_tool benchmark-block/
    );
    assert.match(
      script,
      /capture_final_acceptance_snapshot[\s\S]*?run_tool finalize-acceptance/
    );
    assert.match(script, /--postgres-network-names/);
    assert.match(script, /--postgres-server-addresses/);
    assert.match(script, /--postgres-network-bindings-json/);
    assert.match(script, /network inspect/);
    assert.match(script, /DNSNames/);
    assert.match(script, /normalized_names/);
    assert.match(script, /alias is not exclusive/);
    assert.match(script, /postgresConnectionTarget/);
    assert.match(script, /resolve_service_traffic_binding/);
    assert.match(script, /\$BASE_URL\/api\/service-info/);
    assert.match(script, /serviceInstance/);
    assert.match(script, /workerContainerId/);
    assert.match(script, /capture_db_quiescence/);
    assert.match(script, /quiescence-before-acceptance\.json/);
    assert.match(script, /sha256sum/);
    const quiescenceFunction = script.match(
      /capture_db_quiescence\(\) \{([\s\S]*?)\n\}/
    )?.[1] ?? "";
    assert.doesNotMatch(quiescenceFunction, /set [+-]e/);
    const finalSnapshotFunction = script.match(
      /capture_final_acceptance_snapshot\(\) \{([\s\S]*?)\n\}/
    )?.[1] ?? "";
    assert.match(finalSnapshotFunction, /verify_final_recovery_state/);
    assert.match(finalSnapshotFunction, /PINNED_RECOVERY_SERVICE_ID/);
    assert.match(
      finalSnapshotFunction,
      /record_role_verification final "\$PINNED_RECOVERY_SERVICE_ID"/
    );
    const recoveryFunction = script.match(
      /recover_normal_service\(\) \{([\s\S]*?)\n\}/
    )?.[1] ?? "";
    assert.doesNotMatch(recoveryFunction, /mutated_service=0/);
    const recoveryEvidenceFunction = script.match(
      /complete_normal_recovery_evidence\(\) \{([\s\S]*?)\n\}/
    )?.[1] ?? "";
    assert.match(recoveryEvidenceFunction, /capture_db_quiescence[\s\S]*pinned_service_id/);
    assert.match(recoveryEvidenceFunction, /persist_role_evidence recovery "\$pinned_service_id"/);
    assert.match(recoveryEvidenceFunction, /mutated_service=0/);
    const waitFunction = script.match(
      /wait_public_path\(\) \{([\s\S]*?)\n\}/
    )?.[1] ?? "";
    assert.match(waitFunction, /pinned_container_id/);
    assert.match(
      waitFunction,
      /resolve_service_traffic_binding "\$pinned_container_id"/
    );
    assert.doesNotMatch(
      waitFunction,
      /resolve_service_traffic_binding\s*(?:\|\||;|\n)/
    );
    const rollbackCase = script.match(
      /^    rollback\)\n([\s\S]*?)^        ;;/m
    )?.[1] ?? "";
    assert.match(rollbackCase, /run_standalone_rollback/);
    assert.doesNotMatch(rollbackCase, /resolve_pinned_service_image/);
    const standaloneRollback = script.match(
      /run_standalone_rollback\(\) \{([\s\S]*?)\n\}/
    )?.[1] ?? "";
    assert.match(standaloneRollback, /require_evidence_configuration true false/);
    assert.match(standaloneRollback, /load_rollout_manifest_bindings/);
    assert.match(standaloneRollback, /validate_database_target_in_memory/);
    assert.ok(
      standaloneRollback.indexOf("trap rollback_on_exit EXIT") <
        standaloneRollback.indexOf("acquire_rollout_lock"),
      "standalone recovery trap must precede evidence lock acquisition"
    );
    assert.match(
      script,
      /require_evidence_configuration\(\)[\s\S]*?verify_evidence_mount[\s\S]*?mkdir -p "\$EVIDENCE_DIR"/
    );
    assert.match(script, /verify_container_image fstservice/);
    assert.match(script, /--service-image "\$FST_STORED_RANK_SERVICE_IMAGE"/);
    assert.match(
      script,
      /recreate_service_variant\(\)[\s\S]*mutated_service=1[\s\S]*run_docker_recreate_bounded compose/
    );
  });

  it("bounds a hanging health endpoint and invokes automatic rollback", async () => {
    const sockets = new Set();
    const server = net.createServer((socket) => {
      sockets.add(socket);
      socket.on("close", () => sockets.delete(socket));
    });
    await new Promise((resolve, reject) => {
      server.once("error", reject);
      server.listen(0, "127.0.0.1", resolve);
    });

    const address = server.address();
    assert.equal(typeof address, "object");
    const baseUrl = `http://127.0.0.1:${address.port}`;
    const markerUrl = new URL(
      `stored-rank-rollout-rollback-${process.pid}.marker`,
      import.meta.url
    );
    const markerPath = fileURLToPath(markerUrl);
    const scriptPath = fileURLToPath(
      new URL("postgres-stored-rank-rollout.sh", import.meta.url)
    );
    const started = Date.now();
    let failure;
    try {
      await execFileAsync(scriptPath, ["test-wait-public-path"], {
        cwd: fileURLToPath(repoRoot),
        env: {
          ...process.env,
          BASE_URL: baseUrl,
          WEB_BASE_URL: baseUrl,
          PUBLIC_PATH_CONNECT_TIMEOUT_SECONDS: "1",
          PUBLIC_PATH_MAX_TIME_SECONDS: "1",
          PUBLIC_PATH_TOTAL_TIMEOUT_SECONDS: "2",
          PUBLIC_PATH_RETRY_DELAY_SECONDS: "0",
          ROLLOUT_TEST_ROLLBACK_MARKER: markerPath
        },
        timeout: 8_000,
        encoding: "utf8"
      });
    } catch (error) {
      failure = error;
    } finally {
      for (const socket of sockets) socket.destroy();
      await new Promise((resolve) => server.close(resolve));
    }

    try {
      assert.ok(failure, "hanging endpoint must fail");
      assert.ok(Date.now() - started < 7_000, "health wait exceeded its bound");
      assert.equal(await readFile(markerPath, "utf8"), "rollback\n");
      assert.match(failure.stderr, /did not become healthy within 2s/);
      assert.match(
        failure.stderr,
        /Restoring fstservice through false rollback and normal recovery/
      );
    } finally {
      await unlink(markerPath).catch(() => {});
    }
  });

  it("arms rollback before a partial candidate recreate", async () => {
    const markerPath = fileURLToPath(
      new URL(`stored-rank-partial-${process.pid}.marker`, import.meta.url)
    );
    const logPath = fileURLToPath(
      new URL(`stored-rank-partial-${process.pid}.log`, import.meta.url)
    );
    const scriptPath = fileURLToPath(
      new URL("postgres-stored-rank-rollout.sh", import.meta.url)
    );
    let failure;
    try {
      await execFileAsync(scriptPath, ["test-partial-recreate"], {
        cwd: fileURLToPath(repoRoot),
        env: {
          ...process.env,
          ROLLOUT_TEST_ROLLBACK_MARKER: markerPath,
          ROLLOUT_TEST_EVENT_LOG: logPath
        },
        encoding: "utf8"
      });
    } catch (error) {
      failure = error;
    }

    try {
      assert.ok(failure, "partial recreate must fail");
      assert.equal(failure.code, 42);
      assert.equal(await readFile(markerPath, "utf8"), "rollback\n");
      assert.deepEqual(
        (await readFile(logPath, "utf8")).trim().split("\n"),
        ["validate", "compose-up:marker=1", "rollback:marker=1"]
      );
    } finally {
      await unlink(markerPath).catch(() => {});
      await unlink(logPath).catch(() => {});
    }
  });

  it("times out a hanging docker command and returns to rollback", async () => {
    const fakeDirectory = fileURLToPath(
      new URL(`stored-rank-fake-docker-${process.pid}/`, import.meta.url)
    );
    const fakeDocker = `${fakeDirectory}/docker`;
    const markerPath = fileURLToPath(
      new URL(`stored-rank-docker-timeout-${process.pid}.marker`, import.meta.url)
    );
    const logPath = fileURLToPath(
      new URL(`stored-rank-docker-timeout-${process.pid}.log`, import.meta.url)
    );
    const scriptPath = fileURLToPath(
      new URL("postgres-stored-rank-rollout.sh", import.meta.url)
    );
    await mkdir(fakeDirectory);
    await writeFile(
      fakeDocker,
      [
        "#!/usr/bin/env bash",
        "printf 'docker-start\\n' >> \"$ROLLOUT_TEST_EVENT_LOG\"",
        "sleep 30"
      ].join("\n") + "\n"
    );
    await chmod(fakeDocker, 0o755);

    const started = Date.now();
    let failure;
    try {
      await execFileAsync(scriptPath, ["test-hanging-docker-recreate"], {
        cwd: fileURLToPath(repoRoot),
        env: {
          ...process.env,
          PATH: `${fakeDirectory}:${process.env.PATH}`,
          DOCKER_QUERY_TIMEOUT_SECONDS: "1",
          DOCKER_RECREATE_TIMEOUT_SECONDS: "1",
          ROLLOUT_TEST_ROLLBACK_MARKER: markerPath,
          ROLLOUT_TEST_EVENT_LOG: logPath
        },
        timeout: 8_000,
        encoding: "utf8"
      });
    } catch (error) {
      failure = error;
    }

    try {
      assert.ok(failure, "hanging docker command must fail");
      assert.equal(failure.code, 124);
      assert.ok(Date.now() - started < 7_000, "docker timeout exceeded its bound");
      assert.equal(await readFile(markerPath, "utf8"), "rollback\n");
      assert.deepEqual(
        (await readFile(logPath, "utf8")).trim().split("\n"),
        ["validate", "docker-start", "rollback:marker=1"]
      );
    } finally {
      await unlink(markerPath).catch(() => {});
      await unlink(logPath).catch(() => {});
      await rm(fakeDirectory, { recursive: true, force: true });
    }
  });

  it("fails closed when a rollback verification step fails", async () => {
    const logPath = fileURLToPath(
      new URL(`stored-rank-rollback-failure-${process.pid}.log`, import.meta.url)
    );
    const scriptPath = fileURLToPath(
      new URL("postgres-stored-rank-rollout.sh", import.meta.url)
    );
    let failure;
    try {
      await execFileAsync(scriptPath, ["test-rollback-step-failure"], {
        cwd: fileURLToPath(repoRoot),
        env: {
          ...process.env,
          ROLLOUT_TEST_EVENT_LOG: logPath
        },
        encoding: "utf8"
      });
    } catch (error) {
      failure = error;
    }

    try {
      assert.ok(failure, "rollback verification failure must be nonzero");
      assert.notEqual(failure.code, 0);
      const events = (await readFile(logPath, "utf8")).trim().split("\n");
      assert.equal(
        events.filter((event) => event === "false-recreate").length,
        2
      );
      assert.equal(events.at(-1), "marker=1");
      assert.doesNotMatch(events.join("\n"), /evidence/);
      assert.match(failure.stderr, /service recovered normally/);
    } finally {
      await unlink(logPath).catch(() => {});
    }
  });

  it("resolves and verifies one immutable service image pin", async () => {
    const logPath = fileURLToPath(
      new URL(`stored-rank-image-pin-${process.pid}.log`, import.meta.url)
    );
    const scriptPath = fileURLToPath(
      new URL("postgres-stored-rank-rollout.sh", import.meta.url)
    );
    const reference =
      "ghcr.io/sfenton/fstservice:reviewed@sha256:" + "a".repeat(64);
    const imageId = "sha256:" + "b".repeat(64);

    await execFileAsync(scriptPath, ["test-image-pin-resolution"], {
      cwd: fileURLToPath(repoRoot),
      env: {
        ...process.env,
        EXPECTED_FSTSERVICE_IMAGE: reference,
        ROLLOUT_TEST_EXPECTED_IMAGE_ID: imageId,
        ROLLOUT_TEST_EVENT_LOG: logPath
      },
      encoding: "utf8"
    });

    try {
      assert.deepEqual(
        (await readFile(logPath, "utf8")).trim().split("\n"),
        [`reference=${reference}`, `id=${imageId}`]
      );
    } finally {
      await unlink(logPath).catch(() => {});
    }
  });

  it("rejects mutable and mismatched service image pins", async () => {
    const scriptPath = fileURLToPath(
      new URL("postgres-stored-rank-rollout.sh", import.meta.url)
    );
    const mutableLog = fileURLToPath(
      new URL(`stored-rank-image-mutable-${process.pid}.log`, import.meta.url)
    );
    const mismatchLog = fileURLToPath(
      new URL(`stored-rank-image-mismatch-${process.pid}.log`, import.meta.url)
    );
    const reference =
      "ghcr.io/sfenton/fstservice:reviewed@sha256:" + "a".repeat(64);
    const expectedId = "sha256:" + "b".repeat(64);
    const runningId = "sha256:" + "c".repeat(64);

    let mutableFailure;
    try {
      await execFileAsync(scriptPath, ["test-image-pin-resolution"], {
        cwd: fileURLToPath(repoRoot),
        env: {
          ...process.env,
          EXPECTED_FSTSERVICE_IMAGE: "ghcr.io/sfenton/fstservice:latest",
          ROLLOUT_TEST_EVENT_LOG: mutableLog
        },
        encoding: "utf8"
      });
    } catch (error) {
      mutableFailure = error;
    }

    let mismatchFailure;
    try {
      await execFileAsync(scriptPath, ["test-image-pin-resolution"], {
        cwd: fileURLToPath(repoRoot),
        env: {
          ...process.env,
          EXPECTED_FSTSERVICE_IMAGE: reference,
          ROLLOUT_TEST_EXPECTED_IMAGE_ID: expectedId,
          ROLLOUT_TEST_RUNNING_IMAGE_ID: runningId,
          ROLLOUT_TEST_EVENT_LOG: mismatchLog
        },
        encoding: "utf8"
      });
    } catch (error) {
      mismatchFailure = error;
    }

    try {
      assert.ok(mutableFailure);
      assert.notEqual(mutableFailure.code, 0);
      assert.match(mutableFailure.stderr, /must be immutable tag@sha256/);
      assert.ok(mismatchFailure);
      assert.notEqual(mismatchFailure.code, 0);
      assert.match(mismatchFailure.stderr, /does not match reviewed digest/);
    } finally {
      await unlink(mutableLog).catch(() => {});
      await unlink(mismatchLog).catch(() => {});
    }
  });

  it("binds the service target to one exclusively owned Postgres network alias", async () => {
    const scriptPath = fileURLToPath(
      new URL("postgres-stored-rank-rollout.sh", import.meta.url)
    );
    const logPath = fileURLToPath(
      new URL(`stored-rank-database-binding-${process.pid}.log`, import.meta.url)
    );
    const run = (mode) => execFileAsync(
      scriptPath,
      ["test-database-target-binding"],
      {
        cwd: fileURLToPath(repoRoot),
        env: {
          ...process.env,
          ROLLOUT_TEST_EVENT_LOG: logPath,
          ROLLOUT_TEST_DATABASE_BINDING_MODE: mode
        },
        encoding: "utf8"
      }
    );

    try {
      await run("valid");
      assert.deepEqual(
        (await readFile(logPath, "utf8")).trim().split("\n"),
        [
          "postgres|5432|fstservice|fst",
          "production-postgres|fst-postgres:17-repack|" +
            "sha256:" + "d".repeat(64),
          "fst-network|fst-postgres,postgres,production-postgres|172.20.0.2",
          '[{"exclusiveOwnerContainerId":"production-postgres",' +
            '"networkId":"network-id","networkName":"fst-network",' +
            '"serverAddresses":["172.20.0.2"],"serviceAlias":"postgres"}]',
          "verify=0"
        ]
      );

      for (const mode of [
        "container-mismatch",
        "container-drift",
        "alias-drift",
        "duplicate-alias",
        "dns-name-clone",
        "container-name-clone",
        "network-id-drift"
      ]) {
        await assert.rejects(run(mode));
      }
    } finally {
      await unlink(logPath).catch(() => {});
    }
  });

  it("requires exact 200 and valid service-info JSON", async () => {
    let mode = "valid";
    const server = net.createServer((socket) => {
      socket.once("data", (data) => {
        const path = data.toString().split(" ")[1];
        if (mode === "redirect" && path === "/readyz") {
          socket.end("HTTP/1.1 302 Found\r\nLocation: /\r\nContent-Length: 0\r\n\r\n");
          return;
        }
        if (path === "/api/service-info") {
          const body = mode === "html"
            ? "<html>not json</html>"
            : JSON.stringify({
                publishedScrapeId: 1278,
                activeScrapeId: null,
                publication: {
                  publishedScrapeId: 1278,
                  publicReadsFrozen: false
                },
                currentUpdate: { status: "idle" },
                rolloutReadOnlyStartup: false,
                postgresDefaultTransactionReadOnly: false,
                postgresConnectionTarget: {
                  host: mode === "wrong-target" ? "clone-postgres" : "postgres",
                  port: 5432,
                  database: "fstservice",
                  username: "fst",
                  defaultTransactionReadOnlyOption: false
                },
                serviceInstance: {
                  nonce: "a".repeat(32),
                  hostName: "test-service-host",
                  processId: 123,
                  startedAtUtc: "2026-08-05T00:00:00Z"
                },
                readOnlyViolationDetected: false,
                workerStatus: {
                  workerKey: "scraper",
                  status: "stale",
                  currentOperation: null
                }
              });
          socket.end(
            `HTTP/1.1 200 OK\r\nContent-Length: ${Buffer.byteLength(body)}\r\n\r\n${body}`
          );
          return;
        }
        const body = path === "/" ? "<html>ok</html>" : "ok";
        socket.end(
          `HTTP/1.1 200 OK\r\nContent-Length: ${Buffer.byteLength(body)}\r\n\r\n${body}`
        );
      });
    });
    await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
    const address = server.address();
    const baseUrl = `http://127.0.0.1:${address.port}`;
    const scriptPath = fileURLToPath(
      new URL("postgres-stored-rank-rollout.sh", import.meta.url)
    );
    const run = async () => execFileAsync(scriptPath, ["test-health-check"], {
      cwd: fileURLToPath(repoRoot),
      env: {
        ...process.env,
        BASE_URL: baseUrl,
        WEB_BASE_URL: baseUrl,
        EXPECTED_PUBLISHED_SCRAPE_ID: "1278",
        PUBLIC_PATH_CONNECT_TIMEOUT_SECONDS: "1",
        PUBLIC_PATH_MAX_TIME_SECONDS: "1",
        PUBLIC_PATH_TOTAL_TIMEOUT_SECONDS: "1",
        PUBLIC_PATH_RETRY_DELAY_SECONDS: "0"
      },
      encoding: "utf8"
    });

    try {
      await run();
      mode = "redirect";
      await assert.rejects(run());
      mode = "html";
      await assert.rejects(run());
      mode = "wrong-target";
      await assert.rejects(run());
    } finally {
      await new Promise((resolve) => server.close(resolve));
    }
  });

  it("rejects a stale BASE_URL responder even when WEB_BASE_URL is current", async () => {
    const currentNonce = "a".repeat(32);
    const serviceInfo = (nonce) => JSON.stringify({
      publishedScrapeId: 1278,
      activeScrapeId: null,
      publication: {
        publishedScrapeId: 1278,
        publicReadsFrozen: false
      },
      currentUpdate: { status: "idle" },
      rolloutReadOnlyStartup: false,
      postgresDefaultTransactionReadOnly: false,
      postgresConnectionTarget: {
        host: "postgres",
        port: 5432,
        database: "fstservice",
        username: "fst",
        defaultTransactionReadOnlyOption: false
      },
      serviceInstance: {
        nonce,
        hostName: "test-service-host",
        processId: 123,
        startedAtUtc: "2026-08-05T00:00:00Z"
      },
      readOnlyViolationDetected: false,
      workerStatus: {
        workerKey: "scraper",
        status: "stale",
        currentOperation: null
      }
    });
    const createServer = (nonce) => net.createServer((socket) => {
      socket.once("data", (data) => {
        const path = data.toString().split(" ")[1];
        const body = path === "/api/service-info"
          ? serviceInfo(nonce)
          : "ok";
        socket.end(
          `HTTP/1.1 200 OK\r\nContent-Length: ${Buffer.byteLength(body)}\r\n\r\n${body}`
        );
      });
    });
    const staleServer = createServer("b".repeat(32));
    const currentServer = createServer(currentNonce);
    await Promise.all([
      new Promise((resolve) => staleServer.listen(0, "127.0.0.1", resolve)),
      new Promise((resolve) => currentServer.listen(0, "127.0.0.1", resolve))
    ]);
    const staleAddress = staleServer.address();
    const currentAddress = currentServer.address();
    const scriptPath = fileURLToPath(
      new URL("postgres-stored-rank-rollout.sh", import.meta.url)
    );

    try {
      await assert.rejects(
        execFileAsync(scriptPath, ["test-health-check"], {
          cwd: fileURLToPath(repoRoot),
          env: {
            ...process.env,
            BASE_URL: `http://127.0.0.1:${staleAddress.port}`,
            WEB_BASE_URL: `http://127.0.0.1:${currentAddress.port}`,
            EXPECTED_PUBLISHED_SCRAPE_ID: "1278",
            ROLLOUT_TEST_SERVICE_NONCE: currentNonce,
            PUBLIC_PATH_CONNECT_TIMEOUT_SECONDS: "1",
            PUBLIC_PATH_MAX_TIME_SECONDS: "1",
            PUBLIC_PATH_TOTAL_TIMEOUT_SECONDS: "1",
            PUBLIC_PATH_RETRY_DELAY_SECONDS: "0"
          },
          encoding: "utf8"
        }),
        /service instance nonce mismatch|did not become healthy/
      );
    } finally {
      await Promise.all([
        new Promise((resolve) => staleServer.close(resolve)),
        new Promise((resolve) => currentServer.close(resolve))
      ]);
    }
  });

  it("derives BASE_URL from the exact fstservice host port binding", async () => {
    const logPath = fileURLToPath(
      new URL(`stored-rank-service-binding-${process.pid}.log`, import.meta.url)
    );
    const scriptPath = fileURLToPath(
      new URL("postgres-stored-rank-rollout.sh", import.meta.url)
    );
    const run = (baseUrl) => execFileAsync(
      scriptPath,
      ["test-service-traffic-binding"],
      {
        cwd: fileURLToPath(repoRoot),
        env: {
          ...process.env,
          BASE_URL: baseUrl,
          ROLLOUT_TEST_EVENT_LOG: logPath
        },
        encoding: "utf8"
      }
    );

    try {
      await run("http://127.0.0.1:18081");
      assert.equal(
        await readFile(logPath, "utf8"),
        "service-container-id|service-hostname|http://127.0.0.1:18081\n"
      );
      await assert.rejects(
        run("http://127.0.0.1:18082"),
        /supplied BASE_URL does not match inspected fstservice endpoint/
      );
    } finally {
      await unlink(logPath).catch(() => {});
    }
  });

  it("binds the expected mounted evidence device and filesystem", async () => {
    const logPath = fileURLToPath(
      new URL(`stored-rank-mount-${process.pid}.log`, import.meta.url)
    );
    const scriptPath = fileURLToPath(
      new URL("postgres-stored-rank-rollout.sh", import.meta.url)
    );
    const baseEnv = {
      ...process.env,
      EXPECTED_FST_EVIDENCE_DEVICE: "/dev/test-fst",
      EXPECTED_FST_EVIDENCE_FSTYPE: "ext4",
      ROLLOUT_TEST_EVENT_LOG: logPath
    };
    await execFileAsync(scriptPath, ["test-mount-binding"], {
      cwd: fileURLToPath(repoRoot),
      env: baseEnv,
      encoding: "utf8"
    });
    try {
      assert.equal(
        await readFile(logPath, "utf8"),
        "/mnt/docker-storage|/dev/test-fst|ext4\n"
      );
      await assert.rejects(execFileAsync(scriptPath, ["test-mount-binding"], {
        cwd: fileURLToPath(repoRoot),
        env: {
          ...baseEnv,
          ROLLOUT_TEST_MOUNT_SOURCE: "/dev/wrong"
        },
        encoding: "utf8"
      }));
    } finally {
      await unlink(logPath).catch(() => {});
    }
  });

  it("rejects a concurrent global rollout lock holder", async () => {
    const lockPath = fileURLToPath(
      new URL(`stored-rank-global-${process.pid}.lock`, import.meta.url)
    );
    const scriptPath = fileURLToPath(
      new URL("postgres-stored-rank-rollout.sh", import.meta.url)
    );
    const holder = spawn(scriptPath, ["test-hold-lock"], {
      cwd: fileURLToPath(repoRoot),
      env: {
        ...process.env,
        ROLLOUT_TEST_LOCK_FILE: lockPath,
        ROLLOUT_TEST_LOCK_HOLD_SECONDS: "2"
      },
      stdio: ["ignore", "pipe", "pipe"]
    });
    await new Promise((resolve) => holder.stdout.once("data", resolve));
    let failure;
    try {
      await execFileAsync(scriptPath, ["test-try-lock"], {
        cwd: fileURLToPath(repoRoot),
        env: {
          ...process.env,
          ROLLOUT_TEST_LOCK_FILE: lockPath
        },
        encoding: "utf8"
      });
    } catch (error) {
      failure = error;
    }
    await once(holder, "exit");
    try {
      assert.ok(failure);
      assert.notEqual(failure.code, 0);
      assert.match(failure.stderr, /another stored-rank rollout owns/);
    } finally {
      await unlink(lockPath).catch(() => {});
    }
  });

  it("rejects a concurrent service container replacement", async () => {
    const scriptPath = fileURLToPath(
      new URL("postgres-stored-rank-rollout.sh", import.meta.url)
    );
    await assert.rejects(
      execFileAsync(scriptPath, ["test-block-identity"], {
        cwd: fileURLToPath(repoRoot),
        env: process.env,
        encoding: "utf8"
      }),
      /container identity changed/
    );
  });

  it("writes durable incident evidence for standalone rollback failure", async () => {
    const evidenceDirectory = fileURLToPath(
      new URL(`stored-rank-standalone-${process.pid}/`, import.meta.url)
    );
    const scriptPath = fileURLToPath(
      new URL("postgres-stored-rank-rollout.sh", import.meta.url)
    );
    await mkdir(evidenceDirectory);
    await writeFile(`${evidenceDirectory}/manifest.json`, "{}\n");
    let failure;
    try {
      await execFileAsync(scriptPath, ["test-standalone-rollback-incident"], {
        cwd: fileURLToPath(repoRoot),
        env: {
          ...process.env,
          ROLLOUT_TEST_EVIDENCE_DIR: evidenceDirectory
        },
        encoding: "utf8"
      });
    } catch (error) {
      failure = error;
    }

    try {
      assert.ok(failure);
      assert.equal(failure.code, 55);
      const files = await readdir(evidenceDirectory);
      const incidentName = files.find((name) =>
        name.startsWith("rollout-incident-")
      );
      assert.ok(incidentName);
      const incident = JSON.parse(
        await readFile(`${evidenceDirectory}/${incidentName}`, "utf8")
      );
      assert.equal(incident.accepted, false);
      assert.equal(incident.rollbackStatus, 55);
      assert.equal(files.includes("acceptance.json"), false);
    } finally {
      await rm(evidenceDirectory, { recursive: true, force: true });
    }
  });

  it("rejects and records failed normal-mode recovery", async () => {
    const evidenceDirectory = fileURLToPath(
      new URL(`stored-rank-recovery-${process.pid}/`, import.meta.url)
    );
    const scriptPath = fileURLToPath(
      new URL("postgres-stored-rank-rollout.sh", import.meta.url)
    );
    await mkdir(evidenceDirectory);
    await writeFile(`${evidenceDirectory}/manifest.json`, "{}\n");
    let failure;
    try {
      await execFileAsync(scriptPath, ["test-normal-recovery-incident"], {
        cwd: fileURLToPath(repoRoot),
        env: {
          ...process.env,
          ROLLOUT_TEST_EVIDENCE_DIR: evidenceDirectory
        },
        encoding: "utf8"
      });
    } catch (error) {
      failure = error;
    }

    try {
      assert.ok(failure);
      assert.equal(failure.code, 66);
      const files = await readdir(evidenceDirectory);
      const incidentName = files.find((name) =>
        name.startsWith("rollout-incident-")
      );
      assert.ok(incidentName);
      const incident = JSON.parse(
        await readFile(`${evidenceDirectory}/${incidentName}`, "utf8")
      );
      assert.equal(incident.accepted, false);
      assert.equal(incident.failurePhase, "normal-mode-recovery");
      assert.equal(files.includes("acceptance.json"), false);
    } finally {
      await rm(evidenceDirectory, { recursive: true, force: true });
    }
  });

  it("recovers normal mode despite full or unavailable evidence storage", async () => {
    const scriptPath = fileURLToPath(
      new URL("postgres-stored-rank-rollout.sh", import.meta.url)
    );
    for (const reason of ["unavailable", "full"]) {
      const logPath = fileURLToPath(
        new URL(`stored-rank-evidence-${reason}-${process.pid}.log`, import.meta.url)
      );
      let failure;
      try {
        await execFileAsync(scriptPath, ["test-rollback-evidence-failure"], {
          cwd: fileURLToPath(repoRoot),
          env: {
            ...process.env,
            ROLLOUT_TEST_EVENT_LOG: logPath,
            ROLLOUT_TEST_EVIDENCE_FAILURE: reason
          },
          encoding: "utf8"
        });
      } catch (error) {
        failure = error;
      }
      try {
        assert.ok(failure);
        assert.notEqual(failure.code, 0);
        assert.deepEqual(
          (await readFile(logPath, "utf8")).trim().split("\n"),
          [
            "recreate",
            `evidence:rollback:${reason}`,
            "recreate",
            `evidence:recovery:${reason}`,
            "status=1 marker=1 phase=normal-recovery-evidence"
          ]
        );
      } finally {
        await unlink(logPath).catch(() => {});
      }
    }
  });

  it("does not replay read-only rollback after recovery evidence failure", async () => {
    const logPath = fileURLToPath(
      new URL(`stored-rank-recovery-evidence-${process.pid}.log`, import.meta.url)
    );
    const scriptPath = fileURLToPath(
      new URL("postgres-stored-rank-rollout.sh", import.meta.url)
    );
    let failure;
    try {
      await execFileAsync(scriptPath, ["test-recovery-evidence-failure"], {
        cwd: fileURLToPath(repoRoot),
        env: {
          ...process.env,
          ROLLOUT_TEST_EVENT_LOG: logPath
        },
        encoding: "utf8"
      });
    } catch (error) {
      failure = error;
    }
    try {
      assert.ok(failure);
      assert.notEqual(failure.code, 0);
      assert.deepEqual(
        (await readFile(logPath, "utf8")).trim().split("\n"),
        [
          "recreate",
          "evidence:rollback",
          "recreate",
          "evidence:recovery",
          "evidence:recovery",
          "incident:normal-recovery-evidence"
        ]
      );
    } finally {
      await unlink(logPath).catch(() => {});
    }
  });

  it("keeps recovery armed when the pinned normal container is replaced", async () => {
    const logPath = fileURLToPath(
      new URL(`stored-rank-normal-replacement-${process.pid}.log`, import.meta.url)
    );
    const scriptPath = fileURLToPath(
      new URL("postgres-stored-rank-rollout.sh", import.meta.url)
    );
    let failure;
    try {
      await execFileAsync(
        scriptPath,
        ["test-concurrent-normal-replacement"],
        {
          cwd: fileURLToPath(repoRoot),
          env: {
            ...process.env,
            ROLLOUT_TEST_EVENT_LOG: logPath
          },
          encoding: "utf8"
        }
      );
    } catch (error) {
      failure = error;
    }

    try {
      assert.ok(failure);
      assert.notEqual(failure.code, 0);
      assert.deepEqual(
        (await readFile(logPath, "utf8")).trim().split("\n"),
        [
          "recreate",
          "health:recovery-service",
          "rollback:marker=1",
          "incident:normal-mode-recovery"
        ]
      );
      assert.match(
        failure.stderr,
        /normal-mode recovery is unverified; mutation marker remains armed/
      );
    } finally {
      await unlink(logPath).catch(() => {});
    }
  });

  it("rejects replacement during pinned recovery evidence capture", async () => {
    const logPath = fileURLToPath(
      new URL(`stored-rank-evidence-replacement-${process.pid}.log`, import.meta.url)
    );
    const scriptPath = fileURLToPath(
      new URL("postgres-stored-rank-rollout.sh", import.meta.url)
    );
    let failure;
    try {
      await execFileAsync(
        scriptPath,
        ["test-replacement-during-recovery-evidence"],
        {
          cwd: fileURLToPath(repoRoot),
          env: {
            ...process.env,
            ROLLOUT_TEST_EVENT_LOG: logPath
          },
          encoding: "utf8"
        }
      );
    } catch (error) {
      failure = error;
    }

    try {
      assert.ok(failure);
      assert.notEqual(failure.code, 0);
      assert.deepEqual(
        (await readFile(logPath, "utf8")).trim().split("\n"),
        [
          "quiescence:recovery-service",
          "role:recovery-service:current=replacement-service",
          "rollback:marker=1",
          "incident:normal-recovery-evidence"
        ]
      );
    } finally {
      await unlink(logPath).catch(() => {});
    }
  });

  it("recovers normal mode when standalone rollback evidence mount is full", async () => {
    const logPath = fileURLToPath(
      new URL(`stored-rank-standalone-full-${process.pid}.log`, import.meta.url)
    );
    const scriptPath = fileURLToPath(
      new URL("postgres-stored-rank-rollout.sh", import.meta.url)
    );
    let failure;
    try {
      await execFileAsync(
        scriptPath,
        ["test-standalone-full-mount-rollback"],
        {
          cwd: fileURLToPath(repoRoot),
          env: {
            ...process.env,
            ROLLOUT_TEST_EVENT_LOG: logPath
          },
          encoding: "utf8"
        }
      );
    } catch (error) {
      failure = error;
    }

    try {
      assert.ok(failure, "full evidence mount must remain nonzero");
      assert.equal(failure.code, 28);
      assert.deepEqual(
        (await readFile(logPath, "utf8")).trim().split("\n"),
        [
          "config:true:false",
          "build",
          "load",
          "target-memory",
          "lock-enospc:marker=1",
          "recover-normal:marker=1",
          "incident-best-effort:standalone-evidence-lock"
        ]
      );
      assert.match(
        failure.stderr,
        /Restoring fstservice through false rollback and normal recovery/
      );
    } finally {
      await unlink(logPath).catch(() => {});
    }
  });

  it("rejects a same-image concurrent recreate after recovery evidence", async () => {
    const evidencePath = fileURLToPath(
      new URL(`stored-rank-final-recovery-${process.pid}.json`, import.meta.url)
    );
    const logPath = fileURLToPath(
      new URL(`stored-rank-final-recovery-${process.pid}.log`, import.meta.url)
    );
    const scriptPath = fileURLToPath(
      new URL("postgres-stored-rank-rollout.sh", import.meta.url)
    );
    await writeFile(
      evidencePath,
      JSON.stringify({
        label: "recovery",
        fstserviceContainerId: "original-service",
        fstworkerContainerId: "worker-container",
        fstserviceImageReference:
          "ghcr.io/test/service:test@sha256:" + "a".repeat(64),
        fstserviceImageId: "sha256:" + "b".repeat(64),
        fstworkerImageReference: "ghcr.io/sfenton/fstservice:worker",
        fstworkerImageId: "sha256:" + "c".repeat(64),
        fstworkerContainerStatus: "exited",
        fstworkerContainerState:
          "exited|2026-08-04T00:00:00Z|2026-08-04T01:00:00Z|0",
        fstserviceStoredRankFlag: false,
        fstworkerStoredRankFlag: false,
        fstservicePublishedSources: true,
        fstworkerPublishedSources: false,
        fstserviceReadOnlyStartup: false,
        fstworkerReadOnlyStartup: false,
        healthVerified: true
      }) + "\n"
    );
    let failure;
    try {
      await execFileAsync(scriptPath, ["test-final-recovery-drift"], {
        cwd: fileURLToPath(repoRoot),
        env: {
          ...process.env,
          ROLLOUT_TEST_RECOVERY_EVIDENCE: evidencePath,
          ROLLOUT_TEST_EVENT_LOG: logPath
        },
        encoding: "utf8"
      });
    } catch (error) {
      failure = error;
    }
    try {
      assert.ok(failure);
      assert.notEqual(failure.code, 0);
      assert.equal(
        await readFile(logPath, "utf8"),
        "status=1 marker=1 phase=final-recovery-drift\n"
      );
    } finally {
      await unlink(evidencePath).catch(() => {});
      await unlink(logPath).catch(() => {});
    }
  });

  it("rejects recreate occurring during final DB quiescence", async () => {
    const logPath = fileURLToPath(
      new URL(`stored-rank-final-quiescence-${process.pid}.log`, import.meta.url)
    );
    const scriptPath = fileURLToPath(
      new URL("postgres-stored-rank-rollout.sh", import.meta.url)
    );
    let failure;
    try {
      await execFileAsync(
        scriptPath,
        ["test-recreate-during-final-quiescence"],
        {
          cwd: fileURLToPath(repoRoot),
          env: {
            ...process.env,
            ROLLOUT_TEST_EVENT_LOG: logPath
          },
          encoding: "utf8"
        }
      );
    } catch (error) {
      failure = error;
    }
    try {
      assert.ok(failure);
      assert.notEqual(failure.code, 0);
      assert.deepEqual(
        (await readFile(logPath, "utf8")).trim().split("\n"),
        [
          "quiescence-complete",
          "same-image-recreate",
          "recovery-identity-drift",
          "status=1 marker=1 phase=final-recovery-drift"
        ]
      );
    } finally {
      await unlink(logPath).catch(() => {});
    }
  });

  it("requires final service ID to match recovery evidence", async () => {
    const logPath = fileURLToPath(
      new URL(`stored-rank-final-identity-${process.pid}.log`, import.meta.url)
    );
    const scriptPath = fileURLToPath(
      new URL("postgres-stored-rank-rollout.sh", import.meta.url)
    );
    let failure;
    try {
      await execFileAsync(
        scriptPath,
        ["test-final-capture-recovery-identity"],
        {
          cwd: fileURLToPath(repoRoot),
          env: {
            ...process.env,
            ROLLOUT_TEST_EVENT_LOG: logPath
          },
          encoding: "utf8"
        }
      );
    } catch (error) {
      failure = error;
    }
    try {
      assert.ok(failure);
      assert.notEqual(failure.code, 0);
      assert.deepEqual(
        (await readFile(logPath, "utf8")).trim().split("\n"),
        [
          "quiescence-complete",
          "recovery-container-id-mismatch",
          "status=1 marker=1 phase=final-recovery-drift"
        ]
      );
    } finally {
      await unlink(logPath).catch(() => {});
    }
  });

  it("rejects a same-image fstworker recreate during rollout", async () => {
    const logPath = fileURLToPath(
      new URL(`stored-rank-worker-drift-${process.pid}.log`, import.meta.url)
    );
    const scriptPath = fileURLToPath(
      new URL("postgres-stored-rank-rollout.sh", import.meta.url)
    );
    let failure;
    try {
      await execFileAsync(scriptPath, ["test-worker-pin-drift"], {
        cwd: fileURLToPath(repoRoot),
        env: {
          ...process.env,
          ROLLOUT_TEST_EVENT_LOG: logPath
        },
        encoding: "utf8"
      });
    } catch (error) {
      failure = error;
    }
    try {
      assert.ok(failure);
      assert.notEqual(failure.code, 0);
      assert.equal(
        await readFile(logPath, "utf8"),
        "status=1 original=original-worker current=replacement-worker " +
          "image=sha256:" + "c".repeat(64) + "\n"
      );
    } finally {
      await unlink(logPath).catch(() => {});
    }
  });

  it("preserves EXIT trap handling after real quiescence capture", async () => {
    const evidenceDirectory = fileURLToPath(
      new URL(`stored-rank-errexit-${process.pid}/`, import.meta.url)
    );
    const scriptPath = fileURLToPath(
      new URL("postgres-stored-rank-rollout.sh", import.meta.url)
    );
    await mkdir(evidenceDirectory);
    await writeFile(`${evidenceDirectory}/manifest.json`, "{}\n");
    let failure;
    try {
      await execFileAsync(
        scriptPath,
        ["test-errexit-quiescence-rollback"],
        {
          cwd: fileURLToPath(repoRoot),
          env: {
            ...process.env,
            ROLLOUT_TEST_EVIDENCE_DIR: evidenceDirectory
          },
          encoding: "utf8"
        }
      );
    } catch (error) {
      failure = error;
    }
    try {
      assert.ok(failure);
      assert.equal(failure.code, 42);
      const files = await readdir(evidenceDirectory);
      assert.ok(files.includes("quiescence-after-read-only-rollback.json"));
      assert.ok(
        files.includes("quiescence-after-read-only-rollback.json.sha256")
      );
      const incidentName = files.find((name) =>
        name.startsWith("rollout-incident-")
      );
      assert.ok(incidentName);
      const incident = JSON.parse(
        await readFile(`${evidenceDirectory}/${incidentName}`, "utf8")
      );
      assert.equal(incident.exitStatus, 42);
      assert.equal(incident.rollbackStatus, 1);
      assert.equal(incident.accepted, false);
    } finally {
      await rm(evidenceDirectory, { recursive: true, force: true });
    }
  });

  it("loads standalone rollback published scrape ID from the manifest", async () => {
    const evidenceDirectory = fileURLToPath(
      new URL(`stored-rank-rollback-manifest-${process.pid}/`, import.meta.url)
    );
    const logPath = fileURLToPath(
      new URL(`stored-rank-rollback-manifest-${process.pid}.log`, import.meta.url)
    );
    const scriptPath = fileURLToPath(
      new URL("postgres-stored-rank-rollout.sh", import.meta.url)
    );
    await mkdir(evidenceDirectory);
    await writeFile(
      `${evidenceDirectory}/manifest.json`,
      JSON.stringify({
        publishedScrapeId: 1278,
        serviceImageReference:
          "ghcr.io/test/service:test@sha256:" + "a".repeat(64),
        serviceImageId: "sha256:" + "b".repeat(64),
        workerContainerId: "worker-container",
        workerImageReference: "ghcr.io/sfenton/fstservice:worker",
        workerImageId: "sha256:" + "c".repeat(64),
        workerContainerStatus: "exited",
        workerContainerState:
          "exited|2026-08-04T00:00:00Z|2026-08-04T01:00:00Z|0",
        serviceDatabaseTarget: {
          host: "postgres",
          port: 5432,
          database: "fstservice",
          username: "fst"
        },
        postgresContainerId: "postgres-container",
        postgresImageReference: "fst-postgres:17-repack",
        postgresImageId: "sha256:" + "d".repeat(64),
        postgresNetworkNames: ["fst-network"],
        postgresNetworkAliases: ["postgres", "fst-postgres"],
        postgresServerAddresses: ["172.20.0.2"],
        postgresNetworkBindings: [{
          networkName: "fst-network",
          networkId: "network-id",
          serviceAlias: "postgres",
          exclusiveOwnerContainerId: "postgres-container",
          serverAddresses: ["172.20.0.2"]
        }],
        evidenceMountTarget: "/mnt/docker-storage",
        evidenceMountSource: "/dev/test-fst",
        evidenceMountFileSystem: "ext4",
        selectionFingerprint: "manifest"
      }) + "\n"
    );

    const baseEnvironment = {
      ...process.env,
      EXPECTED_PUBLISHED_SCRAPE_ID: "",
      ROLLOUT_TEST_EVIDENCE_DIR: evidenceDirectory,
      ROLLOUT_TEST_EVENT_LOG: logPath
    };
    await execFileAsync(scriptPath, ["test-load-rollback-manifest"], {
      cwd: fileURLToPath(repoRoot),
      env: baseEnvironment,
      encoding: "utf8"
    });

    let conflict;
    try {
      await execFileAsync(scriptPath, ["test-load-rollback-manifest"], {
        cwd: fileURLToPath(repoRoot),
        env: {
          ...baseEnvironment,
          EXPECTED_PUBLISHED_SCRAPE_ID: "1279"
        },
        encoding: "utf8"
      });
    } catch (error) {
      conflict = error;
    }

    try {
      assert.equal(await readFile(logPath, "utf8"), "publishedScrapeId=1278\n");
      assert.ok(conflict);
      assert.notEqual(conflict.code, 0);
      assert.match(conflict.stderr, /conflicts with original manifest/);
    } finally {
      await unlink(logPath).catch(() => {});
      await rm(evidenceDirectory, { recursive: true, force: true });
    }
  });
});
