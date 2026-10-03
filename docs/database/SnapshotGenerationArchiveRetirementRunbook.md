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
  - tools/postgres-snapshot-sparse-compact.py
  - tools/postgres-snapshot-sparse-compact.test.py
  - tools/postgres-snapshot-sparse-compact-drill.py
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionPlanner.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionOracle.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionSchema.cs
  - docs/decisions/0010-snapshot-archive-retirement.md
  - docs/decisions/0011-automatic-snapshot-archive-retirement.md
  - docs/decisions/0012-sparse-snapshot-child-compaction.md
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
touch /mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/archives/snapshot-generations/AUTO_DISABLED  # pause retirement without unloading
touch /mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/archives/snapshot-generations/compaction/AUTO_DISABLED  # pause compaction
```

Every child still lands in `cycle-<id>/manifest.jsonl` with its archive, so
rollback is the same `restore` command. Archives are about 8% of the reclaimed
size (about 3 GiB per day) and are never deleted automatically.

## Sparse-child compaction

With `Features__SkipUnchangedPhysicalLeaderboardSnapshots`, an unchanged scope
keeps pointing at the snapshot where it last changed, so each child stays live
for the few songs that have not changed since while most of its rows become
unreachable. Whole-child retirement cannot touch such a child. On 2026-10-02,
133 of 217 attached children (196.9 GiB) were at most 50% live by estimated
rows, with about 186.5 GiB reclaimable; every recent scrape adds more as its
scopes change. See
[ADR 0012](../decisions/0012-sparse-snapshot-child-compaction.md).

`tools/postgres-snapshot-sparse-compact.py` compacts one child at a time and
shares the retirement lock, so it never overlaps a retirement run:

1. **Per-song liveness re-proof** (read-only): the same roots as the retention
   oracle, per song. A song is live when `leaderboard_snapshot_state`,
   `solo_current_projection_scope`, or a current/previous/working (or
   building/current) publication's `leaderboard_published_scope_source` points
   at this child. Whole-child roots skip the child entirely: a running scrape,
   a named publication's own scrape, an unreplayed writer failure, any
   retention-hold history, a publication surface binding, a user trigger, the
   configured `Scraper:ResumeScrapeId`, or the operator-excluded Solo Bass
   `1308`. A child with no live songs belongs to retirement, not compaction.
2. **Measure:** one scan records whole-child and live-subset row counts and
   order-independent fingerprints. Children above `--max-live-fraction`
   (default `0.5`) or under `--min-reclaim-bytes` (default 128 MiB) are skipped
   before any archive.
3. **Archive and verify** the whole child exactly as retirement does
   (`compaction/<instrument>/<child>-o<oid>.dump`, TOC and row-count checks,
   every `--drill-every`th child fully restored).
4. **Build** `<child>_cnew` (`LIKE` the child, live songs only, ordered by the
   primary key), add the partition bound as a validated `CHECK` and the
   instrument parent's index shapes (primary key via `USING INDEX`), analyze,
   and require the replacement's fingerprint to equal the live subset's.
5. **Re-prove** liveness; the live set may only shrink, and the run ends
   before the swap if the network-bound window closed.
6. **Swap** in one transaction with `lock_timeout=500ms` (below the server's
   1-second `deadlock_timeout`, so any lock cycle aborts the swap rather than a
   worker write) and up to 12 retries. It first takes `SHARE` locks on every
   liveness-root table and on the child itself (which conflicts with
   `CREATE TRIGGER` and other DDL on it) and holds them through `COMMIT`, then
   requires the instrument parent's DEFAULT partition to be empty, no
   whole-child root (including a user trigger), and no live song outside the
   copied set. From the worker check through the committed swap the run holds
   the worker-guard flock (`.fst-worker-compose-guard.lock`, shared and
   non-blocking), so no guarded worker stop or recreate can interleave; it also
   requires the `fstworker` container ID and its `Scraper:ResumeScrapeId` to
   equal the values pinned at run start, because a resume change needs a
   container recreate. Only then does it `DETACH` the child,
   rename it `<child>_cold`, rename the replacement to the child's name, and
   `ATTACH ... FOR VALUES IN (<snapshot>)`. The replacement's `CHECK` skips its
   validation scan and its indexes are adopted; PostgreSQL still validates the
   (empty) DEFAULT partition under its lock. Lock timeouts, deadlocks, and
   statement timeouts are transient deferrals.
7. **Record** a durable `swapped` manifest entry with the new identity, then
   **verify** the attachment, bound, index adoption (every parent index has
   exactly one adopted child index), and the live fingerprint; then
   `DROP TABLE <child>_cold RESTRICT`, drop the temporary `CHECK`, and record
   `compacted`.

`compaction/manifest.jsonl` records `archived`, `swapped`, and `compacted`
entries with the old and new physical identity, the live song list, and both
fingerprints. If a run stops after `swapped`, `<child>_cold` remains and the
tool refuses that child until an operator restores it (`restore` accepts a
`swapped` entry) or drops `<child>_cold` after confirming the compacted
fingerprint. The compacted child has a new OID and relfilenode; later report
cycles classify it like any other child, and it is compacted again as more of
its songs change.

```bash
python3 tools/postgres-snapshot-sparse-compact.py plan --show 20
# Only during network-bound fetch; one child, full restore drill
python3 tools/postgres-snapshot-sparse-compact.py compact --limit 1 --drill-every 1 \
  --only leaderboard_entries_snapshot_<instrument>_s<id> \
  --stop-file /mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/archives/snapshot-generations/compaction/STOP
# Rollback: online re-insert of the archived non-live rows, exact fingerprint check
python3 tools/postgres-snapshot-sparse-compact.py restore --relation <child>
```

`restore` requires the child to still be the compacted table recorded in the
manifest with its compacted fingerprint, reloads the archive's data into a
standalone `<child>_rfill` table, checks the archived whole-child fingerprint,
inserts only the non-live songs' rows into the attached child, and requires the
original whole-child fingerprint. The worker configuration must be readable
(for `Scraper:ResumeScrapeId`) or the run defers. There is no window bypass;
the isolated drill (`tools/postgres-snapshot-sparse-compact-drill.py
--work-root <dir>`) injects its own window. Compaction obeys the live-safety
windows below and exits with code 3 on a transient deferral.

`compact` scans exactly only children whose cheap estimate (live scope rows
from published scope sources against the child's row estimate) is within
`--max-live-fraction + 0.15` and half the reclaim floor, largest estimated
reclaim first, so dense and just-compacted children cost one catalog query.

### Automatic compaction

The same systemd timer runs `postgres-snapshot-sparse-compact.py auto` after
the retirement step. `auto` compacts eligible children (largest first, up to
400 per run) only while the network-bound window stays open, for at most 150
minutes, and re-checks `compaction/AUTO_DISABLED` before every child. Transient
refusals exit successfully for the next tick; any other failure writes
`compaction/AUTO_DISABLED` (independent of retirement's tripwire) and every
later run stays idle until an operator investigates and deletes it. Children
decay as their scopes change, so a child is compacted again once its live
fraction falls under the threshold.

2026-10-02 production canary (scrape `1458` fetch): Pro Bass `s1413` went
from 1,065,520 to 16,530 rows and 540 MB to 6.5 MB in 11 seconds, with a
passing full restore drill, a first-attempt swap, identical API page hashes
for all six live songs across six offsets, identical per-song root-table
fingerprints, and both indexes adopted.

## Live-safety windows

Run only while the scrape is network-bound (solo or band page fetching) or
during an idle interval. Measured on scrape `1436`, a concurrent run slowed the
band spool flush (`flushing_band`) from 393 to 167 chunks per minute, so stop
runs (create the stop file) before any flush subphase or post-processing.
Post-processing phases also read current snapshots for long periods; a detach
then exhausts its retries and the run stops rather than queueing. Stop runs
before planned worker cutovers: create the manual run's stop file, and create
both `AUTO_DISABLED` files (archive root and `compaction/`; delete them after
the cutover) so automatic runs end before their next child. A guarded worker
cutover cannot interleave with a compaction swap, which holds the worker-guard
flock. Archives stay on the 4 TB FST drive.

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
