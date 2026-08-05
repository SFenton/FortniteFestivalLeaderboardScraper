# Retired Physical Schema Cleanup Runbook

## Decision

**Tier:** package prepared; execution blocked.

This repository contains the exact cleanup package for four retired physical
schema families. It has **not** been run against PostgreSQL. It is eligible
only after cleanup scrape `1278` completes, publishes, unfreezes public reads,
passes exact public/API fingerprint parity, and that parity is explicitly
accepted in the attestation consumed by the tool.

The package is:

- `tools/postgres-retired-schema-cleanup.sh`
- `tools/postgres-retired-schema-cleanup.py`
- `tools/sql/postgres-retired-schema-cleanup/`

Check mode is the default and is read-only. Execute requires both
`--execute` and the exact SHA-256 from a separately accepted check:

```bash
tools/postgres-retired-schema-cleanup.sh \
  --check \
  --output /mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/<check-package> \
  --parity-evidence /mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/<scrape-1278-evidence>/retired-schema-parity-acceptance.json

tools/postgres-retired-schema-cleanup.sh \
  --execute \
  --expected-manifest-sha256 <accepted-check-manifest-sha256> \
  --output /mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/<execute-package> \
  --parity-evidence /mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/<scrape-1278-evidence>/retired-schema-parity-acceptance.json
```

Use a new output directory for each command. The script requires the production
compose directory `/home/sfenton/Docker/FestivalServiceTracker` and verifies
that PostgreSQL data and all evidence remain on the 4 TB FST drive.

## Exact scope

`objects.tsv` is the only drop allowlist. It contains 61 relations: 59 tables
or partitioned tables, one view, and one sequence.

| Ownership family | Exact relations | Count |
|---|---|---:|
| Logical shadow | `leaderboard_current_entries` plus 9 leaves; `leaderboard_entry_versions` plus 9 leaves; `leaderboard_logical_write_metrics` | 21 |
| Score observations | `player_score_observation_union`; `player_score_observations_id_seq`; `player_score_observations` | 3 |
| Optional band-song projection | `band_song_team_rankings`; its 3 current-band tables; `band_song_team_ranking_state` | 5 |
| Aggregate ranking deltas | `ranking_deltas` plus 9 leaves; `ranking_delta_tiers` plus 9 leaves; `rank_history_deltas` plus 9 leaves; `composite_ranking_deltas`; `combo_ranking_deltas` | 32 |
| **Total** |  | **61** |

Indexes, constraints, TOAST relations, defaults, sequence ownership, and
partition attachments are inventoried and hashed as owned objects. Sequence
ownership is also scanned inversely from every target table column through
`pg_depend`, including both automatic and internal ownership. The exact
allowlist contains only
`public.player_score_observations_id_seq -> public.player_score_observations.id`;
any additional or differently named owned sequence blocks cleanup. Owned
objects are not separate wildcard drop targets.

Two allowlisted tables are intentionally nonempty:

- `leaderboard_logical_write_metrics`: exactly 108 historical audit rows;
- `band_song_team_ranking_state`: exactly 3 rebuild-state rows.

Check mode exports both tables in deterministic primary-key order, canonicalizes
the CSV payloads, and binds their row counts, byte counts, SHA-256 values,
under-lock equality SQL, and rollback `COPY` payloads into the manifest. Export
and restore columns are generated from a separately canonicalized complete
column catalog: ordinal/name, type/type OID/typmod, default, nullability,
identity/generated state, collation, storage/compression, inheritance/missing
state, statistics target, options, FDW options, and ACL. Any extra, missing, or
drifted column blocks before data comparison. Every other allowlisted table or
partition must be exactly empty. The retained rows are explicitly rebuildable
state, but they are never silently truncated or dropped without an identical
manifest-bound payload.

The package deliberately excludes all active current-state and physical-source
families, including:

- `current_leaderboard_entries*`
- `leaderboard_entries_snapshot*`
- `leaderboard_entries_overlay*`
- `score_history*`
- `current_band_leaderboard_entries*`
- `band_team_rankings_current_*` and `band_team_rankings_published_*`
- base `account_rankings`, `rank_history*`, `composite_rankings`,
  `composite_rank_history*`, `solo_family_rankings`, and `combo_leaderboard`
- all audit/maintenance tables not named in the 61-row allowlist

Generic matching on `Delta`, `RankDelta`, `current`, `history`, or `ranking`
is forbidden.

## Retained evidence

The package verifies the existing same-drive evidence before it can become
ready:

| Evidence | SHA-256 |
|---|---|
| `retired-schema-baseline.txt` | `f83ba535997a6a1c5a3cad80c715eb9bece7a3cefc382d31d2fbcf2540bf879f` |
| `retired-schema-external-dependencies.txt` | `07e6cb6eb173f2a8cef6b340e001ca091815855207b6a9396c88a5780233968c` |
| `retired-schema-rollback.sql` | `35f0df8e9d3f1e24dd2ab0c019b1b53a5adfdc0c7fa00736c6608a3011aa7aba` |
| `ranking-deltas/catalog-baseline.tsv` | `d6ae264202b2b91207676a35710f76299dec1042618fd3f642025b56785d2f5c` |
| `ranking-deltas/rollback-schema.sql` | `1e5529ede22b48cbf8950fccef15582b6c8717da02fc53f92f764e14546c43f7` |

Root:

`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/branch-cleanup-20260803/`

The two retained rollback dumps overlap on 22 ranking relations. The
orchestrator therefore also creates fresh, non-overlapping schema-only
`pg_dump` files for each ownership family. Each raw dump remains
available byte-for-byte as raw evidence, including its random PostgreSQL 17
`\restrict`/`\unrestrict` key. Before acceptance, a parser requires exactly one
matching boundary pair and rejects every other psql meta-command, including
`\!` and `\connect`.

Execution uses a separately generated bounded copy. It preserves the original
random restriction keys and all SQL byte-for-byte except the four verified
pg_dump timeout preamble assignments: statement `30s`, lock `5s`, idle
transaction `60s`, and transaction `5min`. The parser requires each original
zero timeout exactly once and proves none survive in the executable copy.
Scratch restore, rehearsal, and operator restore use only this bounded copy.

Separate **digest-only, never-executed** files additionally replace only the
random boundary key so manifest hashes remain deterministic. Sequence state
and the exact 108-row/3-row retained payloads remain separately hashed data SQL
concatenated after the bounded dump; the raw dump is never modified or run.

Every `pg_dump` rollback capture uses `--lock-wait-timeout=5s`, a 30-second
catalog statement timeout through `PGOPTIONS`, and an outer two-minute process
timeout. The sequence-state query has a two-second lock timeout, 15-second
statement timeout, and 30-second process timeout. Timeout or capture failure
records a failed rollback hash and prevents execute readiness.

## Parity attestation

The cleanup tool does not infer human acceptance merely because scrape `1278`
is published. `--parity-evidence` must be a same-drive JSON file with:

```json
{
  "schemaVersion": 1,
  "decision": "accepted",
  "scrapeId": 1278,
  "published": true,
  "unfrozen": true,
  "exactPublicFingerprintParity": true,
  "fingerprintCount": 13,
  "cleanupImageId": "sha256:<accepted-running-image-id>",
  "fingerprintSpecSha256": "<accepted-request-spec-sha256>",
  "acceptedAtUtc": "<accepted-evidence-time>Z",
  "evidenceRoot": "/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/<scrape-1278-evidence>"
}
```

Use
`tools/sql/postgres-retired-schema-cleanup/parity-acceptance.example.json` as
the shape only. Do not create an accepted attestation until the full scrape,
publication, unfreeze, and exact parity evidence actually pass.
The fingerprint-spec hash and count bind acceptance to the same exact
13-surface request/normalization suite that check and execute recapture.

## Check-mode evidence and gates

Check mode:

1. Verifies the production compose directory and FST-drive data/evidence paths.
2. Runs the zero-scratch reclaim capacity guard.
3. Captures PostgreSQL, service, web, worker, `/readyz`, web-shell, and
   `/api/service-info` health plus Docker CPU/memory diagnostics.
4. Requires scrape `1278` to be completed and published, public reads
   unfrozen, no working publication, no running scrape, and no failed
   publication-critical phase.
5. Requires `fstworker` stopped with restart policy `no`, pinned to the same
   cleanup image as `fstservice`, and its durable ledger absent or `offline`
   with no current operation.
6. Captures ungranted locks, long queries, active target queries, vacuum/index
   progress, and rewrite-like activity.
7. Inventories every exact relation, relkind, owner, partition parent,
   sequence owner/value, and total bytes. Fifty-seven table/partition objects
   use a bounded exact-zero probe. The two retained-state tables require exact
   counts of 108 and 3 plus canonical payload SHA-256 values.
   Capture staging temp tables are created before `BEGIN TRANSACTION READ
   ONLY`; they use `ON COMMIT PRESERVE ROWS`, then are populated and read
   entirely inside one read-only transaction/snapshot. No capture performs
   temp DDL after entering read-only mode.
8. Captures every direct `pg_inherits` child of all five partitioned parents,
   regardless of child name or schema, and requires exact set equality with
   the 45 allowlisted leaves. Any custom-named attached child blocks.
   A second inventory captures every incoming `pg_inherits` edge for all 61
   targets: standalone tables and partitioned parents must be parentless, while
   each leaf must have exactly its allowlisted parent. Any external parent,
   extra parent, or detach-in-progress state blocks.
9. Rejects missing or extra matching relations.
10. Rejects external views/materialized views, routines, non-internal triggers,
   foreign keys, rules, policies, or effective publication membership. The
   publication scan uses `pg_publication_tables`, not only
   `pg_publication_rel`, and records whether membership comes from `FOR ALL
   TABLES`, `FOR TABLES IN SCHEMA`, or an explicit table entry, together with
   effective columns and row filters.
11. Captures internal indexes, constraints, partition bindings, TOAST objects,
    and every inverse table-column-to-sequence ownership edge. The captured
    owned-sequence multiset must exactly equal the single allowlisted
    observation sequence edge; missing, duplicate, identity-owned, or
    custom-named extra sequences block.
12. Builds a complete canonical catalog signature covering relations and
    columns, constraints, indexes, all triggers, policies/RLS, target and
    dependent view/rule definitions, effective publications (including
    all-table/schema membership), sequence definition/ownership/state,
    partition keys/bounds/attachments, direct dependencies, and matching
    routine definitions.
13. Searches each repository runtime source/config root and the actual
    production compose project separately for retired object names. Production
    ownership includes every discovered raw compose YAML/override, a
    purpose-built sanitized projection of `docker compose config --format
    json`, the resolved bind inventory, and every bounded nonsecret
    bind-mounted config file. Environment values, label values, secret bind
    paths, and secret contents are never persisted; only retired-name matches
    are emitted from raw files. `rg` exit `0` and `1` are accepted; exit `2+`,
    render failure, a missing root, unreadable relevant bind, or a config hash
    changing during scan fails closed. The manifest records every successful
    root, rendered-config hash, raw compose hash, bind classification, and
    nonsecret bind-config hash.
    The file list comes from every running project container's
    `com.docker.compose.project.config_files` label. All containers must agree
    on project, working directory, and ordered list. Every render, service-ID
    lookup, initializer, and validation uses that exact ordered
    `docker compose -f <base> -f <override> ...` sequence, preserving
    production-only overrides such as PIA image and bind routing.
    Every gate additionally compares the actual `postgres`, `fstservice`,
    `fstworker`, and `festivalweb` container configuration to that resolved
    model. Environment names and nonsecret-value hashes, secret presence,
    password-free database targets, explicit command/entrypoint hashes, mounts,
    restart policy, Compose labels/file-list hash, networks, IPs, and required
    aliases are attested without persisting credentials.
    `fstservice` and `fstworker` must target the sanitized Compose
    host/port/database/user. The service must share a network where `postgres`
    is an alias of the exact container ID whose local-socket system identifier
    is maintenance-bound; stale services cannot fingerprint a different DB.
    Every running endpoint on each network shared by `fstservice` and Postgres
    is enumerated, including nonproject containers. The configured database
    hostname must have exactly one alias owner on each shared network, and that
    owner must be the attested Postgres ID. A stale clone is rejected even when
    it publishes no host ports.
    Tests, docs, evidence tools, and rollback packages are retained separately
    as an audit rather than treated as runtime owners.
14. Captures the proven 13-fingerprint public suite: leaderboard raw and
    semantic views; solo list/player/history; composite; band list/team/history/
    songs/song rows; and full/solo normalized player exports. The account and
    band team keys are derived from the captured ranking lists, and the exact
    resolved sample URLs are manifest-bound. `/readyz`, web-shell, and
    `/api/service-info` remain separate health evidence.
15. Generates exact drop SQL, per-family rollback DDL, a combined rollback
    file, hashes the orchestrator/helper/capture SQL, and emits a deterministic
    `manifest.json`.

Dynamic capture timestamps, free-byte counters, and CPU/memory samples remain
in evidence but are intentionally outside the deterministic manifest. Object
shape, sizes, publication pointers, image IDs, parity evidence, public
fingerprints, source ownership, and rollback hashes are inside it.

The capacity gate is not environment-configurable from this package. Before
each check, execute revalidation, and post-check, the orchestrator unsets
`ACTION_CLASS`, all transient/scratch/reclaim overrides, the full-scrape growth
estimate, expected daily scrape count, headroom days, and byte override. It
then passes the pinned policy explicitly: reclaim action, expected reclaim
`0`, transient/scratch `0`, emergency full-scrape window `60,392,999,803`
bytes, two runs/day, seven days, and byte override `0`. The full guard JSON is
retained. A separate canonical policy artifact binds those effective values
and the SHA-256 of `tools/postgres-capacity-guard.sh` into the cleanup manifest;
execute and post-action validation rerun and compare that exact policy.

## Execute behavior

Execute rebuilds the complete check package from current state and refuses to
continue unless its manifest SHA-256 exactly equals
`--expected-manifest-sha256`. It then repeats publication, worker, health,
capacity, image, and fingerprint checks immediately before DDL.

The generated SQL:

- takes the nonblocking global publication maintenance lock;
- starts one atomic transaction for all 61 objects;
- owns the documented FST schema-DDL advisory namespace
  `5067481511116519501` and the retired-sequence namespace
  `5067481511116519502`; package rollback rehearsal uses the same guards;
- never rewrites ownership to acquire a lock. After all target table locks, it
  nonmutatingly reads the allowlisted sequence relation and takes `FOR SHARE`
  row locks on every relevant original `pg_depend` ownership tuple (all target
  sequences plus all target table columns). Those tuple locks prevent an
  ownership row from being deleted/reassigned while validation runs;
- compares that locked inverse ownership multiset against the allowlist before
  any destructive ownership statement. A sequence already reassigned to an
  active column, an unexpected owned sequence, missing ownership, or identity
  ownership blocks without being normalized;
- after ownership validation, acquires the retired-sequence transaction
  advisory guard, reads the sequence relation, and takes `FOR SHARE` tuple
  locks on its `pg_class` and `pg_sequence` catalog rows. These
  nonmutating locks block `ALTER SEQUENCE ... RESTART` and option changes.
  PostgreSQL exposes no supported nonmutating sequence lock that conflicts with
  direct `nextval`/`setval`, so those retired mutation paths are protected by
  the held transaction advisory guard `5067481511116519502`, stopped worker,
  removed runtime references, and active-query gate. Any manual sequence
  mutation must acquire that guard;
- recaptures the manifest-bound `last_value`, `is_called`, type, start,
  increment, minimum, maximum, cache, cycle, and ownership state after those
  locks, then repeats the complete signature immediately before destructive
  statements while all locks remain held;
- after those locks, queries `pg_publication_tables` and rejects every
  effective target membership, including `FOR ALL TABLES` and `FOR TABLES IN
  SCHEMA`, before the first catalog-signature comparison;
- runs the scrape/publication/freeze/worker/contention gate once before taking
  target locks, so cleanup-created public waiters cannot cause a later partial
  family commit;
- locks all five partitioned parents first with a 5-second lock timeout, then
  proves their complete attached-child sets equal the allowlist;
- locks every remaining table before the first drop;
- immediately recaptures the complete manifest-bound catalog signature after
  the final target lock and rejects any column, constraint, index, trigger,
  policy/RLS, view/rule, sequence, partition, dependency, or routine drift;
- repeats that complete signature comparison and the effective-publication
  membership rejection at the last possible database statements before the
  first drop. External DDL that ignores the advisory
  namespace cannot be universally excluded, so the stopped worker, removed
  writers, table/view locks, advisory guard, sequence relation/catalog locks,
  locked ownership tuples, bound sequence state/options, and final recheck fail
  closed on observed sequence/routine drift;
- rejects catalog states that a schema-only dump cannot faithfully recreate,
  including dropped/missing-value columns, invalid/not-ready/not-live indexes,
  `indcheckxmin`, and pending partition detach state;
- rejects every target with `relrowsecurity` or `relforcerowsecurity` enabled.
  All table-count, emptiness, retained-data, scratch-proof, rehearsal, and
  destructive probes set `row_security=off`; policy enforcement therefore
  errors instead of hiding rows. The pinned runtime role must attest either
  superuser or `BYPASSRLS` privilege before the package can become ready;
- uses a 30-second statement timeout, 5-minute transaction timeout, and
  60-second idle transaction timeout;
- exact-zero-checks the 57 empty table/partition objects and compares the two
  retained tables bidirectionally against typed manifest payloads under lock;
- rejects missing, inexact, incorrectly owned, wrongly attached, unexpected,
  or retained-data-drifted objects;
- only after all ownership/catalog/publication gates pass, removes the expected
  sequence default/ownership and drops that exact sequence in the fixed
  destructive order before its owner table;
- commits all four ownership families together or rolls all four back;
- uses no wildcard drops, `IF EXISTS`, or cascading clause.

Dependency-safe order is fixed:

1. logical-shadow leaves, parents, then metrics;
2. observation union view, observation column default/sequence ownership,
   sequence, then table;
3. exact band-song tables;
4. ranking-delta leaves before each partitioned parent, then the two standalone
   aggregate tables.

Family drop markers are diagnostic only and occur inside the uncommitted
transaction. Only `FST_ALL_COMMITTED` proves the single atomic commit; there is
no supported partial-family completion state.

## Post-action validation

After the one all-family transaction commits, execute must pass every step:

1. All 61 relations are absent.
2. The startup initializer is created stopped through the local Docker Engine
   API from the manifest-attested `fstservice` container configuration, with
   `Image` set to the immutable `sha256:<image-id>` from the sealed manifest.
   It never resolves the mutable Compose tag to create the container, never
   publishes ports, never receives the `fstservice` network alias, disables
   restart/auto-remove, and uses the exact initializer command.
3. Before `docker start`, the stopped container's actual image ID, configured
   image ID, source-service configuration hash, networks, command, PID-zero
   created state, and manifest/source container IDs are fsynced to evidence.
   The Compose image reference is resolved both before creation and immediately
   before this attestation; a retag to any other image aborts while the
   initializer remains unstarted. Only the exact attested container ID is then
   started. Its post-exit image IDs and exit status are rechecked before
   explicit removal.
4. Immediately before release, every manifest-bound initializer network is
   re-enumerated without trusting Compose labels. The configured `postgres`
   name must have exactly one attached resolver owner on each network—including
   stopped containers—across endpoint `Aliases`, endpoint `DNSNames`, and the
   container name itself. That owner must be the manifest-bound running
   Postgres container. The initializer additionally receives an exact
   `/etc/hosts` entry for the approved Postgres container's primary-network IP;
   the routing gate requires that IP still belongs to the same container before
   start. A fresh local-socket query must also reproduce the
   approved system identifier, database, user, port, non-recovery state, and
   container target. The routing and target evidence are fsynced into the
   initializer release record. Any drift leaves the initializer unstarted,
   removes it, reconciles the committed drop state, and writes failure and
   operator-only recovery evidence.
5. All 61 relations remain absent after startup initialization.
6. The freshly captured original rollback DDL plus separately generated data
   SQL is executed inside one bounded transaction,
   the complete catalog/column signature, all 61 relkinds, and the exact
   108-row and 3-row retained payloads are verified before rollback.
7. All 61 relations remain absent after the rollback rehearsal.
8. Public/API HTTP statuses and canonical fingerprints exactly match the
   approved pre-action manifest.
9. Published scrape/current publication remain unchanged, reads remain
   unfrozen, no working publication or scrape appears, and the worker remains
   stopped/offline.
10. PostgreSQL/service/web health, the capacity guard, database bytes,
   filesystem bytes, and target bytes are recaptured.
11. The complete evidence directory receives `package-checksums.sha256`.

No minimum byte reclaim is claimed: nearly all tables are already empty and
the only data removed is the exact manifest-bound 108-row audit payload and
3-row rebuild-state payload. The post-action package records measured bytes.

## Failure and rollback

The orchestrator never restores automatically. Any failure writes
`FAILED.txt`, the atomic commit state, and `ROLLBACK-INSTRUCTIONS.txt`.

Before an explicit restore:

1. Keep the worker stopped and publication fixed on scrape `1278`.
2. Inventory all 61 objects. A failed drop transaction leaves all present; a
   committed drop leaves all absent.
3. Verify `rollback-all.sql`, both retained payload hashes, and generated
   family hashes.
4. Restore `rollback-all.sql` using `psql --single-transaction` only when all
   61 objects are absent.
5. Re-run schema, startup, publication, health, and public fingerprint checks.

Do not apply `rollback-all.sql` to a partially present schema. Do not use a
cascading clause. Do not restart the worker until the restored or cleaned state
is accepted.

All shared and destructive `docker exec ... psql` calls use
`PGCONNECT_TIMEOUT=10` and an outer seven-minute timeout with a 30-second kill
grace, exceeding the five-minute database transaction bound. If the destructive
client exits nonzero or times out, the orchestrator performs bounded catalog
reconciliation. It continues only when all 61 objects are proven absent and
then still requires every post-action validation; all-present is recorded as a
rolled-back failure and mixed/unknown state is a hard stop.

The destructive client uses a unique `application_name` and records its local
timeout PID, container-side `psql` PID, PostgreSQL backend PID, and state in
`post/drop-process-control.csv`. Timeout, `ERR`, `INT`, `TERM`, or `HUP`
cancels and then terminates only that exact backend/client, waits until both
are gone and the transaction has ended, and only then permits reconciliation.
Unidentified or still-active processes forbid both all-present and all-absent
acceptance.

Container client discovery uses `/proc/<pid>/exe` with basename exactly
`psql` plus an exact `application_name=<run-id>` argv token. The scanner's own
shell/parent PIDs and differently named control connections are excluded;
substring matches are forbidden. Ambiguous discovery never returns early:
the already recorded backend, container client, and local child are still
cancelled/terminated and waited, while final ambiguity remains a hard failure.

Launch uses a three-stage pipe barrier. A local coprocess waits before starting
`docker exec`; the orchestrator first records and fsyncs its PID, start ticks,
command hash, active state, and armed traps. After the local command identity
changes to the exact timeout/docker/application command, a container shell
still waits on a second `CONNECT` token. The connected `psql` receives only a
backend-PID probe, then blocks on stdin. Container and backend PIDs are
independently matched, recorded, and fsynced as
`post-connect-barrier-ready`; only then is `drop.sql` released through the
third gate. A signal at any barrier terminates and waits the exact local child
and polls for any late-arriving exact backend/client before catalog
classification; all-present can never clear active state for a client that
might later connect or receive SQL.

After the final live gate, the operator-approved `manifest.json` is opened once
with `O_NOFOLLOW`, checked for stable device/inode/size/mtime, required to match
the exact `--expected-manifest-sha256`, parsed from those captured bytes, and
sealed in a write/grow/shrink-protected memfd. Its `dropSqlSha256` then controls
a separate one-open stable read and sealed memfd capture of `drop.sql`.
A streamer fsyncs both source identities, hashes, sealed inode/size/seal masks,
then waits for an explicit release token. Only the sealed SQL bytes are
streamed; neither mutable pathname is reopened or trusted afterward.
Simultaneous replacement of manifest and SQL therefore cannot change execution.

The PostgreSQL target is resolved from the exact compose `postgres` service,
not an operator-selected name. Sanitized `POSTGRES_DB`/`POSTGRES_USER` and
service/worker connection host/port/database/user must agree. The resolved
container ID plus runtime database, user, port, recovery state, and PostgreSQL
system identifier are manifest-bound and reverified before execute,
reconciliation, initializer, and post-check. Explicit clone container,
database, or user arguments are rejected.

Every libpq client (`psql`, `pg_dump`, `pg_isready`, `createdb`, and `dropdb`)
is forced to `host=/var/run/postgresql` and the compose-attested port.
`PGHOST`, `PGHOSTADDR`, `PGPORT`, `PGSERVICE`, and `PGSERVICEFILE` overrides
are rejected on the host and truly removed inside the target container with
`env -u`; empty `docker exec -e NAME=` assignments are forbidden because
libpq treats empty service variables as configured values. Runtime attestation must
report `local-socket`; a remote cluster is rejected even if its database,
user, and system identifier otherwise match.

Before the destructive client starts, the package creates a uniquely named
scratch database in the same pinned PostgreSQL container/cluster, restores the
fresh schema plus retained 108/3-row payload, and runs the complete relkind,
retained-data, incoming-inheritance, and catalog-signature assertions. The
scratch database is then dropped. This pre-destructive proof must pass; the
post-drop rollback rehearsal remains an independent second proof.

Because the scratch proof may run for an extended window, it is not the final
operational gate. After `psql` connects and its container/backend identities
are fsynced—but while it still waits behind the SQL-release barrier—the package
recaptures the complete live safety state: HTTP health/fingerprints, pinned
capacity policy/report, service/worker container images and states,
publication/freeze/scrape/lock/query state, retained rows, complete relation/
column/constraint/index/dependency signature, storage identity, and production
target attestation. Every manifest-bound stable field must still match.
Validation JSON is hashed into the destructive process control record before
`drop.sql` is released; any drift closes the pipe and aborts without DDL.
This second gate includes the complete actual-container configuration
attestation, not only container ID/image/state.
The gate is invoked as a normal fail-fast command, never as `if ! function` or
an unchecked `||` list that disables Bash `errexit` inside functions. Every
capture, canonicalization, retained-data check, manifest comparison, evidence
write, and final artifact verification has an explicit failure return.
Success is possible only after the validation JSON is re-read, proves
`success=true`, binds the accepted manifest SHA, and matches its fsynced hash.

Trap handling never treats cleanup failure as success. Known container/local
clients are terminated even if the backend control query fails, and every
`ERR`/`INT`/`TERM`/`HUP` after destructive launch performs bounded,
target-attested catalog reconciliation before exit. The result is classified
as `committed`, `all-present`, `partial`, or `unknown`; only normal non-signal
execution with a confirmed-dead backend/client may accept `committed`.

The local timeout PID is bound to `/proc/<pid>/stat` start ticks and a command
line SHA-256. It is never signaled after `wait` succeeds, and a live PID is
signaled only when both identities still match, preventing PID-reuse kills.

## Repository-only validation

Repository validation uses generated fixtures, static SQL inspection, and an
isolated real PostgreSQL capture:

```bash
bash -n tools/postgres-retired-schema-cleanup.sh
python3 -m py_compile tools/postgres-retired-schema-cleanup.py
bash tools/postgres-retired-schema-cleanup.test.sh
```

The suite also starts an isolated `postgres:17` container with no published
ports and executes the real `capture-relations.sql` path, proving that temp
staging precedes the read-only transaction while row/catalog capture succeeds
inside one consistent PostgreSQL snapshot.

They cover argument parsing, deterministic manifests, arbitrary-named attached
partitions, exact retained 108/3-row payloads and rollback hashes, retained-data
and complete-column catalog drift, complete under-lock catalog signatures,
atomic transaction/concurrency boundaries, sequence/routine drift, bounded
rollback and psql process capture, post-timeout commit/rollback reconciliation,
sanitized production compose/raw override/bind ownership, `rg`
error/missing-root/render failures, ordered PIA override behavior,
project-container disagreement, clone-target rejection, delayed backend
`INT`/`TERM`/`HUP` commit-state classification, control-query failure,
incoming external-parent edges, non-restorable catalog states, scratch
round-trip failure, local-socket override/remote-target rejection, forced-RLS
hidden rows, missing RLS-bypass privilege, and PID reuse,
deterministic signal-at-launch barrier cleanup, unexpected/missing ownership
evidence, scanner self-match/prefix/control-process regressions, and
drift-during-scratch second-gate rejection,
including deterministic interruption after connect but before SQL release,
stale connection-string and Docker network/alias drift,
duplicate database aliases from running stale/nonproject containers,
retagged mutable service image during the post-drop startup check,
an alias owner appearing after initializer creation but before start,
container-name/`DNSNames` ownership without an explicit alias, exact
Postgres-IP host pinning,
manifest-bound system-ID/database-target recapture, and committed-drop
reconciliation/recovery evidence when that final routing gate fails,
injected failure at every complete-gate stage and Bash conditional-function
`errexit` regression,
concurrent `drop.sql` modification after immutable capture,
simultaneous manifest/SQL replacement after sealed capture,
dependencies, nonzero rows elsewhere, active scrape, frozen reads,
worker/source-reference gates, post-action validation, the exact 13-surface
fingerprint contract and normalizers, absence of cascading/optional drops,
inverse ownership discovery and rejection of a differently named sequence
owned by a target column, rejection of active-column ownership reassignment
without pre-validation normalization, guarded concurrent `nextval`/`setval`
and locked `ALTER SEQUENCE ... RESTART`/option mutation, effective
explicit/schema/all-table publication
membership capture and rejection, sequence handling, exact family counts,
child-before-parent order, and
exclusion of active relations. Crafted extra `\unrestrict`, `\!`, and
`\connect` dumps must fail before any executable rollback package is accepted.

## Current limitation

This package is prepared evidence, not clearance. It cannot certify that
scrape `1278` succeeded or that parity was accepted until the real attestation
and live check package exist. No production, Docker, PostgreSQL, API, or scrape
operation was run while preparing this repository change.
