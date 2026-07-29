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

Correctness gates come before speed. Proxy and concurrency tuning must keep
identical scrape scope and publication completeness while measuring useful
rows per wire send. The Epic request budget, per-exit concurrency, and block
rate may increase when that reduces wall clock without changing results.

## Audit report delivery

This roadmap and the service roadmap are accompanied by:

`FST Autonomous Agent: Recap - Service and Worker Deep Audit · Needs Attention`

Delivery requires rendered HTML/text plus SMTP acceptance, or a recorded SMTP
blocker and exact outbox artifact paths.

## SCRAPE-1268 DUAL-LANE — functional data win; shared promotion held

- Run-once scrape `1268` used the contract-bearing
  `fstservice:scrape1268-dual-4ae6c171` image, restart `no`, the exact
  `candidate-800-32-4` / `notification-db-only` wrapper card, and 25
  preflight-healthy unique PIA exits. It published `6,174` complete source
  mappings / `39,944,787` rows, unfroze public reads, completed notifications,
  and exited cleanly before another scrape.
- The shared data-correctness gate passed: `8,232/8,232` manifests, zero incomplete
  scopes, retry exhaustion, parse failures, writer failures, or
  publication-critical failures. Two post-publish suites were HTTP `200` and
  byte-exact `13/13`.
- **Network lane — iterate/reject.** Network plus writer drain was
  `5:02:40.563`, `0.10%` slower than `1267`; useful throughput was `32.64`
  pages/s, `0.08%` lower. Final transport used `640,081` sends with `18,987`
  CDN blocks (`2.97%`), one primary `503`, zero `429`, `1.0797` retry
  amplification, no three bad one-minute windows, and 25/25 retained exits.
  The lane passed safety but missed the required 10% useful-throughput gain.
- The `32.64` figure is not the transport-only rate. Epic fetching ended after
  `4:17:07.544` at `38.428` useful pages/s; the declared legacy boundary then
  waited `45:33.432` for band writer drain. The bounded `800/32/4` result was
  `35.958` pages/s, so full-run pure fetch was actually `6.87%` faster. Future
  network decisions score pure fetch separately and retain the combined
  network-plus-writer boundary only as a cross-lane diagnostic.
- `candidate-800-32-4` passed the matched bounded calibration at `35.96`
  useful pages/s. `candidate-1600-64-8` reached `53.22` pages/s but its
  one-round harness could not recover two TLS failures after the first
  alternates returned CDN `403`; `candidate-2880-128-16` was not run. The
  accepted `800` canary also had two TLS failures and recovered them, while the
  reported live payload variant was 12 responses of one fingerprint and one
  response of another through the same exit over 56 seconds. That evidence
  rejects the old canary decision package, not the production retry path.
  `pia-gluetun-3` remains on its independently reversible healthy endpoint
  repair as an availability prerequisite, not a promoted throughput result.
- The apparent `~400k -> 592,849` request increase is not retry inflation:
  `401,504` pages are solo and `191,345` are now-required complete band pages.
  Complete band scope semantics explain about 99% of the historical delta;
  wire sends remain a separate retry metric.
- **Data/query lane — functional pass, promotion iterate.** Publication
  persisted an empty bounded projection workset, never fell back to all
  `6,174` scopes, held one recovery owner, and completed player run `166` plus
  band run `167` in `82.15 s` after marker start / `101.76 s` after
  publication. The data lane emitted zero Epic sends, added `266,652,828`
  WAL bytes, added zero temp bytes or checkpoints, and had no duplicate owner.
- The shared public-health gate still failed during the multi-minute
  publication transaction: real festivalweb traffic recorded `13` HTTP `504`
  and `20` client-cancelled `499` responses while `api_response_cache` was
  locked. Root cause is cache `TRUNCATE` occurring before long band ranking
  snapshot copies/index builds in `MetaDatabase.PublishScrapeRun`.
  The follow-up repair moves cache promotion to the end of the transaction and
  adds a concurrent-read regression test plus a real leaderboard probe to the
  60-second monitor. Commit `44a1fe9a` is built as
  `fstservice:publication-lock-44a1fe9a` and is selected in the held production
  compose config without recreating the worker. Neither lane is promoted until
  that repair receives its own dual-lane full-scrape window.
- Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/scrape-1268-dual-lane-20260728T184812Z`.

### NETWORK-NEXT-CANDIDATE-DESIGN — bounded tooling ready; live proof held

- The next smallest hypothesis is bounded-only `candidate-800-32-5`: keep
  global/per-exit pacing at `800/32`, curl HTTP/1.1 fresh connections,
  production least-in-flight routing, cooldowns, and every retry setting;
  change only the per-exit concurrency cap from `4` to `5`.
- The bounded pass threshold is `39.554` useful pages/s, exactly 10% over the
  accepted `35.958` result. A later full run must reach `42.271` pure-fetch
  pages/s, or at most `3:53:45.040` for `592,849` pages, exactly 10% over
  scrape `1268` pure fetch.
- Reproducible source now lives in `tools/FstNetworkCanary`; the Python runner
  builds it, permits at most three recovery rounds through previously untried
  alternates, records app-connect/start-transfer/connection metrics, and runs 25
  near-simultaneous cross-exit payload pairs. Temporal variants inside the
  main live stage are diagnostic; only failed matched pairs are payload
  differences.
- A service regression test proves the production sequence TLS failure ->
  alternate CDN `403` -> successful third exit. Scrape `1268` already
  demonstrated `18,987` proxy-isolated CDN alternate retries. `pia-gluetun-3`
  recorded normal block/cooldown behavior and three successful timeout-driven
  self-heals, so its Seattle TCP pinned-endpoint repair remains accepted.
- Curl process overhead was not the current limiter: `800/32/4` used
  `0.69` average CPU cores / `348 MiB` peak memory / 100 active curl
  processes; `1600/64/8` used `1.09` cores / `595 MiB` / 200 processes.
  Connection reuse or .NET transport remains a separate later candidate and
  must not be combined with the concurrency change.
- The production wrapper and compose guard intentionally do not recognize
  `candidate-800-32-5` yet. Real Epic canary execution, worker recreation, and
  another scrape are held until the heavy Trios storage owner gives fresh
  explicit clearance; wrapper/guard promotion requires a passing bounded
  artifact first.
- Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/network-next-candidate-design-20260729T105348Z`.

### NOTIFICATION-RECOVERY foundation

- Verified that scrape `1267` published and unfroze before notification
  detection, then the worker was stopped during a redundant full solo
  projection refresh. PostgreSQL cancelled the active statement with `57014`;
  no player/band detection run was created.
- Published scrape `1267` was recovered without a new scrape. Player run `164`
  inserted `995` notification rows; band run `165` inserted `3,996`.
  Newly registered subjects were selectively baselined, suppressing `4,193`
  player-song and `17,070` band/rank back-catalog rows.
- Publication now atomically queues notification completion, detection runs
  record the published scrape, interrupted work remains resumable, startup
  catches up before the next scrape, and the normal path refreshes only scopes
  changed after publication cleanup instead of all `6,174` solo scopes.
- The six post-proxy-stabilization scrapes each hit the same three `300 s`
  best-effort timeouts. Solo refresh now has a measured `10 min` budget; band
  discovery and targeted processing checkpoint and fairly rotate through a
  total budget of `80` lookups per pass. Accepted Epic proxy rates remain
  unchanged.
- Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/notification-recovery-20260728T1428Z`.
- Scrape `1268` qualified the bounded normal path, but the lane remains
  unpromoted until the shared publication-route lock repair passes. Rollback
  uses an image that retains this publication-marker/scope-plan state contract
  while reverting only candidate behavior/config. A pre-contract worker image
  is not valid after the database constraint exists. Interruption leaves the
  published scrape available, marks notification work deferred/failed, and
  startup recovery resumes the same published scrape before another scrape.
- Registered-user/discovery/targeted budget changes share the Epic proxy pool
  with the network candidate, so they are not part of the independently scored
  data/query lane. The network lane owns their accepted bounded settings:
  `00:10:00` solo refresh, `00:05:00` discovery/targeted timeouts, and `80`
  lookups per discovery/targeted pass.

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
| WORKER-1 retry/cancellation/curl behavior | `full-scrape-ab` | One sequentially qualified network candidate per dual-lane scrape with identical scope and explicit rate/error budgets |
| WORKER-2 bounded canaries | `continuous-safe` isolated artifacts first, then `full-scrape-ab` for promotion | Canaries cannot publish or mutate shared state; accepted routing then gets one complete scrape window |
| WORKER-3 queues/memory/CHOpt | `full-scrape-ab` | Compare peak RSS/GC/queue depth plus full publication parity |
| WORKER-4 ranking/post-process | `full-scrape-ab` | Compare one complete post-process/publish window and stop before the next scrape |
| WORKER-5 queue/health/token ownership | `full-scrape-ab` when deployed | Fault/restart plus one complete scrape window |
| WORKER-6 code-only reachability cleanup | `continuous-safe`; stricter owner class for runtime removal | No worker hold for static proof; production removals use the owning phase gate |

For each full-scrape candidate, safe implementation and tests may proceed while
the current scrape runs, but production mutation waits for completion. The
worker is then held, one network candidate and one independently reversible
database/storage/query candidate are deployed as a predeclared dual-lane
package, one complete scrape is monitored, and the worker is held again for
independent iterate/reject/accept and commit/revert handling.

### Mandatory dual-lane scrape windows - effective 2026-07-28

Every full scrape must carry a network lane and a data lane. The network lane
owns proxy/rate/concurrency/retry/transport/request-count work. The data lane
owns PostgreSQL query/write/storage/WAL/ranking/post-process work. Each lane
gets its own baseline, target, rollback, metrics, and decision while sharing
the same scope, manifest, API, publication, and notification correctness gate.

Scrape `1268` evaluated the prior card. The next network candidate is designed
and available only in the bounded runner; the production card remains unarmed
until storage clearance and a passing bounded result. The worker therefore
remains held.

| Lane | Candidate | Baseline | Target |
|---|---|---|---|
| Network | **Bounded-only, production unarmed:** `candidate-800-32-5`; change only per-exit concurrency `4 -> 5`, do not repeat `1600/64/8` unchanged | Bounded `35.958` useful pages/s; scrape 1268 pure fetch `4:17:07.544` / `38.428` pages/s; writer drain separately `45:33.432` | Bounded >=`39.554` pages/s; future full pure fetch >=`42.271` pages/s; zero unrecovered/matched-control/public differences; amplification <=`1.50`; 429+503 <=`5%`; >=80% exits |
| Data/query | Publish band ranking snapshots/fingerprints before the final `api_response_cache` truncate/insert, retaining the notification contract and one atomic commit | Scrape 1268 functional notification completion in `101.76 s`, but 13 HTTP `504` and 20 `499` responses during the long cache lock | Zero representative-route failures; cache remains readable while band snapshot work waits; notification workset/owner/completion parity remains exact |

Bounded network canaries can normally overlap demonstrably disjoint
compaction/reclaim work. The current clean Trios v3 build is explicitly
classified as measurement-contaminating heavy I/O, so no Epic canary or worker
start may occur until `storage-reclaim-continuation` gives fresh clearance.

For attribution, score registered-user, band-discovery, and targeted-band
request/time deltas in the network lane, not the data lane. The executable
profile pins `00:10:00`, `00:05:00`, `00:05:00`, and `80`/`80` total lookup
budgets.

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
candidate only as part of a current dual-lane card through
`tools/fst-worker-dual-lane-runonce.sh`, verify the full public path, then
monitor exactly one complete scrape/post-process/publication/notification
decision and hold the worker again before another scrape.

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
  pages changed. At the `1262` decision, this live-fallback contract and
  insufficient capacity both blocked promotion.
- Final measured free space is `31,264,702,464` bytes, leaving a
  `13,883,523,072`-byte shortfall. No second scrape, destructive cleanup,
  alternate-drive work, rate increase, or provider fallback is authorized.
  `fstworker` remained held. WORKER-0.5 and WORKER-0.6 remained pending behind
  the then-unresolved WORKER-0A publication/capacity gate.
- Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/worker0a-final-live-ab-20260715T151317Z`.

**Post-1262 capacity recovery - accepted; worker still held 2026-07-16**

- The non-constraint `ix_rh_latest` partitioned family was retired after
  `SnapshotRankHistory` moved to a primary-key group/max latest-row plan.
  Exactly `45,547,339,776` database bytes were reclaimed in `0.30 s`.
- Final measured free space is `76,804,927,488` bytes. The
  `45,148,225,536`-byte scrape guard passes with `31,656,701,952` bytes of
  margin. `12/12` route/export/history/ranking fingerprints matched baseline,
  post-drop, and repeat, and `68/68` targeted tests passed.
- The worker was recreated but not started on
  `fstservice:post1262-capacity-7050ee93`; run-once remains true, restart is
  `no`, and container state is `created`.
- Capacity no longer blocks the next A/B. The worker remains held because the
  parent-owned live-fallback band best/worst songs parity gap still blocks an
  overall WORKER-0A promotion attempt.
- Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/post1262-capacity-recovery-20260716T021005Z`.

**Scrape 1263 stale-state recovery - publication safe, capacity hard-blocked
2026-07-25**

- Scrape `1263` completed `8,208/8,208` manifests with zero writer or
  publication-critical failures, then resumed selected post-process phases.
  The disk watchdog stopped the worker during rank-history snapshots when free
  space fell to `14,871,388,160` bytes. Docker reported exit `137` with
  `OOMKilled=false`; the stop grace period expired before failure, unfreeze,
  and offline-ledger finalization completed.
- A guarded transaction marked `1263` failed at
  `capacity_watchdog_abandoned`, preserved published `1236`, proved that
  `1263` owns zero published-source rows, cleared the database freeze, and
  reconciled the worker to offline with no current operation. Exact reverse
  SQL was transactionally restored and reapplied before the recovery was
  accepted.
- Service image `fstservice:failed-candidate-isolation-633e7583` contains
  `7558387f`, `21bd5f56`, and the failed-candidate derived-read isolation
  repair. The publication ledger is unfrozen on `1236`; mapped solo
  leaderboards remain byte-exact HTTP `200`, while unversioned
  ranking/history/export and band-song cache misses return stable HTTP `503`
  instead of exposing `1263`. No live `band_entries` fallback was observed.
- Final free space is `31,385,374,720` bytes. The measured
  `45,148,225,536`-byte scrape gate is short by `13,762,850,816` bytes.
  Same-drive spool/curl scratch is empty, retained evidence/path data cannot
  close the gap, the reclaim guard blocks below the emergency window, and
  every sufficiently large remaining index has a proven production owner.
- Recreating `pia-gluetun-8` restored the worker compose guard to `25/25`
  healthy unique PIA exits. Capacity is the remaining hard gate; `fstworker`
  stays exited with restart `no`, and no WORKER-0A run may resume.
- Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/stale-scrape-1263-recovery-20260725T153938Z`.

**Post-1263 residual capacity recovery - accepted; worker remains held
2026-07-25**

- Six low-scratch owner-card decisions retired `33` non-constraint indexes and
  reclaimed `17,174,200,320` database bytes. Final measured free space is
  `48,546,029,568` bytes, `3,397,804,032` bytes above the
  `45,148,225,536`-byte scrape boundary.
- Public mapped leaderboard output remained byte-exact HTTP `200`; isolated
  ranking/history/export and band-song routes remained stable HTTP `503`.
  `120/120` relevant tests and the Release build passed, and the worker proxy
  guard remains `25/25`.
- Commit `8db72081` prevents future startup/ranking publication from
  recreating the retired indexes. The existing
  `fstservice:worker0a-recovery-21bd5f56` worker remains exited with restart
  `no` and was not started. Before any later scrape, deploy `8db72081` or
  newer and rerun the measured capacity, proxy, and public-health guards.
- Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/fst-residual-capacity-20260725T161042Z`.

**Logical shadow retirement readiness - accepted; destructive action blocked
2026-07-25**

- Worker configuration remains
  `Features__WriteLogicalLeaderboardVersions=false`. Code/config defaults now
  fail closed, and startup rejects an attempted true value until a future
  migration/promotion changes that contract.
- Scrapes `1261`, `1262`, and `1263` prove the disabled writer can complete
  `8,208/8,208` solo/band manifests with zero writer or
  publication-critical failures. They do not satisfy the destructive gate:
  each failed on capacity before global publication, and none produced a
  complete post-publish route/export/ranking/history comparison.
- No worker was started and no logical-shadow row was truncated. The exact
  `141,462,937,600`-byte target remains available after the next successful
  disabled-writer publication window. Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/logical-retire-20260725T2306Z`.

**SNAPSHOT-REUSE full-scrape A/B - code accepted, live capacity-blocked and reverted
2026-07-26**

- Auth persistence passed on the FST drive, and a bounded paired canary
  returned valid Epic JSON for `25/25` direct and `25/25` PIA requests with
  exact entry-array and structural parity. The canonical compose guard stayed
  `25/25` at 400 aggregate RPS with no AirVPN/direct fallback.
- A pre-scrape ancestry check rejected an incorrectly built registry-wrapper
  image before it allocated a scrape. That image attempted retired
  `ix_rh_latest`; the worker and exact backend were stopped, DB size/free space
  recovered, and all 13 public fingerprints remained exact. The corrected
  image was rebuilt from `FSTService/Dockerfile` at `919daa32` and verified to
  contain current snapshot/ownership code without the retired index string.
- Corrected candidate scrape `1264` completed `8,232/8,232` manifests,
  `592,460` pages, and `59,077,331` reported entries with zero incomplete
  scopes, parse failures, retry exhaustion, writer failures, locks, long
  queries, or public-health failures.
- Exact live classification was `5,815` changed, `36` new, `281` unchanged,
  and `42` explicit-empty solo scopes. The writer avoided `219,427` physical
  rows, and zero unchanged scope had scrape-`1264` physical rows. Actual-run
  calibration estimates only `78,765,704` physical bytes and about
  `166,448,926` WAL bytes avoided.
- Snapshot relations still grew `15,552,274,432` bytes. Network/writer time
  was about `23,247 s`, `+4.1%` versus candidate `1262`, at 30.7 RPS. The
  one-minute monitor recorded zero health failures; peak worker/Postgres RSS
  was about `2.76/8.63 GiB`, WAL grew `97,876,358,577` bytes, and temp bytes
  did not grow.
- After writer drain deleted the `9.27 GB` band spool, only
  `32,390,148,096` bytes remained. Both the `45,148,225,536` measured
  post-process requirement and `44,394,828,933` candidate estimate failed.
  The worker was stopped before rankings/global publication and `1264` was
  reconciled failed at `capacity_postwriter_guard`.
- Production compose and the held worker were restored to
  `fstservice:worker0a-recovery-21bd5f56`, restart `no`. Published `1236`
  remains unfrozen, `1264` owns zero published-source rows, all 13
  route/export/history/ranking fingerprints match baseline, and final stable
  leaderboard p95 is within `8.46%` of baseline.
- Decision: code/readiness remains accepted default-off; live promotion is
  capacity-blocked and reverted. Do not rerun or restore scheduling until the
  same-drive post-writer capacity gate passes. Logical writes stayed disabled,
  no logical-shadow row was truncated, and the retirement gate remains
  `NOT_CLEARED`.
  Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/snapshot-reuse-live-ab-20260726T032124Z`.

**BAND-SONG-PROJECTION capacity recovery - accepted; worker remains held
2026-07-26**

- Retired only the stale optional `band_song_team_rankings*` data after exact
  ownership, live fallback/fail-closed parity, rollback, archive, and rebuild
  proof. The transaction reclaimed `28,315,533,312` database bytes while
  retaining schema, indexes, state, and an exact same-drive data archive.
- Final free space is about `58.97 GB`. The measured baseline scrape guard now
  has `13,822,787,584` bytes of margin and the SNAPSHOT-REUSE estimate has
  `14,576,143,227` bytes. The generic seven-day capacity alert remains.
- `fstservice` now runs `fstservice:band-song-retire-3ac2a7c9`; service, web,
  Postgres, `/readyz`, shell, and `/api/service-info` are healthy. All `24/24`
  representative band route bodies/statuses remained exact after truncate.
- `fstworker` remains `created` on
  `fstservice:worker0a-recovery-21bd5f56`, restart `no`. It was not started.
  The next SNAPSHOT-REUSE attempt must build a current-source worker image,
  rerun auth, `25/25` proxy, capacity, and full-public-path guards, then run
  the single parent-owned candidate window.
- Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/band-song-projection-retirement-20260726T103231Z`.

**SNAPSHOT-REUSE capacity-ready retry - rejected/reverted; worker held
2026-07-26**

- Scrape `1265` ran exactly once on
  `fstservice:snapshot-reuse-efdd70b8`, restart `no`, with logical writes
  disabled and only physical snapshot reuse enabled.
- Auth refresh and `25/25` authenticated direct/PIA parity passed. The
  canonical pool stayed at 25 healthy unique PIA exits, 400 aggregate RPS, two
  RPS and one in-flight request per exit, with no AirVPN fallback.
- The run completed all `8,232` manifests, `592,506` pages, and
  `59,081,828` reported entries with zero incomplete, parse, retry-exhausted,
  writer, or publication-critical failures. Three five-minute enrichment
  timeouts remained correctly classified best-effort.
- Exact solo reuse was `273` scopes / `218,892` rows; no unchanged scope wrote
  scrape-`1265` physical rows. Network/writer time improved to about
  `19,890 s`.
- The post-writer guard passed at `48,613,908,480` free bytes. Band
  maintenance then completed in `5:16:46.669`, but ranking snapshots crossed
  the declared `14,571,150,203` safety floor at `13,144,125,440` free bytes.
  The monitor stopped the worker before global publication.
- Recovery reconciled `1265` failed, unfroze published `1236`, restored the
  prior service/worker images and flag-off configuration, and recreated
  `fstworker` held with restart `no`. Post-cleanup nominal guards pass at about
  `48.78 GB` free, but scheduling remains held because the failed run proved
  that model insufficient through publication. The observed full-run model
  requires `60,392,999,803` start bytes, `11,616,701,307` above current free
  space.
- Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/snapshot-reuse-live-ab-20260726T110731Z`.

**POST-1265-LOW-SCRATCH capacity recovery - accepted; worker held
2026-07-27**

- No additional scrape ran. The recovery retired only four dormant
  non-constraint secondary index trees from the startup-rejected logical
  shadow, reclaiming `18,289,049,600` database bytes.
- Immediate free space reached `67,148,181,504` bytes. The corrected
  `60,392,999,803`-byte scrape-`1265` start requirement now passes with about
  `6.75 GB` of margin.
- All logical rows and 20 primary-key constraints remained, `13/13` public
  fingerprints matched, `119/119` targeted tests and the Release build passed,
  and service/web/Postgres remained healthy.
- `fstworker` remains held with restart `no`; snapshot reuse remains
  default-off and rejected. Capacity recovery alone is not promotion or retry
  authorization.
- Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/post-scrape-1265-capacity-recovery-20260727T0011Z`.

**PROXY-RETUNE publication-disabled tuning - candidate selected 2026-07-27**

- Preflight preserved published `1236` unfrozen with no active scrape,
  ungranted lock, or long query. The corrected
  `60,392,999,803`-byte start guard passed at `67,147,284,480` free bytes,
  leaving `6,754,284,677` bytes of margin. Auth refresh, `25/25` unique PIA
  guard, and paired direct/proxy entry parity passed.
- Eleven lower-to-higher matched stages each used the same 225 published
  page-zero scopes across all nine instruments and 1,500 curl-primary sends.
  The matrix covered per-exit RPS `2/4/8/16/32`, concurrency `1/2/4`, and
  global ceilings `400/800/1600`.
- The selected `800 / 32 / 4` candidate returned `1,500/1,500` valid JSON,
  `34.80` useful pages/s, `2.539/3.845 s` p95/p99, zero block, timeout, 503, or
  429 responses, and `1.0000` wire sends per useful response. Its doubled
  `3,000`-send repeat was also 100% valid with zero classified failures and
  `32.04` useful pages/s.
- The `1600` global ceiling was non-binding because 25 exits at 32 RPS cap
  effective starts at 800. All 25 exits remained qualified; no new exit was
  quarantined. Peak isolated canary memory/PIDs were about `360 MB / 121`.
- Decision: deploy only `800` global RPS, `32` RPS per exit, and concurrency
  `4` for exactly one full scrape with snapshot reuse and logical writes off.
  Acceptance remains pending complete manifests, writers, post-process,
  rankings, publication/unfreeze, public parity, and unchanged logical hashes.
- Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/proxy-retune-disabled-writer-baseline-20260727T004228Z`.

**PROXY-RETUNE + DISABLED-WRITER-BASELINE - network accepted as research;
publication rejected 2026-07-27**

- Scrape `1266` used `800` global RPS, `32` RPS per exit, concurrency `4`,
  curl primary transport, 25 qualified unique PIA exits, snapshot reuse off,
  logical writes off, and restart `no`.
- It completed `8,232/8,232` manifests, `592,631` pages, and `59,095,126`
  reported entries with zero incomplete scopes, parse failures, retry
  exhaustion, or writer failures. Network plus writer drain was
  `4:54:57.902`, `11.02%` below scrape `1265`; useful pages/s improved
  `12.41%` to `33.49`.
- Core transport recorded `613,040` wire sends and `18,208` isolated blocks
  (`2.97%`, `1.0344` sends/reported page), with no `429` or `503`. The
  60-second monitor captured `999` samples, zero public-health failures,
  `8.33 GiB` peak worker RSS, `10.51 GiB` peak Postgres RSS, and a minimum
  `18,134,577,152` free bytes, still above the `14,571,150,203` safety floor.
- Band Duets ranking hit PostgreSQL `40P01` when two ranking paths concurrently
  ran the same schema ensure. A same-run repair rebuilt `4,477,133` Duets
  ranking rows before publication. Commit `6651ebd9` adds a transaction-scoped
  advisory schema lock and one deadlock retry. Commit `4121e7e5` adds a real
  concurrent rebuild test and makes retry exhaustion fail the
  publication-critical `ComputeRankings` phase after remaining band types are
  observed, rather than leaving a success-shaped partial result.
- The worker later remained in deferred registration/rivals processing for
  more than six hours without a phase deadline or terminal publication
  decision. It completed 22 deferred rival computations and 16 deferred
  backfills, so it was progressing but unbounded; the generic 15-second
  liveness heartbeat also masked the absence of a phase transition. Incident
  recovery stopped it at `18:39:11 UTC`, dry-ran an exact rollback, marked
  `1266` failed at `post_process_no_progress_abandoned`, preserved/unfroze
  `1236`, cleared the worker operation, and confirmed zero candidate
  published-source rows, active queries, or locks.
- Explicit post-process phase/item heartbeats now advance independently from
  liveness, deferred registration sync is best-effort and bounded to 30
  minutes by default, and
  `tools/fst-worker-no-progress-watchdog.mjs` applies a configurable 45-minute
  idle gate while deferring for worker-owned PostgreSQL activity. On timeout it
  stops the worker, writes rollback SQL, performs guarded fail/unfreeze/offline
  recovery, and sends/renders a visible report.
- Validation passed `261/261` changed-surface tests, `12/12` focused incident
  tests, `7/7` watchdog/e-mail tool tests, and the Release build. The full
  suite passed `2,082/2,087`; all five failures exactly matched the prior
  scrape-1263 baseline, and the one load-sensitive failure passed on rerun.
- Production reverted to `400 / 2 / 1`. `fstservice` runs and `fstworker` is
  created-held on `fstservice:scrape1266-recovery-4121e7e5`; restart remains
  `no`. The corrected guard blocks another scrape at `32,507,674,624` free
  bytes versus `60,392,999,803` required (`27,885,325,179` short).
  The tuning candidate is not promoted without successful publication.
- Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/proxy-retune-disabled-writer-baseline-20260727T004228Z`
  and
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/stale-scrape-1266-recovery-20260727T184133Z`.

**SCRAPE-1267 guarded publication - accepted 2026-07-28**

- The current recovery image `fstservice:scrape1266-recovery-4121e7e5`
  contains commits `6651ebd9` and `4121e7e5`. Scrape `1267` used the accepted
  `800` global RPS, `32` RPS per exit, concurrency `4`, curl primary transport,
  snapshot reuse off, logical writes off, and restart `no`.
- Auth refresh preserved mode `0600`; `25/25` direct and `25/25` PIA Epic
  responses were valid and entry-exact before start. The canonical compose
  guard passed with 25 healthy unique exits.
- The run completed `8,232/8,232` manifests, `592,731` pages, and
  `59,105,529` reported entries with zero incomplete, parse, retry-exhausted,
  writer, or publication-critical failures. Three five-minute enrichment
  timeouts remained correctly classified best-effort.
- Network plus writer drain was `5:02:22.661`: `8.79%` faster than scrape
  `1265` and `2.51%` slower than scrape `1266`. Useful pages/s was `32.67`.
  Transport recorded `629,426` wire sends and `19,202` blocks (`3.05%`),
  zero `429`, and zero primary `503`.
- The serialized band schema setup completed with no `40P01`. The
  deferred-registration/rivals phase completed two rival items in `2,653.1 s`
  and did not silently stall. The DB-aware watchdog ended terminal without
  recovery.
- Scrape `1267` published atomically and unfroze. It owns `6,174` complete
  published mappings and `39,937,029` mapped rows. Two post-publish captures
  were HTTP `200` and `13/13` byte-exact.
- The 60-second monitor captured `721` samples and zero public-health failures.
  Minimum free space was `18,203,201,536`, retaining
  `3,632,051,333` bytes above the floor. Final measured free space was
  `41,145,516,032`, so another full scrape is capacity-blocked.
- The logical shadow retained exact hashes for `39,820,273` current and
  `194,171,215` version rows, zero scrape-`1267` touches, zero metric rows,
  and zero positive read-counter deltas. Its destructive parity gate is
  **CLEARED**; no truncate ran in this phase.
- The worker is held exited with restart `no`. Production rate settings were
  restored to `400 / 2 / 1`. `pia-gluetun-6` failed PIA TLS requalification
  after the run and remains stopped; service, web, Postgres, and published
  `1267` remain healthy.
- Evidence:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/scrape-1267-guarded-publication-20260727T201218Z`.

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
**Provider rule:** Never widen entitlement use. Throughput may increase only
through the sequential named guard profiles below, stopping at the first failed
correctness or effective-throughput gate.

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

Do not run a full Cartesian matrix. Escalate one step at a time and do not skip
an unqualified step. Canaries run beside normal production only when the
combined production plus canary budget remains at or below the active profile;
otherwise run them while the worker is held.

| Matrix | Values |
|---|---|
| Healthy unique exits | 1, 4, 8, 16, 26, then 30 only if actually unique/healthy |
| Historical scrape-1268 sequence | `candidate-800-32-4`, rejected `candidate-1600-64-8`; `candidate-2880-128-16` not run |
| Next bounded-only profile | `candidate-800-32-5` = unchanged `800/32`, concurrency `4 -> 5` only |
| Assignment | production least-in-flight; bounded fixed-balanced assignment is conservative and recorded explicitly |

Each step must pass all of these gates before the next step:

- zero unrecovered scope failures, missing manifests, or matched cross-exit
  payload-control differences; repeated temporal live variants are recorded
  separately;
- at least 10% higher useful RPS than the previous qualified step;
- wire-send retry amplification <=`1.50`;
- combined `429` plus `503` <=`5%` of wire sends and no three consecutive
  one-minute windows above `10%`;
- at least `80%` of preflight-healthy unique exits remain usable;
- CDN-block rate has no fixed ceiling, but a higher rate is accepted only when
  useful RPS still improves and all correctness gates pass.

**Execution sequence**

1. Preflight Docker/Postgres/publication/freeze, proxy health, egress
   uniqueness, and current production request rate.
2. Reserve the active production profile first; canaries may use only
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
