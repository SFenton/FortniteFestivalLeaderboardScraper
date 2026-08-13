---
status: canonical
owner: service
last_verified: 2026-08-12
last_verified_commit: 41c3bdb4
sources:
  - FSTService/Scraping/PathGenerationCoordinator.cs
  - FSTService/Scraping/PathArtifactResolver.cs
  - FSTService/Scraping/PathDataStore.cs
  - FSTService/Api/SongEndpoints.cs
  - FSTService/Api/AdminEndpoints.cs
  - FortniteFestivalWeb/src/pages/songinfo/components/path/PathDataTable.tsx
  - packages/core/src/api/serverTypes.ts
  - tools/chopt-cli-linux/README.md
  - .github/workflows/publish-image.yml
update_triggers:
  - CHOpt version, path profile, JSON schema, artifact storage, generation scheduling, path endpoints, text rendering, or regeneration procedure changes.
---

# Path generation

FST uses the `SFenton/CHOpt` fork to calculate optimal Fortnite Festival paths,
render PNG diagrams, and export structured JSON for the browser. The image and
JSON for one chart come from the same CHOpt invocation and are promoted
together as an immutable generation.

## Generation flow

1. FST downloads the encrypted Festival MIDI `.dat` file and verifies its
   content hash.
2. The configured MIDI key decrypts the chart in a private staging directory.
3. CHOpt runs once for each expected instrument and each of `easy`, `medium`,
   `hard`, and `expert`, using the `fnf` engine, zero early whammy, and 20%
   squeeze.
4. FST validates every PNG and JSON artifact. Expert scores must be positive.
   PNGs may be up to 32,768 pixels on either axis, while the independent
   256 MiB decoded-image limit still rejects oversized or compressed-bomb
   payloads. This accommodates the longest current Festival charts.
5. A manifest records the song identity, `.dat` hash, CHOpt version and binary
   SHA-256, generation profile, expected instruments, and expert maxima.
6. The complete directory is moved into
   `paths/<songId>/generations/<generationId>/` and promoted with a
   compare-and-swap update. Partial or conflicted attempts never replace the
   current generation.

The generation profile is a semantic identity, not a display label. Change it
whenever CHOpt arguments or the artifact contract change. A version, binary
hash, or profile mismatch makes a selected song non-skippable.

## JSON contract

Profile `chopt-fnf-ew0-s20-json-png-v2` requires JSON `schemaVersion: 2`.
Every `activations[]` entry has one authoritative `instruction` plus:

- image-range `startBeat`/`endBeat` and seconds;
- activation-point beat and seconds;
- exact score and Overdrive immediately before activation;
- anchor beat/seconds and beats-after-anchor metadata;
- optional legacy `startNotes` only when the activation actually begins on a
  scoring note.

The activation count and instruction count are therefore one-to-one, including
mid-sustain and delayed activations. `startNotes` is supplementary note data
and must never be used as the activation inventory. `odAtActivation` is a
fraction in the inclusive range `0..1`; the browser converts it to a
percentage. `beatsAfterAnchor` is present only when the activation point is
inside a sustain.

Legacy schema-v1 artifacts remain readable. Their `pathSummary` is the
compatibility source for one instruction per activation; missing score or
Overdrive metadata is shown as unavailable rather than silently dropping the
activation. Legacy fret pills are shown only when the activation is on a note
or inside a sustain that identifies its anchor; the browser does not guess
from an unrelated earlier note.

## HTTP and browser behavior

The publication-bound routes are:

- `GET /api/paths/{songId}/{instrument}/{difficulty}` for the PNG;
- `GET /api/paths/{songId}/{instrument}/{difficulty}/data` for JSON.

An optional `generationId` query must equal the song's current generation.
The browser's Text view renders exactly one row per activation, using schema-v2
structured fields when present and the legacy `pathSummary` fallback otherwise.

`POST /api/admin/regenerate-paths` is protected by `X-API-Key` and requires one
`songId`. `force=true` is for bounded canaries. Catalogue regeneration must
submit songs sequentially with `force=false`; the v2 profile makes the run
idempotent and resumable while already-promoted v2 songs skip.

## Deployment and regeneration

The Linux CLI and runtime libraries are pinned under
`tools/chopt-cli-linux/`. Any change in that directory must rebuild the
FSTService image. Update the bundled README, source commit, version, SHA, and
license manifest together.

Deploy the schema-v2 binary and the `-v2` profile in the same service image.
The profile-derived validator accepts legacy `-v1` output only when the
configured profile remains `-v1`; switching the profile is the atomic
fail-closed contract and regeneration trigger.

Before a canary or full regeneration, apply
[Live safety](../operations/live-safety.md):

1. require healthy Docker, PostgreSQL, API, and web roles;
2. require an idle scrape, unfrozen public reads, no path generation, no
   blocking locks/long queries, and adequate FST-drive/CPU/memory headroom;
3. retain the prior immutable generations and snapshot canary metadata;
4. regenerate a representative matrix spanning sustain-heavy vocals, pad and
   pro strings, drums, multiple difficulties, and known delayed activations;
5. require one instruction per activation, matching PNG/JSON generation
   identity, the expected CHOpt version/hash/profile, zero generation errors,
   and accepted expert-score/path parity;
6. stop on unexplained maximum-score changes because maxima feed ranking and
   leaderboard validity calculations;
7. run the catalogue sequentially and preserve a resumable state manifest on
   the 4 TB FST drive;
8. verify the named canaries and catalogue-wide schema/profile counts before
   returning the worker to normal operation.

Rollback keeps the prior immutable generation directories. Do not delete them
until the new catalogue, rankings implications, and browser parity are
accepted.
