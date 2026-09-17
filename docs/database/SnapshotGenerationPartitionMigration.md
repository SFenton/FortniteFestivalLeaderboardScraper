---
status: living-runbook
owner: data
last_verified: 2026-08-27
last_verified_commit: c35b7f47
sources:
  - tools/postgres-snapshot-generation-migration.py
  - tools/postgres-snapshot-generation-migration.sh
  - tools/postgres-snapshot-generation-migration-drill.py
  - tools/postgres-snapshot-generation-migration.test.py
  - tools/postgres-pro-bass-snapshot-rewrite.py
  - docs/database/ProBassSnapshotRewritePilot.md
  - docs/database/SnapshotGenerationRetentionSafety.md
  - docs/operations/live-safety.md
update_triggers:
  - Snapshot instrument bounds, protected-source ownership, archive/restore evidence, migration stages, capacity margins, rollback, or retention rules change.
---

# Snapshot generation partition migration

## Status and boundary

The fixed migration package converts one
`leaderboard_entries_snapshot` instrument partition at a time from a regular
table into a `LIST (snapshot_id)` partitioned table. It retains only the exact
physical snapshot IDs still required by active state, the solo current
projection, and the current/previous/working publication source maps.

The package passed its five-lane isolated PostgreSQL 17 drill. The `pro-bass`
and `pro-guitar` targets completed production migration on 2026-08-18,
`solo-guitar` completed on 2026-08-20, and `solo-vocals` and `solo-drums`
completed on 2026-08-21. `solo-bass` completed on 2026-08-22, and
`pro-vocals`, `pro-cymbals`, and `pro-drums` completed on 2026-08-23. All
nine instrument roots now use snapshot-generation children.
Generation-aware validation scrapes `1304`, `1305`, `1306`, `1307`, `1309`,
and `1310` completed through publication, notifications, registration drain,
and normal run-once worker exit. Scrape `1310` accepted all nine generation
writer paths and the global generation-DDL lock. The worker remains held
offline before recurring destructive generation retention is implemented and
accepted. A separate default-off report-only observer now exists, but it
cannot create executable work.
This runbook is not authorization to unfreeze reads, select alternate scratch
storage, delete an archive, or weaken a failed gate.

This package migrates physical layout only. It does not implement archive,
detach, drop, or recurring deletion. After each instrument migration, run exactly one
guarded run-once validation scrape and hold the worker again after terminal
publication, notification recovery, registration drain, and exit. Do not
resume unattended normal worker scheduling after the nine instrument
migrations until a separate archive-before-child-drop owner is implemented,
restore-tested, documented, and accepted.

The operator-authorized exception for the final migration interval applies
only after a clean post-Solo Bass retry proves the six migrated writer paths.
Pro Vocals, Pro Cymbals, and Pro Drums may then be migrated sequentially in one
worker hold without an intervening scrape, but each target must independently
complete every archive, network-none restore, build, swap, validate, drop,
API, capacity, and recovery gate. One complete scrape across all nine
instruments is required immediately afterward. Pro Vocals, Pro Cymbals, and Pro
Drums completed those gates, and validation scrape `1310` accepted the complete
all-nine layout.

Production Compose ownership remains:

```text
/home/sfenton/Docker/FestivalServiceTracker
```

Repository Compose files are templates. The only authorized temporary scratch
device is `/dev/nvme2n1p2`, mounted at `/`. Accepted PostgreSQL relations and
every retained generation child must finish in `pg_default` on the 4 TB FST
PGDATA filesystem. The tool never creates an 8 TB tablespace.

## Fixed targets

The command accepts only these nine compiled instrument keys. There is no
relation, table, partition-bound, or SQL argument.

| Key | Fixed partition | Fixed bound |
|---|---|---|
| `solo-guitar` | `leaderboard_entries_snapshot_solo_guitar` | `Solo_Guitar` |
| `solo-bass` | `leaderboard_entries_snapshot_solo_bass` | `Solo_Bass` |
| `solo-drums` | `leaderboard_entries_snapshot_solo_drums` | `Solo_Drums` |
| `solo-vocals` | `leaderboard_entries_snapshot_solo_vocals` | `Solo_Vocals` |
| `pro-guitar` | `leaderboard_entries_snapshot_pro_guitar` | `Solo_PeripheralGuitar` |
| `pro-bass` | `leaderboard_entries_snapshot_pro_bass` | `Solo_PeripheralBass` |
| `pro-vocals` | `leaderboard_entries_snapshot_pro_vocals` | `Solo_PeripheralVocals` |
| `pro-cymbals` | `leaderboard_entries_snapshot_pro_cymbals` | `Solo_PeripheralCymbals` |
| `pro-drums` | `leaderboard_entries_snapshot_pro_drums` | `Solo_PeripheralDrums` |

The accepted pro-bass rewrite and its historical custom archive remain
independent evidence. Converting the current pro-bass relation into generation
children still creates and restore-proves a new archive of that exact current
source before dropping it. Existing pro-bass evidence must not be deleted or
treated as scratch capacity.

### Accepted production pro-bass generation migration

Run `snapshot-generation-pro-bass-20260818T190019Z` retained physical
snapshots `1302-1303` and removed obsolete `1301` from hot storage:

- exact source/archive rows: `8,602,324`;
- retained rows: `5,256,465`;
- removed rows: `3,345,859`;
- source bytes: `3,812,302,848`;
- final partition tree: `2,214,182,912` bytes;
- immediate filesystem return: `3,812,192,256` bytes;
- swap: `0.054` seconds;
- finalization: `79.669` seconds;
- archive: `323,003,699` bytes, SHA-256
  `94d499d94b21dcf17aee0ba3c006590176b17c4dd494c4b2ff8117f2d60c136e`;
- final report SHA-256:
  `2d9ac6d8e5252ffab70404aa87d74dab77639c1a07db012c4f5576ebc43fb98e`.

The final root has only generation children `1302`, `1303`, and an empty
default child; all root/leaf relations and indexes are in `pg_default`.
Candidate/original retained fingerprints, per-generation hashes, 1,404 named
publication-source rows, active/projection references, and exact public
`/api/songs` and `/api/rankings/overview` bodies matched. Seventeen final-drop
API-monitor samples had zero failures. Rename-back rollback ended at final
drop; the independent read-only recovery package under
`/home/sfenton/fst-temporary/snapshot-generation-pro-bass-20260818T190019Z`
is now authoritative until a separate deletion decision.

### Accepted production pro-guitar generation migration

Run `snapshot-generation-pro-guitar-20260818T191034Z` retained physical
snapshots `1302-1303` and removed 243 obsolete generations from hot storage:

- exact source/archive rows: `1,015,961,791`;
- retained rows: `9,239,429`;
- removed rows: `1,006,722,362`;
- source bytes: `588,213,903,360`;
- final partition tree: `4,074,053,632` bytes;
- immediate filesystem return: `588,232,740,864` bytes;
- swap: `0.047` seconds;
- finalization: `5,988.277` seconds;
- archive: `42,109,010,793` bytes, SHA-256
  `0cd7b95105959dc6618b94c2c283804f3aa1b521645746c94db7d5d35674f476`;
- final report SHA-256:
  `8c287af2d92f04040a6cc277860d95ac4b6a8fc83aeaf32a058c6c9fb2a3a508`.

The network-none restore peaked at `448,530,678,492` bytes and proved all 245
snapshot generations. Final live reproof scanned the complete original
fingerprint and distribution before the short destructive DDL. The final root
has only generation children `1302`, `1303`, and an empty default; all
relations/indexes are in `pg_default` and no `sgm_pg_*` artifacts remain.
Candidate/original retained evidence, 1,404 named sources, active/projection
references, and exact public songs/rankings bodies matched. The API monitor
recorded 1,158 successful samples and zero failures. FST free space rose from
`186,709,254,144` to `774,941,995,008` bytes. The independent recovery package
under
`/home/sfenton/fst-temporary/snapshot-generation-pro-guitar-20260818T191034Z`
remains authoritative until a separate deletion decision.

### Accepted production solo-guitar generation migration

Run `snapshot-generation-solo-guitar-20260819T235344Z` retained physical
snapshots `1302-1304` and removed 169 obsolete generations from hot storage:

- exact source/archive rows: `902,057,650`;
- retained rows: `17,888,406`;
- removed rows: `884,169,244`;
- source bytes: `445,955,399,680`;
- final partition tree: `7,126,245,376` bytes;
- immediate filesystem return: `445,956,923,392` bytes;
- swap: `0.634` seconds;
- finalization: `5,284.958` seconds;
- archive: `35,890,035,966` bytes, SHA-256
  `d5ea6a42d199e5e72f2146a3587b26a08734415bbc3101e8f4ddfb7f22a86f74`;
- isolated PostgreSQL 17 restore peak: `358,729,507,548` bytes.

The final children contain `6,849,320`, `5,885,574`, and `5,153,512` rows for
snapshots `1302`, `1303`, and `1304`; the default child is empty. The
network-none restore container and transient PGDATA were removed. The live API
monitor recorded `1,021` successful samples and zero failures. The
authoritative recovery package remains under
`/home/sfenton/fst-temporary/snapshot-generation-solo-guitar-20260819T235344Z`;
the earlier failed workspace
`snapshot-generation-solo-guitar-20260819T230609Z` remains forensic evidence.

### Accepted production solo-vocals generation migration

Run `snapshot-generation-solo-vocals-20260820T223324Z` retained physical
snapshots `1302-1305` and removed 170 obsolete generations from hot storage:

- exact source/archive rows: `912,731,557`;
- retained rows: `23,925,998`;
- removed rows: `888,805,559`;
- source bytes: `445,078,241,280`;
- final partition tree: `9,389,801,472` bytes;
- immediate filesystem return: `445,096,439,808` bytes;
- swap: `0.471` seconds;
- finalization: `5,407.036` seconds;
- archive: `36,064,790,508` bytes, SHA-256
  `898dab8368048ef1ba18eae2586e2ef4599115e9b066a4ce5b707243d8576d84`;
- isolated PostgreSQL 17 restore peak: `358,083,150,556` bytes;
- final report SHA-256:
  `a662d2dfb450d321f5290fc1499f4dbf0914c3af167137750a4455f747b448d5`.

The final children contain `6,914,186`, `6,083,195`, `5,144,706`, and
`5,783,911` rows for snapshots `1302`, `1303`, `1304`, and `1305`; the
default child is empty. All accepted relations and indexes are in
`pg_default`, no `sgm_sv_*` artifact remains, and the network-none restore
container and transient PGDATA were removed. The live API monitor recorded
`1,040` successful samples and zero failures. The authoritative recovery
package remains under
`/home/sfenton/fst-temporary/snapshot-generation-solo-vocals-20260820T223324Z`
until a separate deletion decision.

### Accepted production solo-drums generation migration

Run `snapshot-generation-solo-drums-20260821T153515Z` retained physical
snapshots `1302-1306` and removed 171 obsolete generations from hot storage:

- exact source/archive rows: `904,310,454`;
- retained rows: `28,959,242`;
- removed rows: `875,351,212`;
- source bytes: `428,560,547,840`;
- final partition tree: `11,075,510,272` bytes;
- immediate filesystem return: `428,561,866,752` bytes;
- swap: `0.469` seconds;
- finalization: `5,662.084` seconds;
- archive: `36,036,312,306` bytes, SHA-256
  `95112639111d3989891fb689224b72e5a1fac97086263a05e8b574c644f24d4d`;
- isolated PostgreSQL 17 restore peak: `345,121,186,524` bytes;
- final report SHA-256:
  `5173e61dacf625214b32092ffc37fcae34d127798e63a7c34e0164d9f7ba372f`.

The final children contain `6,767,917`, `6,101,098`, `5,146,860`,
`5,713,732`, and `5,229,635` rows for snapshots `1302`, `1303`, `1304`,
`1305`, and `1306`; the default child is empty. All accepted relations and
indexes are in `pg_default`, no `sgm_sd_*` artifact remains, and the
network-none restore container and transient PGDATA were removed. The live
API monitor recorded `1,079` successful samples and zero failures. The
authoritative recovery package remains under
`/home/sfenton/fst-temporary/snapshot-generation-solo-drums-20260821T153515Z`
until a separate deletion decision.

### Accepted production solo-bass generation migration

Run `snapshot-generation-solo-bass-20260822T111348Z` retained physical
snapshots `1302-1307` and removed 166 obsolete generations from hot storage:

- exact source/archive rows: `895,497,806`;
- retained rows: `28,995,467`;
- removed rows: `866,502,339`;
- source bytes: `429,124,583,424`;
- final partition tree: `11,351,261,184` bytes;
- immediate filesystem return: `429,125,984,256` bytes;
- swap: `0.460` seconds;
- finalization: `5,397.909` seconds;
- archive: `35,598,305,328` bytes, SHA-256
  `ceba77be3b41235fe16180f93a4be95308d8e9e20016c6560aca2e1c70a970c2`;
- isolated PostgreSQL 17 restore peak: `348,066,407,132` bytes;
- final report SHA-256:
  `1a5b9bbc99ff1c75c961a64ac0f6a28b43ec1dfacb45204bfc4d65d28e54d8e6`.

The final children contain `6,698,206`, `5,197,807`, `4,011,463`,
`4,724,434`, `4,508,477`, and `3,855,080` rows for snapshots `1302`, `1303`,
`1304`, `1305`, `1306`, and `1307`; the default child is empty. All accepted
relations and indexes are in `pg_default`, no `sgm_sb_*` artifact remains,
and the network-none restore container and transient PGDATA were removed. The
live API monitor recorded `1,040` successful samples and zero failures. The
authoritative recovery package remains under
`/home/sfenton/fst-temporary/snapshot-generation-solo-bass-20260822T111348Z`
until a separate deletion decision.

### Accepted production pro-vocals generation migration

Run `snapshot-generation-pro-vocals-20260823T064042Z` retained physical
snapshots `1302-1307` and `1309` and removed 166 obsolete generations from hot
storage:

- exact source/archive rows: `633,981,317`;
- retained rows: `34,514,935`;
- removed rows: `599,466,382`;
- source bytes: `350,834,352,128`;
- final partition tree: `15,253,815,296` bytes;
- immediate filesystem return: `350,852,210,688` bytes;
- swap: `0.457` seconds;
- finalization: `3,879.986` seconds;
- archive: `25,233,230,471` bytes, SHA-256
  `f583cc61e2e9f14adab8762ca513b229be7eda62f1dae3e774a2538ed2821b52`;
- isolated PostgreSQL 17 restore peak: `281,750,774,492` bytes;
- build WAL: `15,503,605,888` bytes;
- build temporary data: `6,916,947,968` bytes;
- final report SHA-256:
  `f726db57d0929dd627c4fc0ad7f3556e6a262512d549eb27d05928bfd5351d4e`.

The final children contain `4,956,029`, `4,954,113`, `4,910,414`,
`4,947,342`, `4,922,420`, `4,869,712`, and `4,954,905` rows for snapshots
`1302`, `1303`, `1304`, `1305`, `1306`, `1307`, and `1309`; the default child
is empty. Candidate/original retained fingerprints, publication/reference
state, and exact public songs and rankings-overview bodies matched. All
accepted relations and indexes are in `pg_default`, no `sgm_pv_*` artifact
remains, and the network-none restore container and transient PGDATA were
removed. The live API monitor recorded `752` successful samples and zero
failures. The authoritative recovery package remains under
`/home/sfenton/fst-temporary/snapshot-generation-pro-vocals-20260823T064042Z`
until a separate deletion decision.

### Accepted production pro-cymbals generation migration

Run `snapshot-generation-pro-cymbals-20260823T100944Z` retained physical
snapshots `1302-1307` and `1309` and removed 137 obsolete generations from hot
storage:

- exact source/archive rows: `8,661,068`;
- retained rows: `400,455`;
- removed rows: `8,260,613`;
- source bytes: `4,757,266,432`;
- final live partition tree: `185,352,192` bytes;
- immediate filesystem return: `4,757,069,824` bytes;
- swap: `0.087` seconds;
- finalization: `52.998` seconds;
- archive: `340,809,727` bytes, SHA-256
  `90ee465fa025cf19d64d208569379472c13b640e1e5a9c28648b331aaaf8f975`;
- isolated PostgreSQL 17 restore peak: `5,104,447,032` bytes;
- build WAL: `186,691,696` bytes;
- build temporary data: `80,470,016` bytes;
- final report SHA-256:
  `76f16282ebf95a542b0894c8ff07781d75cde9a756f3999440f3a7bdd1773676`.

The final children contain `99,685`, `65,155`, `45,951`, `54,147`, `44,854`,
`39,975`, and `50,688` rows for snapshots `1302`, `1303`, `1304`, `1305`,
`1306`, `1307`, and `1309`; the default child is empty. Candidate/original
retained fingerprints, publication/reference state, and exact public songs and
rankings-overview bodies matched. All accepted relations and indexes are in
`pg_default`, no `sgm_pc_*` artifact remains, and the network-none restore
container and transient PGDATA were removed. The live API monitor recorded
`12` successful samples and zero failures. The authoritative recovery package
remains under
`/home/sfenton/fst-temporary/snapshot-generation-pro-cymbals-20260823T100944Z`
until a separate deletion decision.

### Accepted production pro-drums generation migration

Run `snapshot-generation-pro-drums-20260823T101944Z` retained physical
snapshots `1302-1307` and `1309` and removed 133 obsolete generations from hot
storage:

- exact source/archive rows: `5,473,658`;
- retained rows: `190,168`;
- removed rows: `5,283,490`;
- source bytes: `2,942,705,664`;
- final live partition tree: `86,032,384` bytes;
- immediate filesystem return: `2,942,509,056` bytes;
- swap: `0.091` seconds;
- finalization: `33.076` seconds;
- archive: `215,681,438` bytes, SHA-256
  `be09743bd0a7df75b23749a556e545a35250615471a992173af20494adf11606`;
- isolated PostgreSQL 17 restore peak: `3,563,223,444` bytes;
- build WAL: `87,449,520` bytes;
- build temporary data: `12,574,720` bytes;
- final report SHA-256:
  `a989df042f17125c8d817a4f76961a06b50ad18e71dcd3624a1158ade0b96358`.

The final children contain `62,649`, `27,569`, `16,553`, `26,396`, `22,007`,
`14,485`, and `20,509` rows for snapshots `1302`, `1303`, `1304`, `1305`,
`1306`, `1307`, and `1309`; the default child is empty. Candidate/original
retained fingerprints, publication/reference state, and exact public songs and
rankings-overview bodies matched. All accepted relations and indexes are in
`pg_default`, no `sgm_pd_*` artifact remains, and the network-none restore
container and transient PGDATA were removed. The live API monitor recorded
`8` successful samples and zero failures. The authoritative recovery package
remains under
`/home/sfenton/fst-temporary/snapshot-generation-pro-drums-20260823T101944Z`
until a separate deletion decision.

### Accepted generation-aware validation scrape 1304

Run-once worker image `fstservice:snapshot-generation-a682a16c` completed
scrape `1304` from 2026-08-18 23:55 UTC through normal worker exit on
2026-08-19 20:50 UTC:

- all `8,448` solo and band scope manifests completed, all `603,015`
  persisted page statuses were `success`, and no scope exhausted retries;
- the network phase took `4,972.372` seconds (`82.873` minutes), `3.46%`
  above the accepted `80.1`-minute scrape `1303` baseline and within the
  `10%` gate;
- pro bass routed `1,395,539` rows into
  `leaderboard_entries_snapshot_pro_bass_s1304`, which occupies
  `726,654,976` bytes; its full tree is now `2,940,837,888` bytes;
- pro guitar routed `3,674,245` rows into
  `leaderboard_entries_snapshot_pro_guitar_s1304`, which occupies
  `2,013,806,592` bytes; its full tree is now `6,087,860,224` bytes;
- the corresponding published source rows total exactly those child row
  counts; pro bass has `254` nonempty `1304` sources plus two empty scopes,
  and pro guitar has `509` `1304` sources;
- both default children remained empty in all `1,214` monitor samples;
- all `6,336` published solo scope sources are complete, publication `92`
  advanced scrape `1304` to current, public reads unfroze, and representative
  songs, rankings, pro-bass, and pro-guitar routes returned HTTP `200`;
- notification runs `220` and `221` completed with `67` player and `665` band
  events; the post-publication drain claimed `40` backfill accounts,
  completed history reconstruction for `17` accounts, inserted `789`
  sessions, and issued `65,728` history API calls;
- the worker exited `0`, durable worker status is offline, no worker query
  remains, and registered backfill/history queues are empty.

Publication preparation required two deferred retries before attempt three
committed. Publication-critical projection cleanup and precompute completed.
Rank-history, band-rank-history, and service-level retention cleanup were
safely skipped because three active vacuums and `16,124,112` watched dead
tuples tripped the database-pressure guard; they were not counted as scrape
failures. FST free space moved from `774,941,876,224` to approximately
`753.7` GB, with a `63,718,576,128`-byte peak transient excursion.

The terminal evidence package is:

```text
/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/post-pro-guitar-scrape-20260818T235135Z
```

### Accepted generation-aware validation scrape 1305 and OOM recovery

Scrape `1305` retained the accepted `800/32/4` network profile and reached its
durable core checkpoint with `704` songs, `40,764,011` entries, `409,088`
requests, `59,082,543,837` received bytes, `8,448/8,448` complete manifests,
and `6,336/6,336` complete publication sources. The first worker attempt
completed BandMaintenance and rankings, then the unbounded 45-account Rivals
fan-out OOM-killed PostgreSQL backends. PostgreSQL recovered without a
postmaster restart, public reads stayed fail-closed on publication `92` /
scrape `1304`, and the worker exited before durable failure isolation could be
recorded.

The accepted recovery implementation:

- bulk-loads all target accounts once per instrument with source-selection
  parity, then reuses those score rows for combo counts, rivals, samples,
  dirty comparison, and selection-state fingerprints;
- defaults `Scraper:RivalsMaxDegreeOfParallelism` to `2`;
- adds cancellable bulk reads and cancellation checkpoints;
- permits an already-completed notification marker while a later candidate
  holds the freeze;
- adds a guarded `scrape-resume` profile requiring the exact stalled/updating
  candidate, positive resume metrics, `SoloRankings`, correctness gates, and
  the exact account cap.

The resume used image `fstservice:rivals-resume-20260820`, publication catalog
94, and no network/writer phases. Accepted timings were:

| Phase | Duration |
|---|---:|
| Early shadow activation | `00:01:16.898` |
| ComputeRankings | `00:43:18.419` |
| Rivals | `02:44:52.950` |
| LeaderboardRivals | `03:15:09.542` |
| PlayerStatsTiers | `00:00:03.847` |
| Cleanup.SoloCurrentProjection | `00:14:48.451` |
| Cleanup.PrecomputeAll | `00:08:56.755` |
| Publication preparation | `00:04:13.823` |

Rivals preloaded `42,682` score rows in nine queries and `300,551.174` ms,
then completed all 45 accounts without another OOM. Leaderboard Rivals
completed all `765/765` user/instrument/method scopes for 17 users. Temporary
PostgreSQL memory headroom was increased during the live recovery as measured
working sets grew, then restored to the production `16 GiB` memory /
`20 GiB` memory-swap limits after worker exit; cgroup OOM/OOM-kill counters
remained `9/2` throughout the resume.

Publication `94` became ready at `2026-08-20 20:20:56 UTC` and current at
`20:21:14 UTC`. Notification recovery completed with `62` player and `107`
band events. The post-publication drain completed history reconstruction for
17 users, inserted 10 sessions, issued 1,380 API calls, reached zero queued
registered backfills/history, and exited the worker with code `0`.

Published scrape-1305 source rows with physical snapshot `1305` match the
generation children exactly:

| Instrument | Published rows | Physical child rows | Default rows |
|---|---:|---:|---:|
| Pro Bass | `1,691,233` | `1,691,233` | `0` |
| Pro Guitar | `4,089,613` | `4,089,613` | `0` |
| Solo Guitar | `5,632,637` | `5,632,637` | `0` |

All 13 publication-critical outcomes completed. Three retention phases were
explicitly skipped as best-effort under vacuum/maintenance pressure. Public
service-info, songs, features, and rankings-overview routes returned HTTP 200;
publication 1305 is current and unfrozen.

Terminal evidence is under:

```text
/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/post-solo-guitar-scrape-20260820T041909Z/resume-20260820T125414Z
```

### Accepted generation-aware validation scrape 1306

The official `070daf14` run-once worker completed scrape `1306` from
`2026-08-21 03:10:42 UTC` through normal worker exit at `15:23:57 UTC` using
the exact `800/32/4` network profile and Rivals account cap `2`:

- `707` songs, `40,799,586` leaderboard entries, `603,829` requests, and
  `92,108,177,594` received bytes;
- all `8,484` scope manifests complete, all `603,829` persisted page statuses
  `success`, zero retry-exhausted scopes, and zero manifest failures;
- all `6,363` expected published solo sources validated and mapped, with zero
  missing rows;
- all 13 publication-critical phase outcomes completed; the three retention
  phases were skipped safely under the database-pressure guard;
- cgroup OOM/OOM-kill counters remained `9/2`.

Published scrape-1306 source rows with physical snapshot `1306` match the
generation children exactly:

| Instrument | Published rows | Physical child rows | Default rows |
|---|---:|---:|---:|
| Pro Bass | `1,738,972` | `1,738,972` | `0` |
| Pro Guitar | `3,484,122` | `3,484,122` | `0` |
| Solo Guitar | `5,227,744` | `5,227,744` | `0` |
| Solo Vocals | `5,380,894` | `5,380,894` | `0` |

Accepted phase timings were:

| Phase | Duration |
|---|---:|
| BandMaintenance | `03:17:40.426` |
| ComputeRankings | `01:12:43.369` |
| Rivals | `00:11:30.081` |
| LeaderboardRivals | `05:21:43.486` |
| PlayerStatsTiers | `00:00:03.854` |
| Cleanup.SoloCurrentProjection | `00:15:56.821` |
| Cleanup.PrecomputeAll | `00:13:20.339` |
| Publication preparation | `00:04:32.781` |

Band current projection selected and published `13,118/54,301` impacted
scopes, wrote `23,458,737` rows, deleted `23,471,872` old rows, and recorded
zero failures. Rivals preloaded `1,960` score rows for nine accounts in
`296,148.203` ms and completed with max degree `2`. Leaderboard Rivals
completed all 18 users without another OOM.

Publication `96` became ready at `2026-08-21 15:21:20 UTC` and current at
`15:21:38 UTC`; the exclusive publication lock lasted `3,609.567` ms with
zero lock rejections or retries. Notification recovery completed with `77`
player and `142` band events. Post-publication work completed a four-account
backfill (`19` entries, `45` API calls) and one-user history reconstruction
(`5` sessions, `792` API calls); the cyclical worker reported zero active
attachments and exited `0`.

Public service-info, songs, features, and rankings-overview routes returned
HTTP `200`. FST free space after terminal worker exit was
`1,590,535,684,096` bytes. Terminal evidence is under:

```text
/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/post-solo-vocals-scrape-20260821T031004Z
```

### Accepted generation-aware validation scrape 1307

The official
`ghcr.io/sfenton/fstservice:sha-070daf14e01a0961ad7261d7ed3ec79d58265919`
run-once worker completed scrape `1307` from `2026-08-21 20:27:10 UTC`
through normal worker exit at `2026-08-22 09:06:13 UTC` using the exact
`800/32/4` network profile and Rivals account cap `2`:

- `707` songs, `40,844,329` leaderboard entries, `604,390` requests, and
  `92,184,020,371` received bytes;
- all `8,484` scope manifests complete, all `604,390` persisted page statuses
  `success`, zero retry-exhausted scopes, and zero manifest failures;
- all `6,363` published solo sources complete across all nine instruments,
  with zero incomplete rows;
- all 13 publication-critical phase outcomes completed; the three retention
  phases were skipped safely under the database-pressure guard;
- cgroup OOM/OOM-kill counters remained `9/2`.

Published scrape-1307 source rows with physical snapshot `1307` match the
generation children exactly:

| Instrument | Published rows | Physical child rows | Default rows |
|---|---:|---:|---:|
| Pro Bass | `1,652,632` | `1,652,632` | `0` |
| Pro Guitar | `3,463,985` | `3,463,985` | `0` |
| Solo Guitar | `4,761,707` | `4,761,707` | `0` |
| Solo Vocals | `4,910,955` | `4,910,955` | `0` |
| Solo Drums | `4,842,176` | `4,842,176` | `0` |

Accepted phase timings were:

| Phase | Duration |
|---|---:|
| BandMaintenance | `03:00:20.460` |
| ComputeRankings | `01:13:19.294` |
| Rivals | `00:45:06.674` |
| LeaderboardRivals | `05:21:13.566` |
| PlayerStatsTiers | `00:00:04.109` |
| Cleanup.SoloCurrentProjection | `00:18:42.782` |
| Cleanup.PrecomputeAll | `00:12:22.111` |
| Publication preparation | `00:05:18.013` |

Band current projection selected and published `11,184/54,258` impacted
scopes, wrote `19,868,524` rows, and recorded zero failed scopes.
Leaderboard Rivals completed all `810/810` user/instrument/method scopes.

Publication `98` became ready at `2026-08-22 09:03:45 UTC` and current at
`09:03:53 UTC`; the exclusive publication lock lasted `3,514.100` ms with
zero lock rejections or retries. Notification recovery completed player run
`226` with `111` events and band run `227` with `92` events. The
post-publication drain completed two registration backfills with `14` entries,
zero history sessions, and `45` API calls. No backfill or history
reconstruction remained in progress, no worker query or lock waiter remained,
and the worker exited `0`.

Public readiness, service-info, songs, features, and rankings-overview routes
returned HTTP `200`. Publication `98` is current and unfrozen. FST free space
after terminal worker exit was `1,994,932,432,896` bytes. Terminal evidence is
under:

```text
/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/post-solo-drums-scrape-20260821T202634Z/terminal
```

The post-`solo-drums` validation gate is accepted. Solo Bass was subsequently
migrated under that worker hold. The worker remains offline, and a complete
post-Solo Bass guarded scrape is required before selecting or migrating
another instrument.

### Failed Solo Bass validation attempt 1308

The first master-image validation attempt collected all `6,363` solo scopes,
flushed all `194,623` band pages, and completed all `2,121` band manifests.
It then failed the writer gate because one 13-row Solo Bass page was retained
for replay. Concurrent first-batch partition creation for different
instruments selected the same truncated inherited-index relation name and
raised SQLSTATE `23505` on
`leaderboard_entries_snapshot_solo_bass_s1308`.

The failure remained fail-closed: no post-scrape phase or publication ran,
publication `98` stayed authoritative, reads unfroze, and the run-once worker
exited normally. The six `1308` generation children and the retained replay
artifact remain forensic candidate evidence, not accepted publication state.
The retry image must acquire one global generation-DDL advisory lock in a
separate statement before calling the partition helper. Only a complete retry
with exact physical-child/published-source parity clears the Solo Bass gate
and authorizes the final three-instrument migration interval.

### Accepted Solo Bass validation retry 1309

Candidate commit `b3b72e9b` completed scrape `1309` from
`2026-08-22 18:04:41 UTC` through normal worker exit at
`2026-08-23 06:32:15 UTC` using the exact `800/32/4` network profile and
Rivals account cap `2`:

- `707` songs, `40,882,964` leaderboard entries, `604,907` requests, and
  `92,257,894,447` received bytes;
- all `8,484` manifests complete, all `604,907` persisted page statuses
  `success`, zero retry-exhausted scopes, and zero writer failures;
- all `6,363` publication sources complete across all nine instruments;
- all 13 publication-critical phase outcomes completed; the three retention
  phases were skipped safely under the database-pressure guard;
- cgroup OOM/OOM-kill counters remained `9/2`.

Published scrape-1309 source rows match the six generation children exactly:

| Instrument | Published rows | Physical child rows | Default rows |
|---|---:|---:|---:|
| Pro Bass | `1,642,317` | `1,642,317` | `0` |
| Pro Guitar | `3,995,405` | `3,995,405` | `0` |
| Solo Guitar | `5,460,593` | `5,460,593` | `0` |
| Solo Bass | `4,489,655` | `4,489,655` | `0` |
| Solo Vocals | `5,351,184` | `5,351,184` | `0` |
| Solo Drums | `5,360,563` | `5,360,563` | `0` |

Accepted phase timings were:

| Phase | Duration |
|---|---:|
| BandMaintenance | `03:28:43.161` |
| ComputeRankings | `02:25:55.782` |
| Rivals | `00:16:13.199` |
| LeaderboardRivals | `04:21:19.178` |
| Cleanup.SoloCurrentProjection | `00:19:15.808` |
| Cleanup.PrecomputeAll | `00:12:56.409` |

Publication `101` became ready at `2026-08-23 06:25:39 UTC` and current at
`06:30:48 UTC`. Bounded final-swap retries encountered autovacuum relation
locks; a checksummed 90-second operator window canceled only autovacuums on
the current/prepared swap relations. The commit then completed with
`22.764` ms drain, `4,068.376` ms exclusive lock, and zero final lock
rejections or relation-lock retries.

Notification recovery completed player run `228` with `84` events and band
run `229` with `71` events. The post-publication drain completed one
registration backfill with `8` entries, zero history sessions, and `45` API
calls. No backfill/history worker, worker query, or lock waiter remained, and
the worker exited `0`.

Public readiness, service-info, songs, features, and rankings-overview routes
returned HTTP `200`. PostgreSQL was restored to the production `16 GiB`
memory / `20 GiB` total envelope, and FST free space was
`2,382,491,947,008` bytes. Terminal evidence is under:

```text
/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/post-solo-bass-retry-20260822T180344Z/terminal
```

The Solo Bass gate is accepted. Pro Vocals, Pro Cymbals, and Pro Drums
subsequently passed their complete migration state machines in the same worker
hold. One complete scrape across all nine migrated instruments was required
immediately afterward and is recorded below.

### Accepted all-nine validation scrape 1310

Candidate image `fstservice:generation-ddl-lock-b3b72e9b` completed scrape
`1310` from `2026-08-23 10:31:46 UTC` through normal worker exit at
`20:58:28 UTC` using the exact `800/32/4` network profile, snapshot reuse, and
Rivals account cap `2`:

- `707` songs, `40,907,090` leaderboard entries, `605,239` requests, and
  `92,307,778,607` received bytes;
- all `8,484` manifests complete, all `605,239` persisted page statuses
  `success`, zero retry-exhausted scopes, and zero writer failures;
- all `6,363` publication sources complete across all nine instruments, with
  `40,839,226` published rows and `45` authoritative empty sources;
- all publication-critical phase outcomes completed; the three retention
  phases were skipped safely;
- the worker exited `0` with zero restarts and no worker OOM;
- PostgreSQL cgroup OOM/OOM-kill counters remained `9/2`.

Published scrape-1310 source rows match all nine generation children exactly:

| Instrument | Published rows | Physical child rows | Source scopes | Default rows |
|---|---:|---:|---:|---:|
| Pro Bass | `1,577,901` | `1,577,901` | `298` | `0` |
| Pro Guitar | `3,818,765` | `3,818,765` | `510` | `0` |
| Pro Vocals | `4,988,523` | `4,988,523` | `648` | `0` |
| Pro Cymbals | `45,323` | `45,323` | `229` | `0` |
| Pro Drums | `19,725` | `19,725` | `139` | `0` |
| Solo Guitar | `5,190,179` | `5,190,179` | `531` | `0` |
| Solo Bass | `4,478,546` | `4,478,546` | `473` | `0` |
| Solo Vocals | `5,254,600` | `5,254,600` | `535` | `0` |
| Solo Drums | `5,369,892` | `5,369,892` | `557` | `0` |

Accepted phase timings improved over scrape `1309`:

| Phase | Scrape 1310 | Scrape 1309 |
|---|---:|---:|
| BandMaintenance | `03:07:27.912` | `03:28:43.161` |
| ComputeRankings | `01:23:27.226` | `02:25:55.782` |
| Rivals | `00:14:01.358` | `00:16:13.199` |
| LeaderboardRivals | `03:34:30.898` | `04:21:19.178` |
| Cleanup.SoloCurrentProjection | `00:15:18.546` | `00:19:15.808` |
| Cleanup.PrecomputeAll | `00:09:37.619` | `00:12:56.409` |

Publication `103` became ready at `2026-08-23 20:55:36 UTC` and current at
`20:55:52 UTC`. One deferred retry completed automatically with `7.630` ms
drain, `2,509.567` ms exclusive lock, zero lock rejections, and zero relation
lock retries. No operator cancellation was required.

Notification recovery completed player run `230` with `101` events and band
run `231` with `47` events. The post-publication drain found no queued
registration backfills and completed in `6.888` seconds; no backfill or history
reconstruction remained active.

The 309-sample correctness monitor recorded zero readiness, web, or
representative-leaderboard HTTP failures. PostgreSQL memory limits were
temporarily raised during LeaderboardRivals as cgroup file cache approached
successive ceilings; anonymous memory and swap remained bounded, OOM counters
did not change, and the production `16 GiB` memory / `20 GiB` total envelope
was restored after worker exit. Final FST free space was
`2,701,792,727,040` bytes.

Checksummed terminal evidence is under:

```text
/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/post-all-nine-scrape-20260823T102812Z/terminal
```

The all-nine migration gate is accepted. Normal scheduling remains held until
recurring destructive generation retention is restore-tested, documented, and
accepted. The report-only control plane does not satisfy that gate.

## Current protected-source expectation

Validation scrape `1310` is current and `1309` is previous. Publication
generations `103` and `101` currently resolve to those scrapes. Observed
pro-bass, pro-guitar, solo-guitar, solo-bass, solo-vocals, solo-drums,
pro-vocals, pro-cymbals, and pro-drums source maps reuse physical IDs `1302`
through `1310`. That is planning evidence, not a hard-coded retention list.

For each instrument, `plan` independently derives and groups IDs from:

1. `leaderboard_snapshot_state.active_snapshot_id`;
2. `solo_current_projection_scope.source_snapshot_id`;
3. `leaderboard_published_scope_source.source_snapshot_id` for publication
   generations named by
   `scrape_publication_state.current_publication_id`,
   `previous_publication_id`, and `working_publication_id`.

The stage fails closed if a named publication does not resolve, a named
snapshot source is null/nonpositive, the protected set is empty, or any
protected ID is absent from that instrument. It does not retain an arbitrary
number of recent completed scrapes and does not protect source maps belonging
only to unnamed historical publication generations.

An active or projection snapshot reference may legitimately have no physical
row only for an authoritative empty scope. The current publication must contain
an `alltime` source with `source_kind=empty`, null physical snapshot ID, zero
rows, and complete status. The ready projection must independently contain
zero rows with `source_kind=snapshot`; for active-state validation its source
snapshot ID must equal the active snapshot ID. The plan fingerprints the
current empty-source count and content across swap, validation, and rollback.
Missing either side of this evidence, or any malformed empty source, fails
closed. The complete planned reference JSON is rechecked under the migration
advisory locks before swap DDL, after normal or resumed swap, and inside the
final-drop transaction before the destructive decision.

## Required live gates

Every stage rechecks the fixed host and database identity. Do not continue if
any gate fails.

- PostgreSQL is the exact `fst-postgres` container in project
  `festivalservicetracker`, from the production Compose working directory.
- PostgreSQL 17 is healthy and its `data_directory` is inside the single
  read-write `/var/lib/postgresql/data` bind mount beneath
  `/mnt/docker-storage`.
- Container ID/image, PGDATA source/device, database OID, system identifier,
  and top-parent OID still match the `check` report.
- `fstworker` is stopped and durable worker status is offline/stopped/idle.
- No scrape or scrape phase is running.
- Public reads are unfrozen, current and previous publication IDs exist,
  working publication is null, and publication/max-score mutation intents are
  empty.
- There are no waiting locks, worker/scrape backends, competing maintenance
  backends, or locks on the top parent/current target.
- The parent is exactly `LIST (instrument)` in `pg_default`; the selected
  partition is attached with its fixed bound.
- No `sgm_*` artifact from another instrument exists. A rollback candidate
  intentionally blocks starting another instrument until it is reconciled.
- Representative `/api/songs` and `/api/rankings/overview` body/header
  fingerprints remain exact; readiness/service-info routes remain HTTP 200
  with the same content type.

The initial production `check` also requires a clean repository checkout. The
workspace marker binds the commit and SHA-256 of the migration/drill entry
points, so changing code requires a new workspace.

## Archive and storage rules

Before any source drop, the selected original is streamed to a PostgreSQL
custom archive on `/dev/nvme2n1p2`. The archive package contains:

- the exact parent and selected instrument only;
- archive SHA-256 and byte count;
- `pg_restore -l` TOC with the selected table, table data, primary key, and
  score index;
- the source catalog;
- source OID/relfilenode/heap/index/total bytes and insert/update/delete
  counters before and after the stream;
- the protected fingerprint and publication/database identity.

The before/after fence must be unchanged. The archive is then restored into a
deterministic, run-owned, `--network none` PostgreSQL 17 container. The restore
must prove the complete snapshot-ID distribution, whole-archive fingerprint,
protected distribution, source catalog, cleanup of transient PGDATA, container
removal, and continued archive checksum.

The archive survives `drop`. Deletion is a separate retention decision outside
this migration. Never count retained archives as reclaimable bytes.

## Replacement shape

`build` runs only after the archive restore proof and uses `pg_default`
directly. For the derived protected set, it creates:

```text
<instrument partition> PARTITION BY LIST (snapshot_id)
├── <instrument partition>_s<protected ID>
├── ... one child for every and only protected ID
└── <instrument partition>_default   (empty)
```

The root has a partitioned primary key on
`(snapshot_id, song_id, instrument, account_id)` and a partitioned score index
on `(snapshot_id, song_id, instrument, score DESC)`. The fixed instrument
check allows the short-lock top-parent attach to avoid rescanning retained
data. Validation requires both root indexes to attach to the corresponding
top-parent indexes and every root/child relation to resolve to `pg_default`.

Before detaching the original, `swap` adds and validates a run-owned exact
instrument check while the source is still attached to its known instrument
bound. The detached original retains that check, so rename-back rollback can
reattach without a full-table validation scan. Rollback removes the temporary
check only after the original is attached again.

Check validation runs during `build`, outside the short swap transaction, with
the configured long build timeout. `swap` only reverifies the already-validated
constraint before taking the top-parent lock.

Only protected rows are copied. The original remains attached throughout the
long copy/index work.

## Recurring retention ownership

The subpartition layout prevents future reclaim from requiring another
instrument-wide rewrite, but it does not prevent growth unless obsolete
children are actively retired.

A follow-up retention package must:

1. derive protected IDs from current/previous/working publication source maps,
   active snapshot state, and current projection sources;
2. inventory exact generation children and require the default child to remain
   empty;
3. create a custom archive for each nonempty unprotected child, including its
   heap/index/catalog/checksum and source ownership;
4. restore-prove that archive in isolated PostgreSQL before any live drop;
5. drop one exact child under short lock/statement timeouts, no `CASCADE`, and
   verify parent/index/publication/API parity plus returned filesystem bytes;
6. retain the archive until a separate product-retention decision.

Empty generation children may be dropped without a data archive only after
their exact zero-row and zero-reference state is recorded. No numeric
“latest-two” rule is sufficient because snapshot reuse can keep older physical
IDs behind current or previous publications.

## Capacity and emergency cancellation

Scratch preflight budgets 2.20 times source bytes for the custom archive plus
its independent final-drop recovery copy, 1.25 times source bytes plus 10 GiB
for isolated restore PGDATA, and a fixed 20 GiB scratch reserve.
Before recalculating free space, `drop` removes only run-owned, regular
same-directory `.partial-*` recovery-copy files left by an interrupted copy
and fsyncs each affected directory.

The 4 TB build model uses the accepted pro-bass live profile with fixed
conservative margins:

- replacement: 1.50 times proportional retained source bytes, minimum 64 MiB;
- WAL: 1.50 times replacement, minimum 512 MiB;
- temp: 0.75 times replacement;
- failure reserve: one replacement;
- emergency 4 TB floor: `60,392,999,803` bytes.

Only one instrument is built at a time. A filesystem monitor samples through
archive, restore, and build. Crossing the scratch reserve or the 4 TB floor
writes `reports/emergency-floor-breach.json`, cancels/terminates only the
migration application backends (or stops the owned restore container), and
durably blocks that workspace. Do not delete or edit breach evidence; reconcile
PostgreSQL/WAL/storage and start a new run.

## Stages

| Stage | Mutation | Result |
|---|---|---|
| `check` | none | Claims the empty workspace, captures host/database/publication/API identity, and classifies the fixed source. |
| `plan` | none | Derives exact protected IDs, protected fingerprints/reference parity, source catalog/fence, and archive capacity. |
| `archive` | scratch only | Writes custom archive, checksum, TOC, catalog, and unchanged source fence. |
| `restore` | isolated scratch only | Restores in network-none PostgreSQL 17, validates all archived rows/catalog, then removes transient PGDATA/container. |
| `build` | creates detached 4 TB candidate | Copies only protected rows into exact generation children plus empty default; builds compatible partitioned indexes. |
| `swap` | short-lock DDL | Validates the original instrument check, detaches/renames the original, attaches the candidate, and writes committed-swap evidence with the real duration. |
| `validate` | none | Proves retained fingerprints, references, publication/API parity, child/index catalog, archive, and `pg_default`. |
| `rollback` | short-lock DDL | Accepts committed-swap evidence even if the terminal swap report tore, reattaches the checked original without a full scan, removes its temporary check, and retains the failed candidate. |
| `drop` | destructive DDL | Requires accepted validation and pre-drop API parity, then holds both advisory fences while rechecking publication, protected IDs, target fingerprint, original identity/archive reproof, and destructive DDL; normalizes names and revalidates `pg_default`/API/archive. |

No stage uses `CASCADE`.

## Operator sequence

Create one new empty workspace per instrument on the authorized scratch
filesystem. The run ID and path below are examples; substitute the fixed key
being processed and a unique timestamp.

```bash
cd /home/sfenton/FortniteFestivalLeaderboardScraper

instrument=solo-guitar
run_id="snapshot-generation-${instrument}-20260818T120000Z"
scratch="/home/sfenton/fst-temporary/${run_id}"
mkdir -m 700 "$scratch"
device_id="$(findmnt -T "$scratch" -n -o MAJ:MIN)"

common=(
  --instrument "$instrument"
  --scratch-root "$scratch"
  --expected-device-id "$device_id"
  --run-id "$run_id"
)

tools/postgres-snapshot-generation-migration.sh \
  check "${common[@]}" \
  --claim-workspace \
  --api-base "<service-base-url>"

tools/postgres-snapshot-generation-migration.sh plan "${common[@]}"
tools/postgres-snapshot-generation-migration.sh \
  archive "${common[@]}" --execute
tools/postgres-snapshot-generation-migration.sh \
  restore "${common[@]}" --execute
tools/postgres-snapshot-generation-migration.sh \
  build "${common[@]}" --execute
tools/postgres-snapshot-generation-migration.sh \
  swap "${common[@]}" --execute
tools/postgres-snapshot-generation-migration.sh \
  validate "${common[@]}" --api-base "<service-base-url>"
```

At this point choose exactly one path.

Accepted finalization:

```bash
tools/postgres-snapshot-generation-migration.sh \
  drop "${common[@]}" \
  --execute \
  --api-base "<service-base-url>"
```

Rename-back rollback before `drop`:

```bash
tools/postgres-snapshot-generation-migration.sh \
  rollback "${common[@]}" \
  --execute \
  --api-base "<service-base-url>"
```

Do not start another instrument until the current target has either completed
`drop` with no migration artifacts or rollback artifacts have been separately
reconciled. Recheck 4 TB and scratch capacity from the next target's reports;
do not extrapolate the previous instrument.

## Resumption and evidence handling

Each success report is typed, dependency-checksummed, integrity-hashed, written
atomically in the workspace filesystem, and fsynced with its directory.
Archive/build start evidence is durable before long work begins.

If a final stage report is zero-length or malformed after a process
interruption, the next invocation moves it to `recovered-evidence/`, records a
recovery proof, inspects the archive/database state, and reconstructs the
report only when the committed state is exact. Complete restore validation and
cleanup evidence is reused; a partial restore evidence set is preserved before
the isolated restore is repeated. Valid JSON with a failed integrity hash is
not treated as a torn write and blocks automatically.

`swap.committed.json` is separate from the terminal stage report. It is written
immediately after the DDL transaction with the actual elapsed time and
duration-bound decision. A catalog-swapped state without measured committed
evidence is rollback-only; rerunning `swap` cannot replace the lost duration
with a near-zero idempotent measurement.

PostgreSQL statistics counters are not durable identity: crash recovery can
reset them. Stable rollback identity uses OID/relfilenode and physical sizes.
If mutation counters differ from the plan, rollback recomputes the complete
original fingerprint and per-snapshot distribution and requires exact equality
with the isolated restore report before reattachment.

Final drop keeps one transaction open from reproof through DDL. Target and
original relations are held in read-compatible `SHARE` mode while complete
fingerprints and per-snapshot distributions are recomputed, and a five-second
public API monitor runs beside the scan. The PostgreSQL session pauses at a
decision boundary with those locks still held; any API-monitor failure rolls
the transaction back. Monitor shutdown must be confirmed; a join timeout or
still-running probe is also a failure. Only after a clean, stopped monitor does
the same session re-read and exactly compare the complete root and per-child
bound/default, tablespace, owner, column/default/nullability, constraint,
leaf/root-index (including every index tablespace), and parent-index-attachment
catalog under those locks. It then
acquires the advisory fences, rechecks the unfrozen publication and protected
IDs, and executes the short DDL. The resulting complete shape/catalog is
validated again inside the uncommitted transaction before `COMMIT`.
The transaction explicitly disables
`idle_in_transaction_session_timeout` locally because the locked decision
interval includes API-monitor shutdown and archive verification.

Before the long reproof starts, `drop` binds the run ID, plan ID, archive,
manifest, plan/archive/restore/validation reports, and their SHA-256 chain. It
creates independent byte-for-byte recovery copies of every bound file, verifies
their checksums, makes them read-only, and opens both source and recovery files
under kernel read leases. Each recovery inode also has an anchor inside a
read-only recovery-package directory, so removing or replacing the working
copy cannot orphan the verified bytes. Scratch capacity is checked before
copying. A checksummed `drop.recovery.json` binding the run, plan, archive, and
all recovery paths is itself copied and anchored by a checksum-bearing file
name before destructive DDL. The package includes copied check, plan, archive,
restore, and validation reports, so recovery never needs mutable working
metadata. After the API monitor stops, the tool rechecks source/recovery path
identity, metadata, complete archive checksum, manifest checksum, and report
chain before allowing phase two. A removed or replaced path rolls the database
transaction back while the independent recovery package remains intact.

After phase-two DDL but before `COMMIT`, a second decision boundary rechecks the
report/manifest hash chain, source and recovery inode/size/link-count/timestamp
identity, and whether any writer requested a lease break. A same-inode writer
that starts after this last check is kernel-blocked until the destructive
transaction, post-commit archive/recovery checks, API/catalog validation, and
durable `drop.json` report are complete. The report names the independent
archive copy as authoritative; the original path may change after leases are
released without destroying recovery. Recovery copies remain governed by the
same separate archive-deletion decision. If the process dies after `COMMIT`
but before `drop.json`, the next `drop` invocation recognizes the finalized
catalog, loads the pre-commit recovery manifest, verifies the anchored recovery
archive and copied lifecycle reports, and reconstructs the terminal report
without trusting the potentially changed original archive path or working
reports. Terminal `drop.json` dependencies point to the copied evidence.
At report publication, the tool revalidates and, when needed, repairs each
writable recovery-copy path from its read-only anchor. The checksum-addressed
recovery manifest has working and anchored names, so renaming the package
directory cannot strand recovery or invalidate terminal dependency paths.
Resumed recovery does not require the original package directory to remain at
its initial name.

Never edit reports, manifests, source fences, checksums, or workspace markers.

## Validation package

Structural tests:

```bash
bash -n \
  tools/postgres-snapshot-generation-migration.sh \
  tools/postgres-snapshot-generation-migration-drill.sh

PYTHONDONTWRITEBYTECODE=1 \
  python3 tools/postgres-snapshot-generation-migration.test.py
```

Isolated PostgreSQL 17 lifecycle:

```bash
PYTHONDONTWRITEBYTECODE=1 \
  python3 tools/postgres-snapshot-generation-migration-drill.py \
  --work-root \
  artifacts/snapshot-generation-migration-drills/<new-empty-run>
```

The drill runs independent rollback, guarded final-drop, write-lease/torn-
commit recovery, recovery-publication, and package-rename lanes. It proves
custom
archive/TOC, network-none restore and cleanup, exact protected children plus an
empty default, top-parent index attachment, rename-back rollback, final drop,
archive retention, and torn success-report recovery for archive, restore,
build, swap, validate, and drop. The rollback lane force-kills/restarts
PostgreSQL, resets cumulative statistics, and requires complete archive
fingerprint/distribution reproof before the original can be reattached.
The final-drop lane adds an unexpected child-local `NOT VALID` check both
before `validate` and after accepted validation, proving neither the
independent build contract nor the locked catalog guard can bless it. It also
removes/replaces the archive at both the pre-DDL and post-DDL/pre-commit
decision boundaries, proves that neither attempt commits destructive DDL,
restores the original path from the independent recovery copy, and then
completes the accepted drop. The write-lease lane starts a same-inode archive
writer at commit entry, proves the kernel blocks it through `COMMIT`, kills the
migration before `drop.json`, lets the writer corrupt the original, and proves
the next invocation reconstructs the committed drop solely from the anchored
recovery package. The publication lane proves its read-only anchor cannot be
unlinked and repairs the authoritative working path after it is replaced by a
hard link to the source. The package-rename
lane renames the complete anchor directory at report entry and proves the
process can be killed there and the next invocation still publishes from the
revalidated working archive and copied terminal dependencies.

The implementation validation on 2026-08-18 used 1,400 synthetic source rows
per lane (`1301` purge; `1302-1303` retained), PostgreSQL `postgres:17`, and
completed all five lanes without a production connection or mutation.
Synthetic
sizes and timings are correctness evidence only, not production performance
estimates.

`SnapshotGenerationPartitionTests` additionally proves that dropping one
generation child removes only that snapshot, leaves another generation
readable through the unchanged top parent, keeps the default child empty, and
preserves both remaining leaf-index attachments. This is layout evidence only;
it is not the recurring archive/drop owner described above.
