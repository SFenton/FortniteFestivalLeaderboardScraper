---
status: canonical
owner: service
last_verified: 2026-08-15
last_verified_commit: 739954f8
sources:
  - FSTService/Scraping/MidiTrackInspector.cs
  - FSTService/Scraping/PathGenerationCoordinator.cs
  - FSTService/Scraping/PathGenerationModels.cs
  - FSTService/Scraping/PathArtifactResolver.cs
  - FSTService/Scraping/PathDataStore.cs
  - FSTService/Scraping/GlobalLeaderboardScraper.cs
  - FSTService/Scraping/RankingsCalculator.cs
  - FSTService/Persistence/MaxScoreMaintenanceModels.cs
  - FSTService/Persistence/MaxScoreMaintenanceService.cs
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
   FST promotes `PLASTIC DRUM` or `PLASTIC DRUMS` to `PART DRUMS` in a
   dedicated MIDI variant and hides the pad `PART DRUMS` track. A `pd` song
   without the plastic track fails closed.
3. Expected instruments begin with property presence in Epic's raw intensity
   object. When any supported property is absent, FST decrypts and parses each
   named MIDI track, then augments only omitted instruments whose track contains
   a positive-velocity Note On event. Empty placeholder tracks and zero-velocity
   Note On events do not count. This inspection occurs before the
   unchanged-generation skip decision, so a prior generation cannot remain
   current after an omitted real chart is discovered.
4. CHOpt runs once for each expected instrument and each of `easy`, `medium`,
   `hard`, and `expert`, using the `fnf` engine, zero early whammy, and 20%
   squeeze. Plastic-drums charts generate two modes from Epic's `pd` chart:
   `Solo_PeripheralCymbals` uses the dedicated `prodrums` engine so cymbals
   score 42 and toms score 36, while `Solo_PeripheralDrums` also passes
   `--no-pro-drums` so all gems score 36. Both modes preserve double kicks and
   restrict activation starts to scoring gems nearest Epic's authored
   activation-window endpoints.
5. FST validates every PNG and JSON artifact. Expert scores must be positive.
   Plastic-drums expert artifacts must retain non-empty authored activation
   windows.
   PNGs may be up to 32,768 pixels on either axis, while the independent
   256 MiB decoded-image limit still rejects oversized or compressed-bomb
   payloads. This accommodates the longest current Festival charts.
6. A manifest records the song identity, `.dat` hash, CHOpt version and binary
   SHA-256, generation profile, expected instruments, and expert maxima.
7. The complete directory is moved into
   `paths/<songId>/generations/<generationId>/` and promoted with a
   compare-and-swap update. Partial or conflicted attempts never replace the
   current generation.

After promotion, `path_expected_instruments` supplements provider intensity
metadata for scrape admission and ranking chart denominators. This applies only
to a complete, non-pending immutable generation whose current song ID and
catalog `lastModified` identity still match the selected provider catalog.
Provider JSON remains unchanged and authoritative for catalog provenance; a
stale or identity-mismatched path generation cannot widen scrape scope.

The generation profile is a semantic identity, not a display label. Change it
whenever CHOpt arguments or the artifact contract change. A version, binary
hash, or profile mismatch makes a selected song non-skippable.

## JSON contract

Profile `chopt-fnf-ew0-s20-json-png-prodrums-v4` requires JSON
`schemaVersion: 2`.
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
The browser's Text view renders exactly one row per activation. It shows the
structured fret cue, beat, time, Overdrive, and score fields without exposing
raw CHOpt instruction notation.

`POST /api/admin/regenerate-paths` is protected by `X-API-Key` and requires one
`songId`. `force=true` is for bounded canaries. Catalogue regeneration must
submit songs sequentially with `force=false`; the current profile makes the
run idempotent and resumable while already-promoted songs skip.

After deploying a new stable instrument correction, regenerate each affected
song sequentially with `force=false`. Do not update nullable maximum columns
directly: the normal path promotes the complete immutable generation and keeps
the manifest, maxima, expected instruments, and revision coherent.

## Max-score correction maintenance

A reviewed correction to an already-published song's theoretical maximum uses
the CLI-only
[max-score correction runbook](../database/MaxScoreCorrectionMaintenanceRunbook.md),
not the generic admin endpoint.

`--max-score-maintenance-stage` requires a canonical version-2 request. A
discovery request binds the exact v4 runtime, eight generated instruments,
four approved changed instruments, known old-null/new-guitar constraints, and
cannot be planned or applied. A promotion request then binds complete old/new
eight-field maxima copied from discovery. Both stages acquire the distributed
path-generation lease, process songs serially, apply decrypted-MIDI inference,
write complete immutable generations, and never call the PostgreSQL pointer
promotion path.

Plan accepts only a promotion-purpose manifest. It revalidates both the
current rollback generation and staged generation as exact file trees with
SHA-256 identities, then rejects:

- a changed publication, catalog, provider timestamp, or path revision;
- an active staged generation;
- missing, extra, changed, or incoherent current/staged artifacts;
- known-invalid plastic-drums v3 current or staged state;
- a runtime/artifact identity mismatch;
- a nonpositive changed maximum;
- any non-null target current, staged, or request maximum above
  `RankingsCalculator.MaximumScoreWithRepresentableRankingCutoff`
  (`2,045,222,521`);
- a missing authoritative published score source or an observed score above
  `floor(newMaximum × 21 / 20)`, the exact `1.05` integer ranking validity
  cutoff (a score above the CHOpt denominator but within that cutoff remains
  valid);
- a plastic-drums cymbal mode below no-cymbal mode, empty authored activation
  windows, or a plastic note inventory matching `Solo_Drums`;
- a maximum difference omitted from `changedInstruments`; or
- any supposedly unchanged maximum that differs.

The target admission bound does not reject an unrelated catalog maximum while
plan evidence selects frozen publication inputs. General ranking-threshold
computation saturates such a cutoff at `int.MaxValue`, preserving the exact
result over the PostgreSQL `INTEGER` score domain and preventing an overflowing
SQL parameter. A target value at the same boundary still fails request,
actual-path, manifest, and report validation.

Apply promotes every manifest generation in one PostgreSQL transaction. It
does not expose the new path pointer until a digest-owned maintenance freeze is
active, and it does not release that freeze until all maximum-dependent
derived state, notification quarantine, current-publication caches, and
rollback evidence validate. Promotion refreshes cached scraper admission before
derived work. Any prior negative backfill result for a newly usable
song/instrument pair is removed and its account requeued, while positive and
unrelated completed pairs remain untouched. Freeze release invalidates scraper
admission again so registration-only/service roles reload the same-publication
path revision. The old exact-four command names remain rejected.

## Deployment and regeneration

The Linux CLI and runtime libraries are pinned under
`tools/chopt-cli-linux/`. Any change in that directory must rebuild the
FSTService image. Update the bundled README, source commit, version, SHA, and
license manifest together.

The profile-derived validator accepts v2, v3, and v4 as schema-v2 JSON, but v3
plastic-drums maxima are known invalid and are masked from serving/ranking and
rejected by max-score maintenance. Switching to v4 changes the binary, MIDI
variant, activation model, and expected instrument set. While a v2 or v3 song
is pending v4 regeneration, plastic-drums routes return unavailable rather
than falling back to stale or incorrect artifacts.

Before deploying the v4 service, run the existing
`--initialize-schema-only` one-shot with that image. It adds the nullable,
idempotent `max_pro_cymbals_score` and `max_pro_drums_score` columns before any
role queries or promotes the expanded path state.

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
   and accepted expert-score/path parity. Plastic-drums canaries additionally
   require observed leaderboard scores to remain below the applicable CHOpt
   maximum, non-empty authored activation windows, a note inventory distinct
   from `Solo_Drums`, and a cymbal-mode maximum greater than or equal to the
   no-cymbal maximum;
6. stop on unexplained maximum-score changes because maxima feed ranking and
   leaderboard validity calculations;
7. run the catalogue sequentially and preserve a resumable state manifest on
   the 4 TB FST drive;
8. verify the named canaries and catalogue-wide schema/profile counts before
   returning the worker to normal operation.

All current songs expose `pd`, so v4 adds 5,616 JSON and 5,616 PNG artifacts
across the two plastic-drums modes and four difficulties. Atomic generations
still rebuild the complete expected set, approximately 22,448 CHOpt
invocations for the current 702-song catalogue.

Rollback keeps the prior immutable generation directories. Do not delete them
until the new catalogue, rankings implications, and browser parity are
accepted.
