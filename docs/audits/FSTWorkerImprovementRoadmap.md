# FSTWorker Improvement Roadmap

**Audit date:** 2026-07-10  
**Container:** `fstworker`  
**Mode:** Read-only scrape, proxy-fleet, persistence, post-process, and
reliability audit  
**Implementation status:** No worker, proxy, Epic request, data, or deployment
changes were made during this audit.

## Executive decision

The worker has sophisticated adaptive concurrency, proxy isolation, staging,
COPY, publication, ranking, and replay foundations. It is currently vulnerable
to publishing incomplete data because several failure paths are swallowed or
treated as successful. The active 30-node PIA fleet also spends substantial
effort on retries, curl fallback, tarpits, restarts, and duplicate/unhealthy
egress capacity.

Correctness gates come before speed. Proxy and concurrency tuning must keep the
same global Epic request budget and identical scrape scope while measuring
useful rows per wire send.

## Audit report delivery

This roadmap and the service roadmap are accompanied by:

`FST Autonomous Agent: Recap - Service and Worker Deep Audit · Needs Attention`

Delivery requires rendered HTML/text plus SMTP acceptance, or a recorded SMTP
blocker and exact outbox artifact paths.

## Cross-container publication rollout

Worker publication changes follow this order:

1. PostgreSQL adds backward-compatible per-scope published-source schema.
2. Worker dual-writes and validates source mappings.
3. Worker atomically promotes mapping, generation, cache, and scrape pointer.
4. Service resolver/exports switch behind a rollback flag.
5. Forced frozen cold-miss and live-scrape parity approve cutover.

**PG-1 rollout completed 2026-07-11**

- Worker coverage and source-candidate writes are enabled with
  `Features__WritePublishedScopeSources=true`; worker reads keep
  `Features__UsePublishedScopeSources=false`.
- Scrape `1230` recorded complete coverage for all `6,129` expected scopes,
  built and validated all `6,129` mappings in `00:03:27.318`, then atomically
  promoted mapping, fingerprint publication IDs, band/cache state, and the
  global scrape pointer.
- Cancellation/failure fixtures retain the prior pointer, and the live
  `1229 -> 1230` window completed with zero missing/incomplete mappings and
  continuous public health.
- Runtime schema probes no longer retain publication-ledger DDL locks through
  band publication. WORKER-4.3's broader duplicate band publication cleanup
  remains separate and was not combined with PG-1.

## Autonomous execution windows

| Phase/task family | Execution class | Decision window |
|---|---|---|
| WORKER-0 completeness/publication correctness | `full-scrape-ab` | Wait for current terminal publish, stop worker, deploy one gate, run a complete scrape/post-process/publish, stop and compare manifests/parity |
| WORKER-1 retry/cancellation/curl behavior | `full-scrape-ab` | One network candidate per complete scrape with identical scope and global Epic budget |
| WORKER-2 bounded canaries | `continuous-safe` isolated artifacts first, then `full-scrape-ab` for promotion | Canaries cannot publish or mutate shared state; accepted routing then gets one complete scrape window |
| WORKER-3 queues/memory/CHOpt | `full-scrape-ab` | Compare peak RSS/GC/queue depth plus full publication parity |
| WORKER-4 ranking/post-process | `full-scrape-ab` | Compare one complete post-process/publish window and stop before the next scrape |
| WORKER-5 queue/health/token ownership | `full-scrape-ab` when deployed | Fault/restart plus one complete scrape window |
| WORKER-6 code-only reachability cleanup | `continuous-safe`; stricter owner class for runtime removal | No worker hold for static proof; production removals use the owning phase gate |

For each full-scrape candidate, safe implementation and tests may proceed while
the current scrape runs, but production mutation waits for completion. The
worker is then held, one candidate is deployed, one complete scrape is
monitored, and the worker is held again for iterate/reject/accept and
commit/revert handling.

## Current live baseline

| Surface | Evidence | Assessment |
|---|---|---|
| Active proxy config | 30 PIA proxy URLs/container names; active-standby false; DOP 120; initial DOP 90; learned max 360; global RPS 480; page concurrency 30 | All-node mode is active |
| Proxy metadata | Active container lacks provider and control URL arrays; logs label proxies `unknown` | Poor diagnostics/config completeness |
| Egress health | 27 control probes succeeded, 3 failed; only 26 unique egress hashes; one duplicate egress assignment | Effective pool is below 30 |
| Docker health | `pia-gluetun-20` unhealthy during audit | Active health degradation |
| Eight-hour worker log occurrences | 12,056 timeout lines; 38,809 CDN-block lines; 6,275 HTTP-error lines; 48,161 tarpit lines; 142 scheduled restarts; 22,840 `503` lines; 496,741 curl-fallback mentions | Severe retry/fallback amplification |
| Latest cumulative log counter | 2,825,080 wire sends and 234,472 blocks | About 8.3% block ratio in that counter window |
| Current scrape | Scrape 1228 started 08:54 UTC; network/post-process transition at 12:23; post-process took 3:01:39; scrape remained incomplete after post-process | End-to-end finalization is long |
| Rankings | 1:52:52 total; rank-history snapshots 1:16:45; band rankings 15:17 | Ranking/history persistence dominates post-process |
| Recent completed scrape duration | 6:18-6:57 for scrapes 1224-1227 versus 4:44-5:06 for selected July 3-6 scrapes | Material regression |
| Useful scrape result | About 39.5M entries and about 398k logical requests per completed scrape | Stable useful scope |

Log counts are occurrences, not guaranteed unique requests. They are still
sufficient to show the failure path is dominating the operational signal.

### Inserted WORKER-0A provider recovery prerequisite

**Decision:** Accepted for bounded recovery; full WORKER-0A live A/B remains
the next scrape-boundary decision.

Scrapes `1237` through `1242` did not identify an Epic token, entitlement,
429, or Epic 5xx failure. Scrape `1242` refreshed the worker token successfully
but produced 460 alternate-proxy retries and 376 timeouts with zero completed
scopes. The failure was therefore isolated to provider-exit readiness and
high-load routing, not account-global authentication.

The recovery inventory found 30/30 Docker-healthy PIA containers but only
26/30 general HTTP-proxy paths initially ready. Three sequential authenticated
Epic canary rounds narrowed the stable set:

| Recovery signal | Result |
|---|---|
| Stable authenticated PIA exits | 25/30, all with unique hashed egress |
| Never-valid exits | `pia-gluetun-16`, `pia-gluetun-23` |
| Flapping exits | `pia-gluetun-11`, `pia-gluetun-12`, `pia-gluetun-20` |
| Direct control | Valid Epic JSON in every bounded round; not promoted |
| Auth/account | Refresh succeeded; no JSON 401/403 or entitlement signal |
| Matched publication-disabled slice | 25 direct + 25 proxied requests, 50/50 valid JSON, 25/25 exact entry-array matches, eight instruments |
| Public/database safety | Published `1236`, 6,138 scopes, 39,588,650 rows; reads unfrozen; zero active scrape, active query, or ungranted lock |
| Capacity | 96,006,438,912 bytes free; scrape allowed with 3.19-day capacity alert |

Production now keeps the canonical 30 PIA service definitions but configures a
25-exit effective worker pool, stops the five quarantined containers, and caps
aggregate Epic pacing at 400 requests/s. Provider and control arrays are
index-aligned with the proxy and container arrays. The stale held worker
container was removed so it cannot be started with the prior 30-exit/480-RPS
configuration.

`tools/fst-worker-compose-guard.sh` is installed in production and is required
for worker recreation. It fails closed unless the canonical
`docker-compose.pia-30.yml` overlay resolves the expected effective endpoint
count, all 30 canonical services, aligned provider/control/container metadata,
the per-exit pacing cap, healthy controls, and unique hashed egress.
`Scraper:ExpectedProxyEndpointCount` adds the matching application startup
guard for guard-aware images.

Evidence:
`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/autonomous-artifacts/proxy-recovery-20260713T171754Z`.

**First full-scrape retry - rejected safely, bounded repair accepted in code**

- Guarded run-once scrape `1243` used the exact 25-exit / 400-RPS recovered
  pool and kept `/readyz`, the festivalweb shell, and `/api/service-info`
  healthy.
- The run completed zero scopes because all `150/150` observed
  `events-discovery` sends were CDN-blocked before leaderboard work began.
  This narrowed the gap to an endpoint omitted from the earlier matched
  leaderboard canaries; it did not invalidate their leaderboard-page parity.
- The worker was stopped, scrape `1243` was marked failed, public reads were
  unfrozen on published `1236`, and zero scope manifests or writer failures
  were left behind.
- Season-window discovery now has a 45-second deadline before using persisted
  season windows (or existing bounded probing when no cache exists). The repair
  adds no direct/AirVPN fallback and does not change the 25-exit pool, DOP, or
  400-RPS aggregate cap.
- Retries `1244` and `1245` then isolated a separate run-once contaminant:
  `RegistrationBackfillWorker` claimed one queued user and started V2 POST
  lookups across `682` songs while the controlled scrape was beginning.
  Scrape `1245` recorded `1,639/1,660` CDN-blocked sends and still had zero
  manifests, staging rows, or completed scopes when it was stopped.
- Full-worker run-once mode now omits the continuously polling registration
  backfill hosted service. Normal scheduled workers and dedicated registration
  sync workers retain it, and the scrape's explicit deferred registration-sync
  phase remains available after core post-processing.
- With registration isolated and logical-shadow writes disabled, scrape `1247`
  reached core leaderboard work but adaptive DOP `90` produced
  `6,260/7,370` CDN-blocked sends. A DOP-25 retry still ramped to `83` and
  reached only `84/6,138` leaderboards at `0.9` request/s before the measured
  network estimate exceeded the allowed performance envelope.
- Publication-disabled rate canaries then proved `125/125` valid responses at
  requested per-exit ceilings of `1`, `2`, `4`, and `8`. The next candidate
  keeps the global `400`-RPS ceiling and 25-exit pool but enforces a conservative
  `1` request start/s and one simultaneous request per exit. The rate-only
  candidates proved why both controls are required: slow 30-second requests
  accumulated up to 19 in flight on one proxy, then stalled at `65/6,138`
  leaderboards despite the start-rate cap.
- The one-in-flight retry proved connection reuse was the remaining transport
  difference from the valid curl canaries: it held every proxy to one active
  request but still accumulated `1,532` timeouts. The next candidate disables
  proxy connection reuse so each .NET Epic request uses the same fresh-
  connection posture as the `125/125` canary rounds.
- Fresh .NET connections did not remove the stall, while both default and
  forced-HTTP/1.1 curl canaries remained `125/125` valid. The next bounded
  candidate therefore uses curl as the primary proxy transport, keeps the same
  lease/rate/concurrency/cooldown accounting, and stores transient bodies only
  under `/app/data/curl-transport` on the FST drive.
- Curl-primary scrape `1254` sustained about `29` useful requests/s and reached
  the end of its first network window, but a 30-second curl timeout surfaced as
  a plain `OperationCanceledException` and canceled the solo pass instead of
  entering the transient retry loop. The executor now treats all internal
  cancellations as retryable timeouts while preserving caller cancellation.
- The production run-once overlay now resolves `restart: "no"`. Without that
  override, Docker's base `unless-stopped` policy restarted the cleanly exiting
  run-once process and began unwanted scrape `1255`; the compose guard now
  rejects any run-once recreate whose resolved restart policy is not `no`.
- Solo-complete retry `1257` produced `6,122/6,138` complete manifests and
  zero writer failures. The 16 isolated gaps all ended in `HttpFailure`; logs
  showed a transport failure followed by a curl fallback `503` that was
  incorrectly returned as a recovered response. Retryable fallback statuses
  (`429`/`5xx`) are now discarded so the normal transient retry loop continues.
- After two new catalog songs appeared, retry `1259` reached
  `6,140/6,156` complete solo manifests. All 16 remaining scopes were the eight
  supported instruments for those songs, and Epic returned JSON
  `404 com.epicgames.events.event_not_found` because the leaderboard events did
  not exist yet. Page-zero `event_not_found` is now classified as a legitimate
  empty scope for solo and band; later-page 404s remain failures.
- Retry `1260` proved the solo path (`6,156/6,156`) and completed all
  `190,111` band pages, but the independent `BandPageFetcher` still used the
  shared base-class HTTP-failure path and left the same six new-song band
  scopes incomplete. The shared page fetcher now creates a typed empty page for
  page-zero `event_not_found`, so both band implementations enforce the same
  legitimate-empty rule.

**Next live A/B instruction:** keep the worker held, run the scrape capacity
guard and proxy compose guard, deploy the accepted `b01d5c03` WORKER-0A
candidate through `--recreate-runonce`, verify the full public path, then
monitor exactly one complete scrape/post-process/publication decision and hold
the worker again before another scrape.

## Great / good / okay / poor / bad

| Rating | Areas |
|---|---|
| Great | Full-scope scrape coverage; binary COPY/staging; real PostgreSQL tests; rich phase logs; adaptive concurrency concepts |
| Good | Per-proxy cooldown/restart mechanics; publication ledger concept; resumable band-history jobs |
| Okay | Thirty-exit pool as an experimental capacity tool |
| Poor | Proxy health quality; retry accounting; task allocation; status freshness; role isolation; registration claims |
| Bad | Deep-scrape rows discarded; writer failures swallowed; critical post-process failures publish; malformed/missing pages accepted |

## Phase WORKER-0: Make incomplete data unpublishable

**Decision:** Accepted correctness blocker  
**Dependencies:** None  
**Rollback:** Preserve the last published scrape; keep old path behind flags only
until replay and live shadow parity pass.

### WORKER-0.1 - Persist coordinated deep-scrape results

**Evidence**

- `DeepScrapeCoordinator.cs:191-222,357-373` does not populate
  `EntriesCount`.
- `GlobalLeaderboardScraper.cs:1813-1825` and
  `ScrapeOrchestrator.cs:179-215` skip zero-count results.

**Acceptance**

- A fixture with over-threshold top pages fetches deeper rows and those exact
  rows reach staging, snapshot, projection, and publication.

### WORKER-0.2 - Propagate every writer failure

**Evidence**

- Solo, band, and online bounded writers catch persistence failures and allow
  the scrape to continue.

**Work**

1. Return a durable per-scope writer result.
2. Mark failed/dropped rows and stop publication.
3. Keep enough spool/artifact state for replay.

**Acceptance**

- Fault injection in each writer leaves the prior published scrape active,
  records the exact failed scope/rows, and permits deterministic replay.

**Rollback/blocked condition**

- Dual-run the strict result contract before enforcing it. Do not promote if a
  failed scope cannot be retained or replayed.

### WORKER-0.3 - Classify post-process phases by publication criticality

**Evidence**

- `PostScrapeOrchestrator.RunPhaseAsync` suppresses failures for snapshot
  activation, rankings, projections, rivals, stats, and precompute.

**Work**

1. Critical before publication: snapshot activation, current projections,
   ranking generation, band generation, response generation required by
   published routes.
2. Best effort after publication: cleanup, optional notifications, and
   non-contract analytics.
3. Persist phase outcome and publication decision.

**Acceptance**

- Fault each critical phase independently and prove no publish occurs.
- Fault each declared best-effort phase and prove publication remains correct
  while the failure is visible.

**Rollback**

- Reclassify only a specifically proven non-contract phase; never globally
  restore exception swallowing.

### WORKER-0.4 - Add per-scope page completeness manifests

**Evidence**

- Parse failures become empty/failed page results but do not block scope
  completion.
- Fingerprints have no reported total entries/pages.
- 42 active Pro Vocals scopes had no scope fingerprint during the live scrape.

**Manifest**

- expected page range;
- received pages;
- terminal Epic boundary;
- parse status;
- retry exhaustion;
- total reported entries/pages;
- deep-scrape extension range;
- content/coverage fingerprint.

**Acceptance**

- Every expected song/instrument scope has one complete manifest.
- Unexplained page gaps, parse failures, or missing fingerprints reject that
  scrape's publication.

**Rollback/blocked condition**

- Dual-write manifests without gating first. Publication gating remains blocked
  until replay proves all legitimate Epic terminal conditions are classified.

### WORKER-0A correctness implementation - code accepted, live promotion hard-blocked

**Execution class:** `full-scrape-ab`
**Rollback switches:** `Features__RequireSuccessfulScrapeWriters`,
`Features__EnforcePublicationCriticalPhases`, and
`Features__EnforceScopeCompletenessManifests`.

- Coordinated deep scopes now retain wave-one rows, merge wave-two rows once,
  populate `EntriesCount`, and invoke persistence once per scope. Exact
  PostgreSQL fixtures cover both disk-spool and bounded-online writers through
  snapshot, current projection, manifest, published-source mapping, and global
  publication.
- Solo, band, and bounded-online writers return exact durable failure results.
  Failed disk batches retain the original binary spool plus a versioned JSON
  manifest; bounded-online batches retain versioned typed JSON. Both formats
  have deterministic persisted-artifact replay tests.
- `scrape_log` has durable `running`/`completed`/`failed` state.
  `scrape_writer_failures` records exact failed scopes/pages/rows and artifact
  paths. A failed candidate cannot later be completed or published, while the
  prior mapped published scrape remains active.
- Every post-scrape phase is explicitly `publication_critical` or
  `best_effort`. Outcomes are persisted in `scrape_phase_outcomes`; critical
  failures reject publication, while best-effort failures publish with visible
  `/api/service-info` warnings.
- `leaderboard_scope_manifests` records expected/received pages, final status
  per page, Epic empty/forbidden boundaries, parse/retry state, reported
  totals/pages, deep range, and content/coverage fingerprints for all expected
  solo and band scopes. Missing, malformed, retry-exhausted, or unexplained
  gap scopes cannot publish when enforcement is enabled.
- A configured proxy pool no longer bypasses its alternate-exit recovery with
  a direct curl process. Where curl fallback is still applicable, HTTP `200`
  responses with non-JSON bodies are treated as continued CDN blocks rather
  than successful leaderboard pages. Other malformed successful responses
  receive one bounded outer retry before their manifest remains failed.
- Focused correctness validation passes `418/418`; the final full diagnostic
  passes `2,015/2,017`, with two deterministic pre-existing fixtures outside
  WORKER-0A. CI-equivalent FSTService line coverage is `94.81%`. The web API
  contract build and secret scan pass.

**Live decision - hard-blocked 2026-07-13**

- Baseline scrape `1237` failed closed with `4,575/6,138` incomplete solo
  scopes while retaining published `1236`. It logged `162,454` parse failures,
  `237,566` curl-fallback HTTP `200` responses, and `8,506` failed curl
  transports. Public reads unfroze cleanly.
- Candidate `1238` was stopped before network work because inherited logical
  shadow rollback attempted an unbounded full-table scrape-ID scan. The safe
  retry disabled the experimental logical-version shadow writer; physical
  snapshots and published-source semantics remained authoritative.
- A later retry exposed that legacy staging cleanup deleted older
  `completed_at IS NULL` scrape-log rows even after they were marked failed.
  Cleanup now removes only abandoned `status='running'` log rows, retaining
  failed candidates and their manifest/writer/phase ledgers for audit.
- Candidates `1239` and `1240` proved malformed HTML could no longer be treated
  as valid data. The final executor path classified disguised HTTP `200` HTML
  inside the proxy/CDN loop, skipped duplicate curl sends for routed requests,
  and produced zero parser-level false successes.
- Candidate `1241` completed zero scopes after all routed exits remained
  blocked through cooldown and self-heal restarts (`1,471` alternate-proxy CDN
  retries and `1,303` timeouts in the captured window). A manual reset of all
  30 configured PIA containers did not restore a usable provider path.
  Candidate `1242` again completed zero scopes (`460` alternate-proxy retries,
  `376` timeouts, zero parse failures).
- No writer, manifest, phase, projection, or publication gate could be
  evaluated on a complete live candidate because Epic returned no valid page
  zero through any refreshed PIA exit. This is a time/provider-accrual hard
  gate, not a correctness acceptance.
- Production was rolled back to `fstservice:service02-824415e9`; `fstworker`
  is created on that image but held to avoid repeated partial writes while all
  exits are blocked. `fstservice`, `festivalweb`, and Postgres are healthy,
  published `1236` remains mapped with `6,138` sources and `39,588,650` rows,
  and public reads are unfrozen.
- During stopped-worker image normalization, `docker compose create` also
  recreated Postgres and PIA dependencies in the stopped state. They were
  immediately restarted; Postgres readiness, `/readyz`, festivalweb, service
  status, locks, and the exact published mapping were revalidated with no data
  or publication change. The incident and recovery are included in evidence.
- Additive correctness tables remain harmless and backwards-compatible.
  Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/autonomous-artifacts/worker0a-correctness-20260713T1552Z`.

**Live incident update - held 2026-07-15**

- Incremental candidates `1254` through `1260` all failed closed for curl
  cancellation, unwanted run-once retry, missing band manifests, isolated HTTP
  gaps, capacity before band flush, new-song `event_not_found`, or remaining
  band incompleteness. None replaced published scrape `1236`.
- Candidate `1261` completed its network manifests and entered
  `PostScrapeEnrichment`, but was stopped during band current-projection
  generation `94` before rankings/publication. It was finalized as failed at
  `capacity_before_rankings_publish`; it has zero published-source rows and
  public reads remain unfrozen on published `1236`.
- The hold was required because only `26.2 GB` remained while matched scrape
  `1236` had consumed about `45.15 GB` from the equivalent pre-rank boundary
  through publication. Two one-family index reclaims plus reproducible cache
  cleanup raised free space to about `39.83 GB`, still about `5.32 GB` below
  that measured boundary before rollback margin.
- `fstworker` remains intentionally offline. `fstservice`, `festivalweb`, and
  Postgres are healthy, and WORKER-0A remains unaccepted because no strict-gate
  candidate has completed and published end to end. Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/fst-disk-pressure-20260715T1408Z`.
- A residual owner sweep found no safe single index that closes the remaining
  gap. The only sufficiently large low-scan candidate is used by the public
  selected-team projection plan; all four smaller zero-owner indexes combined
  still leave about `1.30 GB` short before rollback margin.

**Residual capacity recovery - ready 2026-07-15**

- The composite retention helper was replaced by a 688,128-byte BRIN, and only
  the `23,526,973,440`-byte non-constraint
  `ix_crh_retention_cutoff_account` btree was dropped concurrently.
  Filesystem free space reached `63,339,065,344` bytes.
- The measured `45,148,225,536`-byte pre-rank-through-publication guard now
  passes with `18,190,839,808` bytes of margin. `12/12` public
  route/history/ranking/export fingerprints matched before, after, and on
  repeat; relevant plans and `106` targeted tests passed.
- `fstworker` remains intentionally held, published scrape `1236` remains
  unfrozen and authoritative, and scrape `1261` remains failed closed.
  Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/fst-residual-capacity-20260715T144916Z`.

**Final WORKER-0A live A/B - rejected and capacity-hard-blocked 2026-07-16**

- The measured scrape guard passed immediately before start with
  `63,365,509,120` bytes free, `18,217,283,584` bytes above the
  `45,148,225,536`-byte requirement. The guarded candidate used image
  `fstservice:worker0a-caab3c3f` from pushed commit `caab3c3f`, restart policy
  `no`, all three enforcement flags enabled, the canonical 30-service PIA
  overlay, 25/25 healthy unique effective exits, 400 aggregate RPS, and no
  AirVPN fallback.
- Scrape `1262` completed all `8,208/8,208` expected manifests with zero
  incomplete scopes, parse failures, retry exhaustion, writer failures, or
  publication-critical phase failures. Solo contributed `6,156` manifests;
  band contributed `2,052`. The combined manifest contained `588,454` pages
  and `58,675,997` reported entries. Band fetched `190,144` pages and
  `18,987,470` entries.
- Writer drain completed and deleted its `9,223,693,833`-byte same-drive spool.
  The exact post-writer guard passed at `54,284,406,784` bytes free. Three
  five-minute enrichment timeouts remained correctly classified as
  best-effort: registered-user refresh, registered-player band discovery, and
  registered-band targeted processing.
- The run was stopped during band current-projection generation `95`
  publication after `12,972` ready scopes and `21,967,889` projection rows.
  Free space had fallen to `30,992,838,656` bytes. Rankings and global
  publication had not run, so candidate `1262` owns zero published-source
  rows and cannot satisfy WORKER-0A promotion.
- The guarded window ran `37,434.590 s` before the stop. Network plus writer
  drain took `22,326.583 s`; solo network took `12,893.7 s` and band fetch
  `6,031.2 s`. The candidate added `26,778,927,104` database bytes and reduced
  filesystem free space by `32,087,322,624` bytes. Peak worker/Postgres memory
  was `3.71/15.21 GiB`; `/readyz` and festivalweb stayed HTTP `200` for all
  `604` captured one-minute ticks.
- Rollback marked `1262` failed at
  `capacity_during_band_projection_publish`, cleared stale worker/freeze
  state, retained published `1236`, and restored the held worker container to
  `fstservice:worker0a-d744aed9`. Published mapping remains exactly `6,138`
  complete scopes and `39,588,650` rows.
- Post-rollback route/export/history/ranking parity was exact for `12/13`
  normalized surfaces. `/api/rankings/bands/.../songs` differed only in 61
  rank, population, and percentile scalar values because that endpoint falls
  back to live `band_entries` when the optional band-song ranking projection
  is stale. No scores, song IDs, exports, histories, leaderboards, or ranking
  pages changed. This live-fallback contract and insufficient capacity both
  block promotion.
- Final measured free space is `31,264,702,464` bytes, leaving a
  `13,883,523,072`-byte shortfall. No second scrape, destructive cleanup,
  alternate-drive work, rate increase, or provider fallback is authorized.
  `fstworker` remains held. WORKER-0.5 and WORKER-0.6 remain pending behind the
  unresolved WORKER-0A publication/capacity gate.
- Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/worker0a-final-live-ab-20260715T151317Z`.

### WORKER-0.5 - Separate solo and band completion

A band timeout must not mark `LeaderboardScrapeCompleted=true`. A task still
writing after spool disposal is a failed band pass, not a successful scrape.

**Acceptance**

- Timeout/cancellation tests leave the prior band generation published and no
  task writes after spool disposal.

**Rollback**

- Retain the prior published band generation; do not restore partial publish.

### WORKER-0.6 - Add all nine instruments to score-validity support

Pro Vocals, Pro Cymbals, and Pro Drums require max-score/CHOpt fixtures,
deep-scrape thresholds, leeway validation, and ranking metrics.

**Acceptance**

- Nine-instrument fixtures cover valid, over-threshold, and missing-chart
  cases; replay output matches Epic/source evidence.

**Blocked condition**

- If chart provenance is unavailable, keep the instrument explicitly
  unsupported for threshold-based decisions rather than treating unknown as
  valid.

## Phase WORKER-1: Bound retries and wire amplification

**Decision:** Accepted  
**Dependencies:** WORKER-0

### WORKER-1.1 - Add pass deadlines and per-operation retry budgets

**Evidence**

- Transport retries are unbounded.
- `ScrapePassTimeoutMinutes` is compatibility-only.

**Work**

1. Global pass deadline.
2. Per-song/instrument/page retry budget.
3. Durable retry/resume state.
4. Preserve old published data on exhaustion.

### WORKER-1.2 - Isolate cancellation

One cancelled CDN waiter must not cancel shared CDN recovery. Use per-waiter
`WaitAsync(ct)` semantics rather than cancelling the shared completion source.

### WORKER-1.3 - Remove or strictly contain curl fallback

**Evidence**

- Curl fallback creates processes/files, bypasses the normal rate token and
  request counter, and can alter HTTP classification.

**Candidate**

1. Prefer fixing .NET transport behavior and disable fallback.
2. If retained, cap global fallback concurrency, consume a rate token, count
   every send, preserve status semantics, use FST-drive scratch, and kill/clean
   on cancellation.

**Promotion target**

- At least 80% fewer actual fallback invocations/processes.
- Every fallback consumes a rate token and increments wire-send accounting.
- Useful rows per wire send improves with identical scrape parity and no
  increase in 429/5xx.

### WORKER-1.4 - Classify failure ownership

| Signal | Ownership/action |
|---|---|
| Tunnel/connect/transport | Node quarantine and bounded restart |
| Public egress duplicate/flap | Capacity removal until healthy/unique |
| Non-JSON 403/CDN block | Egress cooldown and alternate sticky node |
| 429 | Global/account pacing, not node punishment |
| Epic 5xx | Backend circuit/retry budget |
| JSON 401/403 | Token/entitlement path |

## Phase WORKER-2: Run a matched 30-proxy evaluation

**Decision:** Experimental until matched evidence  
**Dependencies:** WORKER-0 and WORKER-1  
**Provider rule:** Do not increase global Epic rate or widen entitlement use.

### WORKER-2.1 - Build real proxy health and capacity metrics

1. Configure provider and control URL for every node.
2. Probe gluetun health, public egress, egress uniqueness, RTT, and HTTP proxy
   readiness before assigning Epic work.
3. Export per-node in-flight, success, timeout, block, 429, 5xx, restart,
   cooldown, useful bytes, and useful rows.
4. Keep actual public IP out of logs; use a stable redacted hash.

### WORKER-2.2 - Use weighted least-outstanding with stickiness

1. Per-node concurrency cap.
2. EWMA latency/error weight.
3. Song/instrument/page-range stickiness for connection reuse.
4. Half-open probes before returning a node.
5. Remove duplicate egresses from effective capacity.

### WORKER-2.3 - Run bounded pool-size and DOP canaries

Do not run a full Cartesian matrix and do not send 480 RPS through one exit.
Canaries run beside normal production only when the combined production plus
canary budget remains at or below the configured global limit.

| Matrix | Values |
|---|---|
| Healthy unique exits | 1, 4, 8, 16, 26, then 30 only if actually unique/healthy |
| Global DOP | 60, 90, 120, 180 |
| Aggregate RPS | Start at no more than 16 RPS per healthy unique exit and never exceed 480 |
| Assignment | current least-in-flight vs weighted sticky |

**Execution sequence**

1. Preflight Docker/Postgres/publication/freeze, proxy health, egress
   uniqueness, and current production request rate.
2. Reserve the normal production request budget first; canaries may use only
   unallocated capacity and must not displace production work.
3. Run a small fixed song/instrument/page slice with publication disabled,
   isolated result artifacts, and no shared snapshot/projection/cache mutation.
4. Evaluate one pool/DOP combination at a time.
5. Compare pool sizes at a matched aggregate RPS supported safely by both
   configurations.
6. Stop immediately on publication lag, service degradation, 429 increase,
   error-budget breach, proxy flapping, or correctness mismatch.
7. Run a full live shadow scrape only after bounded canaries pass; the shadow
   remains publication-disabled until full parity is complete.

**Correctness**

- Same songs, instruments, page ranges, deep-scrape result, row counts,
  fingerprints, and published API responses.

**Metrics**

- wall clock;
- useful rows/wire send;
- bytes/wire send;
- retries;
- timeout/block/403/429/5xx;
- per-node p50/p95;
- RSS/GC;
- connection reuse;
- post-process start time.

**Promotion target**

- At least 30% fewer wire sends per useful row.
- At least 20% lower network wall clock.
- No correctness, 429, entitlement, or publication regression.

## Phase WORKER-3: Bound task and memory growth

**Decision:** Accepted after correctness gates

### WORKER-3.1 - Replace task-per-song/instrument/page fan-out

Use bounded channels for:

- song work;
- leaderboard/page work;
- retries;
- persistence batches.

The DOP limiter should bound queued state, not only active network sends.

### WORKER-3.2 - Stream page ownership to the writer

1. Parse once.
2. Hand immutable page batches to one bounded persistence pipeline.
3. Record durable scope coverage.
4. Avoid parallel legacy/result/page/online writer implementations.

### WORKER-3.3 - Measure real allocation pressure

Record:

- total allocated bytes;
- allocation rate;
- peak RSS;
- Gen 0/1/2 and LOH collections;
- GC pause;
- queue depth;
- task count;
- child process count.

### WORKER-3.4 - Make CHOpt cancellation safe

Read stdout/stderr concurrently, kill the process tree on cancellation, and use
the configured FST data directory for scratch.

## Phase WORKER-4: Reduce post-process and ranking time

**Decision:** Accepted A/B program  
**Dependencies:** PostgreSQL query phases and WORKER-0

### WORKER-4.1 - Attack rank-history snapshot cost first

**Evidence**

- Current ranking total: 6,771,576 ms.
- Rank-history snapshots: 4,605,489 ms (68% of ranking time).
- Composite snapshot alone: 1,078,184 ms.
- Recent ranking total rose from about 3.87-4.57M ms to 6.77M ms.

**Candidates**

1. Latest-state tables maintained incrementally rather than rebuilding from
   full history.
2. Changed-account snapshot inserts.
3. Partition/date-aware history access.
4. Remove unused rank-history indexes only after read-owner proof.

**Promotion target**

- Rank-history snapshot time below 45 minutes initially, then below 30 minutes,
  with exact rank/history parity.

### WORKER-4.2 - Make band ranking failure fail the required phase

Band Duets failed during the audited scrape while remaining band types
continued. Record whether the failed type remained on the prior generation and
prevent a success-shaped final result unless that is an explicit partial
publication contract.

### WORKER-4.3 - Remove duplicate band publication

Promote band current/published generations once in the final publication
transaction, not before and after post-process.

**Dependencies**

- PostgreSQL PG-1 published-source schema and SERVICE-0 resolver contract.

**Acceptance**

- Mapping, band generation, cache generation, and scrape pointer promote in one
  transaction.
- Cancellation before commit leaves all public pointers on the prior scrape.

### WORKER-4.4 - Drive downstream work from changed scopes

Do not rebuild projections, precompute, and ranking inputs for unchanged scopes
unless a global algorithm proves it needs them.

## Phase WORKER-5: Make queues and worker health durable

**Decision:** Accepted

### WORKER-5.1 - Use one registration claimant

Replace competing registration consumers with a lease/`SKIP LOCKED` claim,
bounded work per poll, lease expiry, and idempotent completion.

### WORKER-5.2 - Add required-loop health

Readiness must fail when scraper scheduling, registration backfill, catalog
dependency consumption, or durable event publishing exits or becomes stale.

### WORKER-5.3 - Make credential refresh single-owner

Use one token owner or an atomic, locked, permission-restricted shared store.

## Phase WORKER-6: Consolidate dead and duplicate paths

**Decision:** Accepted after reachability proof

Candidates:

- `RoundRobinProxyHandler` and stale proxy diagnostics.
- Duplicate path generation in worker and catalog refresh.
- Duplicate registration consumers.
- Legacy result/page writer APIs used only by tests.
- `BackfillQueue` if no production enqueuer exists.
- Dead options such as compatibility timeout/write batch/background job fields.
- Disabled circuit-breaker code.
- Stale compose feature keys.

## Projected outcomes

| Outcome | Promotion target |
|---|---|
| Data correctness | Writer/page/post-process failure cannot publish |
| Network efficiency | >=30% fewer wire sends per useful row; >=80% fewer fallback occurrences |
| Proxy capacity | Every active node healthy, unique, measured, and provider-labeled |
| Scrape duration | First target <=5.5 hours end-to-end on the same 681-song scope |
| Post-process | First target <2 hours |
| Ranking | First target <90 minutes; rank-history snapshots <45 minutes |
| Memory | Bounded queues and measured peak RSS/GC under the 12 GiB cap |
| Reliability | Finite retries, resumable failures, required-loop health |

## Explicitly rejected shortcuts

- Do not increase Epic RPS because more VPN exits exist.
- Do not punish nodes for global/account 429s.
- Do not publish partial data to improve wall clock.
- Do not retain curl fallback as an unmetered second transport.
- Do not declare 30-node capacity when health/egress uniqueness proves only 26.
- Do not tune DOP without identical data scope and correctness fingerprints.
