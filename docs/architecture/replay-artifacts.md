---
status: canonical
owner: worker
last_verified: 2026-08-14
last_verified_commit: cb295b7e
sources:
  - FSTService/Scraping/Replay/TierZeroEvidenceModels.cs
  - FSTService/Scraping/Replay/TierZeroCanonicalJson.cs
  - FSTService/Scraping/Replay/TierZeroConfigurationFingerprinter.cs
  - FSTService/Scraping/Replay/TierZeroPackagePath.cs
  - FSTService/Scraping/Replay/TierZeroPackageWriter.cs
  - FSTService/Scraping/Replay/TierZeroPackageVerifier.cs
  - FSTService/Scraping/PhaseProgressCatalog.cs
  - FSTService.Tests/Unit/TierZeroEvidenceContractTests.cs
  - FSTService.Tests/Unit/TierZeroPackageTests.cs
  - FSTService/Scraping/Replay/ReplayCommand.cs
  - FSTService/Scraping/Replay/ReplaySecurity.cs
  - FSTService/Scraping/Replay/TierOneReplayModels.cs
  - FSTService/Scraping/Replay/TierOneReplayPackage.cs
  - FSTService/Scraping/Replay/TierOneReplayRunner.cs
  - tools/postgres-tier1-replay-drill.sh
update_triggers:
  - Evidence package format, canonicalization, hashing, path safety, artifact ownership, capture, import, replay, retention, or promotion behavior changes.
---

# Replay evidence artifacts

## Current boundary

Tier 0 defines an immutable evidence-package contract and filesystem
verification primitives. It does **not** capture a live scrape, export
PostgreSQL, import into an isolated database, invoke a phase, replay a phase,
publish data, or grant an artifact authority over public reads.

PR-4 and PR-5 are accepted repository contracts. No replay producer or
consumer is deployed in production. PostgreSQL remains the durable source of
truth.

## Ownership and location

Tier-0 packages are artifact/replay companions owned by their producer and
consumer workflow. Future production-derived packages, replay scratch, and
exports must remain on the 4 TB FST drive. Unit-test fixtures stay under the
repository test/session workspace and are bounded.

No automatic retention policy exists yet. A sealed package must not be
overwritten or deleted by generic scrape-log cleanup. A future capture or
replay implementation must define package admission, retention, capacity,
rollback, and lineage ownership before creating live artifacts.

### Caller-root policy

The PR-4 library confines package-relative operations beneath the root supplied
by its caller, but it does not choose or authorize that root. This is
intentional: no CLI or runtime entry point exists yet.

The accepted PR-5 root policy fails closed before package creation unless:

- a runtime root resolves beneath an operator-approved location on the 4 TB
  FST drive;
- a test root resolves beneath the repository or the explicitly assigned
  session-test workspace;
- the canonical root and every existing ancestor are free of symbolic links,
  reparse points, traversal, and normalization aliases; and
- the path is not on an alternate disk, a generic temporary directory, or a
  PostgreSQL data directory.

The root policy also binds every input/output path to the configured filesystem
device, rejects generic temporary and PostgreSQL-data paths, and requires
non-overlapping immutable attempts. Tests inject isolated roots directly; the
runtime path has no weakening flag.

## Tier-1 phase input

Format `fst.tier1.phase-input`, version `1`, is stored as a canonical artifact
inside a sealed Tier-0 envelope. It binds:

- a separately verified sealed Tier-0 parent root;
- the current stable phase-plan ID/version;
- stable phase `post.band_maintenance`;
- subphase adapter `current_projection_refresh`, version `1`;
- captured source PostgreSQL system identifier;
- dependency `post.band_extraction`;
- exact dataset IDs, paths, schema versions, row/byte counts, completeness
  statements, and SHA-256 hashes; and
- package, row, output, statement-timeout, and lock-timeout limits.

Protocol v1 accepts one band type and at most 16 unique overall scopes. The
typed allowlist contains only requested scopes, complete `band_entries`, and
complete `band_member_stats`. JSON Lines must be canonical UTF-8, keys must be
unique, member rows must be complete, and the package is rejected rather than
truncated.

The current replay adapter calls the production
`BandCurrentProjectionBuilder.RefreshScopesAsync` implementation directly with
`SkipUnchangedScopes=false`, one band-type worker, synchronous commit enabled,
local isolated generation publication enabled, and candidate cleanup disabled.
This is a deterministic current-projection refresh kernel, not full
BandMaintenance parity: prune, search projection refresh, incremental
unchanged detection, old candidate cleanup, global publication, freeze, cache,
notifications, and provider behavior remain unsupported.

These overrides intentionally differ from production. Output and comparison
format version `2` therefore require:

```json
{
  "productionComparableTiming": false,
  "timingComparisonReason": "Deterministic replay overrides differ from production: SkipUnchangedScopes=false, one band-type worker, synchronous commit enabled, and candidate cleanup disabled."
}
```

The fields are part of canonical hashing and verification. Replay elapsed/WAL/
resource deltas are drill diagnostics only and cannot be cited as production
wall-clock evidence.

## Isolated PostgreSQL target

Replay uses only `FST_REPLAY_POSTGRES_CONNECTION`. Before import and again
before output it verifies:

- one configured loopback endpoint and `fst_replay_*` database;
- a PostgreSQL system identifier different from the captured source cluster;
- absence of production publication, worker-status, and scrape-log tables;
- an exact bootstrap marker table, constraints, object allowlist, single row,
  package root, replay ID, database name, system identifier, and state; and
- writable default transactions for the bounded import/phase path.

Import is static parameterized DDL plus typed binary COPY. The package cannot
provide SQL, shell, table names, schema names, or configuration execution.
Only the marker bootstrap may exist before import. Marker states move through
`created`, `imported`, `phase-completed`, and `completed`; output failures mark
the isolated attempt failed and retain an unsealed failure package.

## Tier-1 output and comparison

A successful replay seals a Tier-0 output envelope parented to both Tier-0 and
Tier-1 input roots. Output format version `2` records phase/adapter,
implementation commit/image/config/schema identity, isolated database
identity, exact output datasets, row counts/hashes, timing, CPU/allocation/RSS,
WAL/temp deltas, `noPublication=true`, and the mandatory non-production timing
semantics above.

Output datasets are canonical projections, scope state, and projection-global
state with volatile timestamps excluded from parity. The trusted comparison
format version `2` requires exact expected digest, Git commit, OCI revision,
attempt, and `productionComparableTiming=false` for each lane. It reports
row/hash parity and diagnostic resource deltas, and fails even when replay
timing improves if any output differs.

`tools/postgres-tier1-replay-drill.sh` runs baseline and candidate against
separate fresh PostgreSQL 17 containers with no published ports, no provider
network, no Docker socket, non-superuser replay roles, candidate-inaccessible
PGDATA, read-only input/baseline mounts, lane-specific writable outputs, and an
immutable baseline comparator. It removes containers and PGDATA while
preserving only sealed evidence.

## Package layout

```text
<package>/
  package.lock
  package-state.json
  <artifact paths...>
  checksums.sha256
  manifest.json
```

- `package.lock` is a package-scoped exclusive filesystem lock retained as an
  empty system file. Every mutation and seal refreshes state while holding it,
  so separate writer instances cannot race artifact journals or checksum/
  manifest commitment. Only package creation may create/truncate the lock;
  existing and sealed package operations never repair a missing or corrupt
  lock.
- `package-state.json` is the pre-seal resume journal. It stores immutable
  producer/parent identity, registered artifact metadata, and at most one
  pending artifact descriptor/temporary path, never configuration values or
  artifact content. Sealing rewrites canonical state while holding the package
  lock and commits the SHA-256 of those exact on-disk bytes into the manifest.
- `checksums.sha256` contains artifact hashes in normalized path order. It does
  not include itself or the manifest.
- `manifest.json` is written last and is the seal marker.
- Temporary files use same-directory `.partial-<pid>-<guid>` names and are
  renamed atomically. Artifact commit first journals the pending descriptor,
  then renames content, then moves the descriptor into the committed artifact
  list. Resume validates and completes either the pending temporary-file or
  final-file state. A resumable unsealed package removes only strict
  unjournaled orphan temporary names and empty transaction directory
  scaffolding after immutable identity checks pass.

## Manifest identity

Format `fst.tier0.evidence`, manifest version `1`, records:

- package ID, attempt, producer, status/error, and UTC created/sealed times;
- source scrape/publication IDs and optional UTC source cut;
- exact catalog identity and SHA-256;
- git commit, OCI digest/revision, and service version;
- producer-supplied PostgreSQL major version, sorted extensions, and schema
  fingerprint;
- allowlisted configuration key names and a canonical key/value hash, without
  the values;
- phase-plan ID/version and the ordered descriptors projected directly from
  `PhaseProgressCatalog`;
- scope-manifest, scope-fingerprint, phase-outcome, and phase-timing summary
  references;
- artifact logical owner, normalized relative path, media/schema version, row
  count, safe min/max metadata, compressed/uncompressed bytes, and SHA-256;
- ordered parent root hashes;
- checksum-manifest identity and package root hash.
- exact pre-seal state-journal hash.

Artifact bytes are never embedded in the manifest.

## Canonicalization and hashes

Canonical JSON is UTF-8 without indentation. Object properties use ordinal
name order. Model collections with set semantics are normalized with ordinal
ordering before serialization; ordered phase descriptors retain catalog
ordinal order. Numbers use `System.Text.Json` invariant formatting and all
timestamps are normalized to UTC.

The package root hash is SHA-256 over the canonical manifest with
`packageRootHash` omitted. This avoids self-recursion while covering artifact
metadata, artifact hashes, checksum-manifest hash, parent lineage, build,
schema, configuration, phase plan, and source identity.

The hashes detect corruption and bind lineage but are not a digital signature.
A consumer that needs origin authentication must anchor the expected package
root or parent roots in a separately trusted channel.

Identical logical inputs, including timestamps, produce byte-identical manifest
and checksum files regardless of dictionary input order, filesystem order,
process culture, or path separator style.

## Path and secret safety

Artifact paths normalize Unicode to NFC and use package-relative forward-slash
paths. The contract rejects:

- absolute, drive-qualified, UNC, empty, parent-traversal, reserved, or
  duplicate-normalized paths;
- cross-platform-invalid segments, trailing dots/spaces, Windows device names
  including console aliases, Unicode/case-normalized aliases, and
  reserved-name descendants;
- package roots, directories, or files containing symbolic links/reparse
  points;
- FIFOs, sockets, devices, and any other non-regular artifact/system file;
- untracked files or directories, including otherwise empty directories;
- files resolving outside the package root.

Unix reads and the package lock use no-follow descriptors, validate the opened
file identity/type, and reject a path that changed after inventory. Linux
creation, directory traversal, rename, and deletion additionally use
`openat2`/descriptor-relative operations that reject symbolic-link ancestors
and keep mutations beneath pinned package directories. Verification also
repeats the full file/directory inventory before returning success. Package
directories remain single-producer workspaces; unrelated processes must not
mutate an unsealed package.

Configuration fingerprinting requires an exact, non-empty named allowlist. It
rejects non-allowlisted keys, missing/duplicate allowlist entries, secret-like
key names, scheme-qualified or bare DNS/IP/host-port endpoints,
credential-assignment values, authorization material, cookies, connection
strings, proxy/account keys, and arbitrary environment dumps.
Only sorted key names, algorithm, and the key/value hash enter the manifest.
Diagnostic errors and range metadata receive the same endpoint/credential
screening.

## Lifecycle

1. Create a new empty package directory with immutable draft identity.
2. Add artifact streams. Content is written and hashed to a same-directory
   temporary file; pending metadata is journaled before the final rename, then
   committed metadata clears the pending record.
3. Optionally mark an unsealed package interrupted with a safe error.
4. Resume only when package ID, attempt, producer, parent roots,
   configuration hash, schema fingerprint, and phase-plan identity match.
5. Seal as `sealed` or `failed`. Sealing validates artifacts and summary
   references and the exact file set, writes the canonical checksum atomically,
   then writes the manifest atomically as the final seal marker.
6. Reject further mutation or another attempt at the same directory. A new
   attempt uses a new directory and never overwrites prior sealed output.

## Verification

Verification returns typed failures rather than swallowing errors. It detects:

- unsealed/missing/invalid/noncanonical manifest state;
- root or checksum mismatch;
- missing, extra, modified, incorrectly sized, duplicate, or symbolic-link
  files and untracked directories;
- invalid paths and summary references;
- state-journal mismatch;
- any file identity or directory inventory change during verification;
- expected parent, configuration, schema, or phase-plan mismatch.

The verifier can validate internal package integrity without expectations, or
apply consumer-provided immutable expectations before a later import/replay
step. No such import/replay step exists yet.

## Validation fixture

`TierZeroPackageTests` creates a synthetic package containing small JSON, CSV,
and empty artifacts. An optional `FST_TIER0_FIXTURE_OUTPUT` path may preserve
that fixture only beneath the repository working directory. Tests cover
determinism, cultures, reordered inputs, large and empty count boundaries,
path/secret rejection, pending-artifact crash recovery, interruption/resume,
exact state-byte commitment, atomic sealing, corruption, empty-directory and
lineage mismatches, and stable phase descriptors without network or database
access.
