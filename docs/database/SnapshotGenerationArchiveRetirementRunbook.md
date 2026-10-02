---
status: living-runbook
owner: data
last_verified: 2026-10-02
last_verified_commit: 598965ef
sources:
  - tools/postgres-snapshot-archive-retire.py
  - tools/postgres-snapshot-archive-retire.test.py
  - deploy/systemd/fst-snapshot-retire-auto.service
  - deploy/systemd/fst-snapshot-retire-auto.timer
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionPlanner.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionOracle.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionSchema.cs
  - docs/decisions/0010-snapshot-archive-retirement.md
  - docs/decisions/0011-automatic-snapshot-archive-retirement.md
update_triggers:
  - Snapshot-generation liveness roots, report-cycle format, partition layout, archive format, or retirement/restore commands change.
  - Scrape phase or sub-operation names, `/api/service-info` `currentUpdate` fields, or the host timer change.
---

# Snapshot generation archive retirement

This operator procedure reclaims space from `leaderboard_entries_snapshot`
children that an immutable report-only retention cycle proved unreferenced. It
archives every child before any mutation, keeps each archive on the FST drive,
and restores any child with one command. It is a host tool, not a worker
feature. A host systemd timer runs its `auto` mode once per scrape during
network-bound fetching (see [Automatic retirement](#automatic-retirement)).
See [ADR 0010](../decisions/0010-snapshot-archive-retirement.md) for why it
replaces the per-child quarantine machine for this workload and
[ADR 0011](../decisions/0011-automatic-snapshot-archive-retirement.md) for why
it runs automatically.

## Authorization boundary

A run is bound to exactly one retention cycle and its
`candidate_identity_hash`. The tool refuses unless the cycle is observed,
report-only, oracle-agreeing, free of global blockers, and its planner and
oracle candidate sets are identical. It accepts no SQL, relation list from
outside the cycle, CASCADE, or force option. `--only` may narrow a run but
never names a child outside the cycle.

## Per-child sequence

1. **Live-safety preflight:** FST drive free space of at least 100 GiB,
   healthy `fst-postgres` and `fstservice`, and no more than 20 lock waits.
2. **Exact identity and liveness re-proof** (read-only, immediately before
   touching the child): same OID and relfilenode as the cycle, still attached
   to the recorded instrument parent. The child must not be bound by the
   current, previous, or working publication, nor by any `building`/`current`
   generation, through `leaderboard_published_scope_source`. It must also not
   be the active snapshot in `leaderboard_snapshot_state`, a
   `solo_current_projection_scope` source, named in a live publication's
   surface bindings, or ever held in `snapshot_generation_retention_holds`.
   It must carry no user trigger and must not be Solo Bass snapshot `1308`.
3. **Fingerprint:** one read-only scan records the row count and an
   order-independent content hash (`sum(hashtextextended(row::text, 0))`).
4. **Archive:** `pg_dump -Fc -Z zstd:3 --no-owner --no-privileges -t <child>`
   streams to
   `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/archives/snapshot-generations/cycle-<id>/<instrument>/<child>.dump`
   and is fsynced before rename.
5. **Verify:** a network-none `postgres:17` container checks the archive TOC
   (table, data, primary key, index) and counts every archived row, which must
   equal the fingerprint count. By default every 25th child (and always the
   first child of a run) is fully restored standalone in that container and
   must reproduce the exact fingerprint.
6. **Manifest:** `manifest.jsonl` in the cycle directory records identity,
   rows, fingerprint, sizes, archive path, and SHA-256 (`state=archived`).
7. **Re-proof** of step 2, then a plain `ALTER TABLE <parent> DETACH PARTITION
   <child>`. Every instrument parent owns a DEFAULT partition, so `DETACH
   CONCURRENTLY` is unavailable; the catalog-only detach runs with
   `lock_timeout=2s` and up to 12 bounded retries so queued readers/writers
   wait at most two seconds per attempt.
8. `DROP TABLE <child> RESTRICT` on the now-standalone table and a
   `state=dropped` manifest entry.

A run stops at the first unexpected error, at `--limit`, or when its
`--stop-file` exists. Re-running the same cycle skips children already
`dropped`.

## Commands

```bash
H=<cycle candidate_identity_hash>
# Read-only eligibility and bytes
python3 tools/postgres-snapshot-archive-retire.py plan --cycle 95 --expected-candidate-hash "$H"
# Bounded retirement (stop by creating the stop file)
python3 tools/postgres-snapshot-archive-retire.py retire --cycle 95 \
  --expected-candidate-hash "$H" --limit 50 --drill-every 25 --pause-seconds 3 \
  --stop-file /mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/snapshot-retire-cycle95/STOP
# Rollback one child
python3 tools/postgres-snapshot-archive-retire.py restore --cycle 95 --relation <child>
```

`restore` verifies the archive SHA-256, refuses an existing relation, restores
the child standalone from a `postgres:17` client in `fst-postgres`'s network
namespace (loopback trust auth; the archive is mounted read-only from the FST
drive), re-attaches it with `ATTACH PARTITION ... FOR VALUES IN (<snapshot>)`
under the same bounded lock policy, and requires the exact archived
fingerprint. A single-table archive of an attached partition orders its
`TABLE ATTACH` entry before its own primary key, so a direct `pg_restore` of
the whole archive fails with "multiple primary keys"; restoring without the
`ATTACH` entries and attaching afterwards adopts the restored indexes as
partitions of the parent indexes. A restored child receives a new OID and
relfilenode, so the same cycle can never retire it again; a later report cycle
must reclassify it.

## Automatic retirement

Each scrape's terminal post-publication safe point records a new report-only
cycle, and each cycle adds roughly 9 GiB of newly unreferenced children
(about 36 GiB per day at four scrapes per day). `auto` retires them without an
operator-supplied hash:

1. It does nothing while `AUTO_DISABLED` exists in the archive root.
2. It reads `GET /api/service-info` and continues only when `currentUpdate`
   is `scrape.leaderboards` with sub-operation `fetching_leaderboards`,
   `fetching_pages`, or `awaiting_band`. Idle workers, spool drains, index
   drops, flushes, index builds, post-processing, unknown sub-operations, and
   an unreachable API all close the window.
3. It selects the newest observed, report-only, oracle-agreeing cycle with no
   global blockers that is no older than 12 hours and was recorded for the
   current publication, and binds that cycle's own `candidate_identity_hash`.
   The normal `retire` path then re-checks every cycle gate.
4. It retires that cycle with the per-child sequence above, re-probing the
   window (and `AUTO_DISABLED`) before every child, so the run ends when the
   scrape reaches its flush or an operator creates the file.

Load, health, disk-floor, lock-wait, lock-held, and exhausted detach-contention
refusals are transient: the run exits successfully and the next timer tick
retries. The health/disk/lock-wait preflight runs before cycle selection, so an
unavailable database defers instead of tripping. Any other error (cycle
selection or gate, fingerprint, archive, restore drill, non-lock DDL failure)
writes `AUTO_DISABLED` with the cycle (when known) and error and exits
non-zero, and every later run stays idle until an operator investigates and
deletes the file.

The timer runs the tool from a dedicated worktree detached at accepted master:

```bash
OPS=/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/ops/snapshot-retire-checkout
git worktree add --detach "$OPS" origin/master        # or: git -C "$OPS" checkout --detach origin/master
cp "$OPS"/deploy/systemd/fst-snapshot-retire-auto.{service,timer} ~/.config/systemd/user/
systemctl --user daemon-reload
systemctl --user enable --now fst-snapshot-retire-auto.timer
journalctl --user -u fst-snapshot-retire-auto.service --since today   # run log
systemctl --user disable --now fst-snapshot-retire-auto.timer        # stop automation
touch /mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/archives/snapshot-generations/AUTO_DISABLED  # pause without unloading
```

Every child still lands in `cycle-<id>/manifest.jsonl` with its archive, so
rollback is the same `restore` command. Archives are about 8% of the reclaimed
size (about 3 GiB per day) and are never deleted automatically.

## Live-safety windows

Run only while the scrape is network-bound (solo or band page fetching) or
during an idle interval. Measured on scrape `1436`, a concurrent run slowed the
band spool flush (`flushing_band`) from 393 to 167 chunks per minute, so stop
runs (create the stop file) before any flush subphase or post-processing.
Post-processing phases also read current snapshots for long periods; a detach
then exhausts its retries and the run stops rather than queueing. Stop runs
before planned worker cutovers: create the manual run's stop file, and create
`AUTO_DISABLED` (then delete it after the cutover) so an automatic run ends
before its next child. Archives stay on the 4 TB FST drive.

## 2026-09-26 cycle 95 evidence

Cycle `95` (scrape `1424`, publication `344`) recorded 995 planner/oracle
candidates (1,130 GiB; 494 GiB heap) and hash
`e5922aad48b99925088ce1656ed36e65b9ab447c29176cbeab045705140aa7ab`. The live
re-proof found 994 eligible; Pro Cymbals snapshot `1314` is excluded by its
released hold history. Isolated drills proved detach, archive, drop, restore,
and re-attach with exact content parity, and that DEFAULT partitions forbid
concurrent detach. On production, canary child
`leaderboard_entries_snapshot_solo_bass_s1377` (2,777,743 rows, 1.20 GiB)
archived to 103 MiB (about 8% of its on-disk size), passed a full restore
drill, was dropped, restored with its fingerprint and both index attachments
identical to an untouched sibling, and then was correctly refused for
re-retirement because its physical identity changed. API responses for
`/api/songs`, a Solo Bass leaderboard, and Solo Bass rankings, plus the
current/previous/working publication pointers (344/342/357), were identical
before and after. The batch then proceeded during scrape `1436` acquisition
with zero lock waits.

Cycle `95` is complete. Batches ran only in network-bound windows (scrape
`1436` acquisition from 2026-09-26T23:31Z, stopped before its band flush, and
scrape `1438` acquisition, finishing 2026-09-27T09:17Z). 993 children were
archived, verified, and dropped: 2,437,512,533 rows, 1,129.2 GiB on disk
(493.7 GiB heap) reclaimed, with 87.7 GiB of archives under
`archives/snapshot-generations/cycle-95/` on the FST drive. The FST drive
went from 68% to 41% used. The final read-only `plan` reports every candidate
`missing` except the two intended exclusions: the restored Solo Bass `1377`
canary (`oid-or-relfilenode-changed`) and Pro Cymbals `1314`
(`retention-hold-history`). A later report cycle must reclassify them; cycle
`95` has no further work.
