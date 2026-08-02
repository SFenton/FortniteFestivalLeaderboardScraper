# Proxy Rotation on CDN Block — Design Document

**Date:** April 8, 2026
**Status:** Proposed

## July 13, 2026 production recovery qualification

The active production intent is now the canonical 30-service Private Internet
Access overlay at
`/home/sfenton/Docker/FestivalServiceTracker/docker-compose.pia-30.yml`.
Docker health alone is not accepted as proxy readiness.

The inserted WORKER-0A recovery prerequisite established:

- all 30 containers used one valid PIA/OpenVPN account configuration with no
  authentication errors;
- three sequential authenticated, low-rate Epic canary rounds found 25 exits
  that returned valid leaderboard JSON every time;
- `pia-gluetun-16` and `pia-gluetun-23` never returned valid Epic JSON, while
  `pia-gluetun-11`, `pia-gluetun-12`, and `pia-gluetun-20` flapped between valid
  JSON and timeout;
- the effective worker pool temporarily excludes those five exits but retains
  all 30 canonical service definitions for later requalification;
- the effective 25 exits passed Docker health, Epic DNS, Gluetun control,
  HTTP-proxy readiness, and 25/25 unique hashed-egress checks;
- a publication-disabled matched slice issued 25 direct and 25 proxied page
  requests across eight instruments. All 50 responses were valid Epic JSON,
  and all 25 proxied entry arrays exactly matched their direct controls;
- aggregate Epic pacing is capped at 400 requests/s, no more than 16 requests/s
  per effective unique exit. DOP was not increased.

No AirVPN fallback was promoted. Direct access was valid in the bounded
canaries but is not part of the effective production pool.

The first guarded full-scrape retry exposed an endpoint-specific gap in that
qualification: the 25 exits had qualified against leaderboard pages, but all
observed `events-discovery` requests were CDN-blocked. Season-window discovery
now has a 45-second deadline and falls back to the already persisted
season-window cache (or existing bounded probing when no cache exists). This
does not add direct access, AirVPN fallback, a new exit, or a higher request
budget.

Controlled `Scraper:RunOnce=true` full-scrape workers also omit the
continuously polling `RegistrationBackfillWorker`. A live retry showed that one
queued registration could otherwise start V2 POST lookups across the full song
catalog while the candidate scrape was establishing its network path, causing
endpoint-wide CDN blocks before any core scope completed. Normal scheduled and
dedicated registration-sync workers retain that service.

The pool now also supports
`Scraper:ProxyMaxRequestsPerSecondPerEndpoint`. The global production ceiling
remains `400` requests/s, while each effective exit is paced independently at
`1` request start/s so DOP bursts cannot concentrate the shared budget on an
exit faster than its qualified rate. Publication-disabled five-round canaries
at requested per-exit ceilings of `1`, `2`, `4`, and `8` each returned
`125/125` valid leaderboard responses. Production also limits each exit to one
simultaneous request: a 1-RPS candidate without that concurrency bound allowed
slow 30-second requests to accumulate up to 19 in flight on one exit and
stalled after 65 leaderboards.

Proxy-routed Epic requests also disable upstream connection reuse. The matched
curl canaries opened a fresh connection for every request and remained valid,
while the bounded .NET candidate still stalled with one request in flight per
exit after reusing its proxy connections. `Connection: close` plus zero pooled
connection lifetime makes the production transport match the qualified path.

The final transport candidate uses curl as the primary proxy-routed Epic
transport rather than only as a post-failure fallback. This exactly matches the
qualified canary client while retaining the same proxy lease, per-exit rate,
per-exit concurrency, cooldown, and global 400-RPS accounting. Request and
response scratch files use `/app/data/curl-transport` on the FST drive and are
deleted after each request.

## July 27, 2026 publication-disabled throughput retune

The recovery configuration of 400 global RPS, 2 RPS per exit, and one
simultaneous request per exit remained the full-scrape baseline through scrape
`1265`. A new publication-disabled matched matrix used the same 225 page-zero
scopes across all nine instruments, all 25 qualified unique PIA exits, curl
primary transport, HTTP/1.1, fresh connections, and 1,500 sends per stage.
Lower stages passed before higher limits were attempted.

| Per-exit RPS | Per-exit concurrency | Global ceiling | Valid JSON | Useful pages/s | p95 / p99 | Block / timeout / 503 / 429 | Wire sends/useful |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 2 | 1 | 400 | 100.00% | 7.86 | 2.502 / 4.523 s | 0 / 0 / 0 / 0 | 1.0000 |
| 4 | 1 | 400 | 100.00% | 7.99 | 2.518 / 4.054 s | 0 / 0 / 0 / 0 | 1.0000 |
| 8 | 1 | 400 | 100.00% | 8.51 | 2.519 / 4.077 s | 0 / 0 / 0 / 0 | 1.0000 |
| 16 | 1 | 400 | 100.00% | 7.59 | 2.571 / 4.137 s | 0 / 0 / 0 / 0 | 1.0000 |
| 16 | 2 | 400 | 100.00% | 12.58 | 2.804 / 5.773 s | 0 / 0 / 0 / 0 | 1.0000 |
| 16 | 4 | 400 | 99.87% | 15.47 | 2.569 / 7.828 s | 0 / 1 / 0 / 0 | 1.0013 |
| 32 | 1 | 800 | 99.93% | 5.02 | 2.611 / 6.352 s | 0 / 1 / 0 / 0 | 1.0007 |
| 32 | 2 | 800 | 99.93% | 12.02 | 2.766 / 5.459 s | 0 / 0 / 0 / 0 | 1.0007 |
| 32 | 4 | 400 | 99.93% | 31.83 | 2.608 / 4.130 s | 0 / 0 / 0 / 0 | 1.0007 |
| 32 | 4 | 800 | 100.00% | 34.80 | 2.539 / 3.845 s | 0 / 0 / 0 / 0 | 1.0000 |
| 32 | 4 | 1600 | 99.93% | 34.45 | 2.686 / 4.386 s | 0 / 0 / 0 / 0 | 1.0007 |

The selected one-scrape candidate is 800 global RPS, 32 RPS per exit, and four
simultaneous requests per exit. A doubled 3,000-send repeat returned 100% valid
JSON at 32.04 useful pages/s, 2.496 s p95, 4.491 s p99, zero blocks, timeouts,
503s, or 429s, and 1.0000 wire sends per useful response. The 1,600 ceiling was
non-binding because 25 exits at 32 RPS can start at most 800 requests/s.

All 25 exits remained qualified; no additional exit was quarantined. Public
service health, Postgres locks/long queries, authentication, and matched
direct/proxy entry parity remained safe. This selects the candidate for exactly
one full disabled-logical-writer baseline; it is not a production acceptance
until that scrape completes strict manifests, post-processing, rankings,
publication, unfreeze, and public/logical parity.

Evidence:
`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/proxy-retune-disabled-writer-baseline-20260727T004228Z`.

### Full disabled-writer baseline decision

Scrape `1266` ran the selected `800 / 32 / 4` limits exactly once with curl
primary transport, snapshot reuse off, logical writes off, and restart `no`.
The network/writer boundary completed all `8,232/8,232` manifests,
`592,631` reported pages, and `59,095,126` reported entries with zero writer,
parse, retry-exhaustion, or incomplete-scope failures.

Network plus writer drain took `17,697.902 s` (`4:54:57.902`), versus
`19,890 s` (`5:29:50`) for scrape `1265`: wall clock improved `11.02%` and
useful pages/s improved `12.41%` from `29.79` to `33.49`. Core transport
recorded `613,040` wire sends, `18,208` isolated CDN blocks (`2.97%`), and
`1.0344` wire sends per reported page, with no `429` or `503`. Two normal
two-hour token rollovers caused bounded `401` retries; all manifests remained
complete. All 25 exits stayed healthy and unique.

The full baseline did **not** publish. Concurrent ranking schema setup
deadlocked Band Duets once; a bounded same-run repair rebuilt
`4,477,133` Duets ranking rows before publication, and commit `6651ebd9`
serializes future schema setup with an advisory transaction lock plus one
deadlock retry. Commit `4121e7e5` additionally rejects exhausted per-type
ranking failures as publication-critical. The old worker then spent more than
six hours in deferred registration/rivals processing without a phase deadline.
It was making slow item progress, but generic liveness heartbeats hid the lack
of a terminal phase transition. Recovery marked `1266` failed at
`post_process_no_progress_abandoned`, preserved and unfroze published `1236`,
and confirmed zero candidate published-source rows, active worker queries, or
locks.

Decision: the `800 / 32 / 4` network result is a research win but is not
promoted without a successful publication. Production reverted to
`400 / 2 / 1`; `fstservice` and the created-held `fstworker` use
`fstservice:scrape1266-recovery-4121e7e5`, and worker restart is `no`.
Post-process now has explicit progress heartbeats, a 30-minute
deferred-registration timeout, and a DB-aware 45-minute autonomous no-progress
watchdog. A corrected full-scrape guard fails at `32,507,674,624` free bytes
versus `60,392,999,803` required, so another scrape is capacity-blocked.

Internal curl timeouts are classified as transient transport failures and
retried; only the caller/host cancellation token ends the pass. Controlled
run-once compose also sets `restart: "no"` so Docker cannot restart a completed
or failed one-shot worker into a second scrape.

### Scrape 1267 publishing qualification

Scrape `1267` reran the accepted `800 / 32 / 4` candidate exactly once with
curl primary transport, 25 qualified unique PIA exits at start, snapshot reuse
off, logical writes off, and restart `no`. It published successfully and
unfroze public reads.

- Network plus writer drain completed `8,232/8,232` manifests,
  `592,731` pages, and `59,105,529` reported entries in `5:02:22.661`.
  This was `8.79%` faster than scrape `1265` (`5:29:50`) and `2.51%` slower
  than scrape `1266` (`4:54:57.902`). Useful pages/s was `32.67`, `9.67%`
  above `1265` and `2.43%` below `1266`.
- Final transport counters recorded `629,426` wire sends and `19,202` isolated
  CDN blocks (`3.05%`), with zero HTTP `429` or `503` primary responses.
- The advisory schema lock serialized concurrent band ranking schema setup;
  no PostgreSQL `40P01` or band-type ranking failure occurred. All 10
  publication-critical phases completed.
- The bounded deferred-registration phase advanced through two rival items
  and completed in `2,653.1 s`, below the configured 45-minute no-progress
  backstop. The watchdog reached terminal scrape completion without recovery.
- The 60-second monitor captured `721` samples with zero public-health or
  capacity-stop ticks. Minimum free space was `18,203,201,536` bytes,
  `3,632,051,333` above the declared safety floor.
- Atomic publication advanced to `1267`; two public captures returned HTTP
  `200` and matched `13/13` normalized fingerprints.

Decision: the `800 / 32 / 4` candidate is **accepted for full publishing
throughput**. Scheduling remains held because the post-publish scrape capacity
guard is blocked at `41,145,516,032 < 60,392,999,803` bytes. Production
rate settings were restored to the held `400 / 2 / 1` baseline so no later
worker start can occur accidentally. Effective exit `pia-gluetun-6` also
failed PIA TLS health after the run and remains stopped pending provider-exit
requalification; the published result and public path are unaffected.

Evidence:
`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/scrape-1267-guarded-publication-20260727T201218Z`.

### Scrape 1268 sequential qualification and full result

The next sequential matrix stopped at its first failed step:

- `candidate-800-32-4` passed the matched bounded calibration with
  `3,000/3,000` recovered requests, `35.96` useful pages/s, `1.0007`
  amplification, zero 429/503, zero payload variants, and 25/25 retained
  exits.
- `candidate-1600-64-8` reached `53.22` useful pages/s but the one-round
  harness stopped after two TLS failures were retried once and those first
  alternates returned CDN `403`. The accepted `800` canary also had two TLS
  failures and recovered both. The single repeated live-scope variant was
  observed 13 times through one fixed exit, with a 12:1 fingerprint split, so
  it did not prove cross-exit transport corruption. The old evidence package
  still failed closed because it could not prove recovery/correctness.
- `candidate-2880-128-16` was not run. Sequential qualification stops at the
  first failure.

Production therefore reran `candidate-800-32-4` with only an independently
reversible availability repair for `pia-gluetun-3`. The exit moved away from
an unreachable server pool to a measured-reachable endpoint and passed the
25/25 unique-egress guard. During the full run it participated in normal
cooldown/self-heal behavior and finished healthy.

Scrape `1268` completed `8,232/8,232` manifests with `592,849` useful pages.
Network plus writer drain was `5:02:40.563`, `0.10%` slower than scrape
`1267`; useful pages/s was `32.64`, `0.08%` lower. Final transport recorded
`640,081` wire sends, `18,987` CDN blocks (`2.97%`), one primary `503`, zero
`429`, `1.0797` amplification, no three consecutive bad one-minute windows,
and 25/25 healthy exits at decision. Correctness and safety passed, but the
10% useful-throughput target did not, so the network lane is **iterate**, not
promoted.

That combined boundary hid the actual transport result. `BandPageFetcher`
finished at `00:06:43.980Z`, `4:17:07.544` after the scrape start, for
`38.428` pure-fetch pages/s. Band writer drain then consumed `45:33.432`
before the manifest boundary. Full-run pure transport was `6.87%` faster than
the bounded `35.958` result; the apparent `32.64` deficit was entirely a
measurement-boundary mismatch. Future network decisions score pure fetch and
writer drain separately.

The historical request-count increase is a scope-semantics change rather than
retry growth. Scrape `1268` consisted of `401,504` solo pages plus `191,345`
complete Band Duets/Trios/Quad pages. Historical `~400k` totals were
effectively solo-only; wire sends are tracked separately.

Evidence:
`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/scrape-1268-dual-lane-20260728T184812Z`.

### Candidate 5 result and next smallest candidate

`candidate-800-32-5` changed one variable only: per-exit concurrency `4 -> 5`.
Global `800`, per-exit `32`, curl HTTP/1.1 fresh connections, production
least-in-flight selection, cooldowns, retry thresholds, and the PIA endpoint
set remain unchanged.

Bounded acceptance requires:

- at least `39.554` useful pages/s, exactly 10% over `35.958`;
- zero unrecovered responses after at most three recovery rounds, each using
  previously untried alternate exits with a 500 ms inter-round delay;
- zero failed near-simultaneous cross-exit payload pairs across 25 sampled
  scopes, with pair starts no more than 250 ms apart;
- retry amplification <=`1.50`, combined 429+503 <=`5%`, no three consecutive
  one-minute windows above `10%`, and >=`80%` exit retention;
- zero publication/shared-state or representative public-route differences;
- peak canary memory <=`768 MiB`, peak PIDs <=`300`, and zero scratch residue.

The storage-cleared live canary passed every correctness, public-health, exit,
error, and resource gate:

- `3,000/3,000` recovered responses and `1.00067` amplification;
- zero 429/503/CDN blocks and 25/25 exits retained;
- 25/25 near-simultaneous cross-exit payload pairs exact;
- `431 MiB` peak memory, 149 PIDs, and zero scratch residue;
- 20/20 continuous public-health ticks green with publication unchanged at
  scrape `1268`, unfrozen.

Strict recovered useful throughput was `39.314` pages/s (`+9.33%`) against
the required `39.554`; it missed by `0.240` pages/s. Primary-only throughput
was `39.629`, but recovery wall time is deliberately part of the gate.
`candidate-800-32-5` is rejected on performance only.

Preflight found `pia-gluetun-21` unable to negotiate its Virginia UDP tunnel.
The independently reversible Virginia TCP override restored health, passed
the 25/25 unique-egress guard, and delivered 120/120 valid candidate responses
at `1.153 s` p95. Retain this availability repair.

The next smallest profile is bounded-only `candidate-800-32-6`. It changes
only per-exit concurrency `5 -> 6`; all rates, transports, routing, recovery,
controls, and gates remain identical. One additional slot is justified because
c5 safely narrowed the target miss to `0.61%` while remaining well below the
resource caps. A fresh storage clearance is required before its live canary.

Freshness limits this qualification window to one c6 attempt. The current
bounded storage chunk may finish and checkpoint, but the longer storage lane
must then pause. If c6 fails or cannot start promptly at that safe boundary,
run the continuity scrape with accepted `candidate-800-32-4`; report that
network result as an accepted-baseline measurement rather than a promotion,
and resume candidate work only after terminal publication and notification
completion.

That fallback path was exercised by scrape `1269`. c6 did not run because the
safe storage boundary was not available promptly. Exact c4 completed the
network-plus-writer boundary in `5:01:08.141` at `32.819` useful pages/s, with
`640,250` sends, `18,918` blocks (`2.955%`), zero `429`/`503`, and `1.0797`
amplification. Pure fetch was about `4:15:37.141` / `38.663` pages/s. This is
accepted continuity-baseline evidence only; it does not clear the `42.271`
full-run promotion target, and c6 remains bounded-only.

After `1269` reached terminal publication, the continuity owner ran an
isolated c6 at `05:58:50-05:59:52 UTC`. It reached `53.022` useful pages/s,
recovered `3,000/3,000`, and left publication unchanged, but 24/25 matched
payload-control pairs were invalid after 38 control-stage CDN `403`s. That is
the authoritative c6 decision: **reject on correctness**.

The continuity owner began its accepted-c4 fallback workflow at `06:02:07
UTC`. A second owner received the clearance without the first terminal
decision and began a duplicate c6 at `06:02:25 UTC`. The fallback worker
became active at `06:02:37 UTC`, allocating scrape `1270` and adding
concurrent Epic traffic. The duplicate run's apparent `42.242` pages/s is
invalid; it had 16 unrecovered responses, 63 CDN blocks, 25/25 invalid
controls, and `1269|unfrozen|1269 -> 1269|frozen|1270`.

The worker was stopped, `1270` was guardedly marked failed with zero candidate
mappings/queries/locks/maintenance, published `1269` was preserved/unfrozen,
and the worker ledger returned offline. Public routes remained HTTP `200`.
The duplicate attempt is excluded; no further c6 attempt is justified without
a newly named payload-control/transport hypothesis.

The corrected exclusive fallback then ran as scrape `1271` on exact accepted
`candidate-800-32-4`. It fetched `593,058` useful pages in `4:05:05.601`
(`40.329` pages/s), followed by `43:30.374` of band writer drain for a combined
`4:48:35.975`. Final transport was `650,751` sends, `18,358` blocks
(`2.821%`), `1.0973` amplification, zero strict-fetch `429`/`503`, and no three
bad one-minute windows. The run completed publication, notifications, and
`13/13` settled parity, but pure fetch remained `4.59%` below the
`42.271` promotion target. This is accepted continuity-baseline evidence only;
c6 remains rejected on correctness and no new network candidate is armed.
Evidence:
`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/scrape-correction-followup-20260730T055228Z`.

### Scrape 1272 rejection and scrape 1273 `1600 / 64 / 8` acceptance

Scrape `1272` reran exact `800 / 32 / 4` with the generation-cache data lane.
It completed `8,364/8,364` manifests and zero writer failures, but pure fetch
was `4:14:01.298` and network plus writer was `4:51:57.820`. Those results
were `3.64%` and `1.17%` slower than scrape `1271`, so the unchanged network
lane was rejected. The scrape later failed closed in registered-user refresh;
that publication failure does not invalidate the completed network
measurement.

Scrape `1273` then qualified exact `candidate-1600-64-8` through a complete
publishing window:

- global/per-exit/concurrent limits were `1600 / 64 / 8`;
- aggregate DOP was `200`, initial DOP `50`, learned max DOP `200`, and page
  concurrency `50`;
- pure fetch completed in `2:54:13.380` at `57.006` useful manifest pages/s;
- network plus writer completed in `3:01:27.380` at `54.733` useful manifest
  pages/s;
- those boundaries improved `28.92%` and `37.13%` over scrape `1271`;
- all `8,364/8,364` manifests completed with `595,897` manifest pages,
  `59,414,653` reported entries, and zero writer failures;
- transport recorded `607,268` logical requests, at least `637,250` wire
  sends, `28,491` CDN blocks (`4.47%`), `1.0494` amplification, zero exhausted
  retry chains, zero unauthorized responses, zero HTTP `429`, and 17 primary
  HTTP `503`;
- publication `6`, notifications, post-publication registration work, public
  route parity, and the run-once worker exit all completed successfully.

Decision: `candidate-1600-64-8` is the accepted full-scrape network baseline.
The higher block rate is an accepted throughput trade because retries,
manifests, publication, and public correctness all passed. `800 / 32 / 4`
remains the independently reversible rollback profile. Automatic scheduling
remains held until a new paired data-lane card is armed; this acceptance does
not authorize an unpaired worker start.

Evidence:
`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/scrape-1273-dual-lane-20260801`.

### Scrape 1274 initial-ramp candidate

The next paired full scrape keeps the accepted `1600 / 64 / 8`, aggregate DOP
`200`, learned maximum `200`, page concurrency `50`, curl transport, and exact
25-exit pool unchanged. Its only network variable is initial DOP `50 -> 64`.

- **Hypothesis:** reduce the first fifteen minutes of AIMD ramp time without
  increasing retry amplification, three-minute error windows, or public load.
- **Target:** first-fifteen-minute useful request rate improves at least `5%`;
  terminal pure fetch and network-plus-writer must not regress more than `5%`
  against scrape `1273`.
- **Correctness:** exact manifests, zero writer failures, zero exhausted retry
  chains, healthy exits, successful publication/notifications, and continuous
  public health remain mandatory.
- **Rollback:** restore initial DOP `50`; all other accepted network values are
  identical.

The named profile is `candidate-1600-64-8-initial64`. It is paired only with
the `catalog-path-notification-source-cut` data profile and does not authorize
an unpaired worker start.

The bounded runner now atomically creates
`/home/sfenton/Docker/FestivalServiceTracker/.fst-bounded-network-canary-active.json`
after proving the worker is offline and removes it only at terminal cleanup.
Every autonomous continuity or candidate worker start must fail closed while
the sentinel exists. Site freshness does not authorize concurrent traffic
inside an active bounded-canary window. The runner independently polls
`fstworker` every 250 ms and stops its own stage container if a noncompliant
start still occurs.

Future network candidates use scrape `1273` as the matched baseline:
`57.006` pure-fetch pages/s and `54.733` pages/s through writer drain. Writer
drain remains separately reported. A candidate must still preserve exact
scope/payload parity, retry amplification <=`1.50`, combined `429`+`503`
<=`5%`, at least `80%` healthy unique exits, successful publication, and
continuous public health.

The canary now has repository-owned buildable source in
`tools/FstNetworkCanary`, bounded distinct-alternate recovery,
app-connect/start-transfer timing, and matched payload controls. A service
regression test proves TLS -> alternate CDN `403` -> third-exit success on the
production least-in-flight path.

The production wrapper and compose guard remain unchanged. Candidate 6 stays
bounded-only and retry-blocked until the concurrent-start owner acknowledges
the sentinel protocol and a new explicit boundary is issued. At that later
boundary, use a new empty FST-drive evidence directory:

```bash
python tools/fst-network-bounded-canary.py \
  --network-profile candidate-800-32-6 \
  --out-dir /mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/<window>/candidate-800-32-6 \
  --request-count 3000 \
  --prior-useful-rps 35.95782174836861
```

Evidence:
`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/network-candidate-800-32-6-20260730T060051Z`.

Transport fallback responses are only treated as recovered when they are not
another CDN block and not retryable `429`/`5xx` status. This prevents a curl
`503` observed after a transient process/tunnel failure from becoming a final
scope `HttpFailure`; the original request remains in the bounded retry loop.

Epic JSON `404 com.epicgames.events.event_not_found` on page zero is treated as
a legitimate empty solo or band scope. This covers newly cataloged songs whose
leaderboard event has not been created yet without weakening later-page gap,
malformed-response, or retry-exhaustion gates.

The shared `PageFetcherBase` applies the same rule used by
`GlobalLeaderboardScraper`, preventing the independent flat-parallel band
fetcher from classifying the identical new-song response as an HTTP failure.

### Fail-closed worker deployment

`Scraper:ExpectedProxyEndpointCount` makes `ProxyPool` reject missing,
incomplete, non-unique, or misaligned proxy/control/provider/container arrays.
Production's base worker env carries the expected count, so omitting the PIA
overlay cannot silently start a direct or partial-pool worker on a guard-aware
image.

Inspect the held baseline without starting a worker:

```bash
cd /home/sfenton/FortniteFestivalLeaderboardScraper
tools/fst-worker-compose-guard.sh \
  --throughput-profile baseline-up-to-800-32-4 \
  --check
```

The guard resolves only the canonical PIA overlay, verifies 30 canonical
services and the configured effective count, enforces aligned metadata and a
named fail-closed throughput profile, then proves live DNS, control, HTTP
proxy, and unique hashed egress before recreation. Candidate profiles require
exact values and may start only through the run-once path. Every run-once
config also requires a named data profile. It never prints public addresses
or credentials.

The rollback image must retain the durable notification marker/scope-plan
contract and database compatibility constraint. Revert candidate flags and
network values, not to a pre-contract worker binary.

`fst-worker-dual-lane-runonce.sh` atomically selects the exact network values,
the `notification-db-only` data profile, `RunOnce=true`, and scrape-1267
registered-phase budgets. The data profile also pins the full pipeline
(`EnabledPhases=None`), registered notification scope, player/band song and
ranking lanes, projection refresh, and bounded-scope-only recovery. Use it for
both preflight and recreation:

```bash
tools/fst-worker-dual-lane-runonce.sh \
  --network-profile candidate-800-32-4 \
  --check
tools/fst-worker-dual-lane-runonce.sh \
  --network-profile candidate-800-32-4 \
  --recreate
```

Use `candidate-1600-64-8` as the accepted network profile for the next paired
full scrape. Do not advance to `candidate-2880-128-16` without a new bounded
qualification and paired full-scrape card. `candidate-800-32-5` remains
rejected because it missed the bounded performance gate.
`candidate-800-32-6` remains rejected because its isolated attempt failed
matched-control correctness; a duplicate attempt was also invalidated by
concurrent scrape `1270`. Do not repeat c6 unchanged.

Recovery evidence:
`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/autonomous-artifacts/proxy-recovery-20260713T171754Z`.

## Problem Statement

CDN blocks from Epic's API are 403 responses with HTML bodies (not JSON). The current `ResilientHttpExecutor` detects these and runs a probe loop on the **same IP** with escalating backoff (500ms → 1s → 2s → 5s → 10s → 15s → 30s → 45s → 60s, up to 30 retries ≈ 7 minutes). If all retries are exhausted, a `CdnBlockedException` escapes to the scrape pass level, the pass is aborted, and the next attempt is 4 hours later.

**Key insight:** CDN blocks are IP-based. If we can change our exit IP, we can escape the block immediately.

---

## Current CDN Block Handling

### Detection

In `ResilientHttpExecutor.SendAsync()`:

```csharp
if (statusCode == 403)
{
    var body = await res.Content.ReadAsStringAsync(ct);
    bool isCdnBlock = !body.TrimStart().StartsWith('{');  // non-JSON = CDN
    if (isCdnBlock) {
        // Increment metrics & launch probe
        throw new CdnBlockedException(...);
    }
    // Otherwise: JSON 403 → return to caller (API error like no_score_found)
}
```

### Probe Mechanism

- `LaunchCdnProbe()` starts a background task protected by `_cdnGate` semaphore (only one probe at a time)
- Probe walks a fixed backoff schedule: 500ms, 1s, 2s, 5s, 10s, 15s, 30s, 45s, 60s (then 60s indefinitely)
- All other requests on the same executor wait on `_cdnResolved` (a `TaskCompletionSource`)
- On success: signals `_cdnResolved`, clears CDN state, all waiting requests resume
- On exhaustion: signals failure, `CdnBlockedException` propagates up

### Concurrency Impact

- `AdaptiveConcurrencyLimiter.SlashDop()` is called on CDN block — cuts DOP to `minDop`, sets `ssthresh = oldDop / 2`
- Recovery uses TCP slow-start: multiplicative ×1.333 below ssthresh, additive +16 above
- DOP slot is released during probe wait, reacquired for probe sends

### Metrics

- `CdnBlocksDetected` — number of 403 non-JSON responses
- `CdnProbeAttempts` — probe HTTP sends during retry
- `CdnProbeSuccesses` — successful probes (CDN cleared)
- `TotalHttpSends` — all HTTP sends including probes/retries

---

## Proposed Solution: Proxy Rotation via Gluetun + AirVPN

### Architecture

Add configurable HTTP proxy rotation to the scraper. When a CDN 403 block is detected, the system rotates to a different exit IP via proxy instead of waiting on the same blocked IP.

**Proxy infrastructure:** [Gluetun](https://github.com/qdm12/gluetun) Docker sidecar containers, each tunneling via WireGuard to a different AirVPN server and exposing a built-in HTTP proxy on port 8888.

Run Gluetun sidecars with Docker `init: true`. Their health checks and VPN helper commands can leave short-lived `timeout`/shell children behind when Gluetun is PID 1; Docker init reaps those children so long-lived proxy pools do not accumulate zombie processes. The Docker-based recycler also recreates rotated containers with `HostConfig.Init = true` even when the existing container was created before the Compose template included `init: true`.

Docker host control is worker-only. `fstservice` has neither
`/var/run/docker.sock` nor the Docker group and resolves
`IProxyContainerRecycler` to a rejecting implementation. `fstworker` alone
mounts the socket, receives the Docker group, and resolves the real recycler.
This prevents the public API process from controlling the host even though the
service and worker still share one binary.

### Why Gluetun + AirVPN (Not Commercial SOCKS5)

| Factor | Gluetun + AirVPN | Commercial SOCKS5 |
|---|---|---|
| **Monthly cost** | $0 (already subscribed) | $2–50/mo |
| **Bandwidth** | Unlimited | May throttle at ~60 GB/day |
| **Setup** | ~30 min one-time | 5 min |
| **Containers** | 3–4 extra (~43 MB each) | None |
| **Control** | Full (regions, health, stealth) | Provider-dependent |

The scraper pulls ~10 GB per scrape cycle × 6 cycles/day = ~60 GB/day. Per-GB proxy pricing (Bright Data at $8–15/GB, Oxylabs, Smartproxy) is prohibitively expensive. Unlimited-bandwidth providers like PIA standalone SOCKS5 ($2/mo) or NordVPN ($4/mo) are viable but may throttle at this volume. AirVPN via gluetun costs nothing additional and handles the bandwidth.

### Why HTTP Proxy (Not `network_mode`)

Gluetun supports two connection modes:

**❌ `network_mode: "service:gluetun"`** — ALL of FSTService's traffic routes through the VPN. Breaks PostgreSQL queries, API responses, health checks. Only one gluetun instance usable at a time.

**✅ HTTP proxy via `HTTPPROXY=on`** — Only specific HTTP requests (scraper calls to Epic's API) are routed through gluetun's proxy. PostgreSQL, API serving, health checks, and all other containers are completely unaffected. Multiple gluetun instances can be used simultaneously or rotated between.

### Traffic Isolation

| Traffic | Path |
|---|---|
| `fstservice` / `fstworker` → PostgreSQL | Direct Docker network (unchanged) |
| `fstservice` → clients (API responses) | Direct port 8080 (unchanged) |
| `fstworker` → Epic API (normal) | Direct internet (no proxy) |
| `fstworker` → Epic API (CDN blocked) | Through `gluetun-{region}:8888` → VPN → internet |
| PostgreSQL, festivalweb, etc. | Completely unaware of gluetun |

---

## Proxy Pool Design

### Pool Composition

AirVPN allows 5 simultaneous connections per standard plan. The pool:

| Slot | Connection | Exit IP |
|---|---|---|
| 0 | Direct (no proxy) | Server's real IP |
| 1 | gluetun-us | AirVPN US server |
| 2 | gluetun-eu | AirVPN Netherlands |
| 3 | gluetun-asia | AirVPN Singapore/Japan |
| 4 | gluetun-us2 | AirVPN different US server |

**Recommendation: 3–4 gluetun instances** (different regions) + direct = 4–5 exit IPs. Geographic diversity matters more than count — same-region AirVPN servers may share exit IPs or subnets.

### Rotation Strategy

**Reactive only** — rotate on CDN block, not preemptively.

```
Scraping on Direct (#0) → CDN 403
  → rotate to gluetun-us (#1), quick probe
  → success → continue scraping on gluetun-us
  ...
  CDN 403 on gluetun-us (#1)
  → rotate to gluetun-eu (#2), quick probe
  → success → continue on gluetun-eu
  ...
  CDN 403 on gluetun-eu (#2)
  → rotate to gluetun-asia (#3), quick probe
  ...
  CDN 403 on ALL proxies → ALL EXHAUSTED
  → fall back to timed backoff on oldest-blocked proxy (#0)
```

**Two phases:**

1. **Fast rotation phase:** Try each proxy with a quick probe (~2–5 seconds per rotation). Cycle through all N proxies.
2. **Timed backoff phase:** If all N are exhausted, fall back to the existing backoff schedule (500ms → 60s) on the **oldest-blocked proxy** (most recovery time elapsed).

### Per-Proxy Cooldown Tracking

`ProxyRotator` tracks `lastBlockedAt` per proxy (`DateTimeOffset[]`):

- `RotateNext()` skips proxies blocked within the cooldown window (e.g., < 5 minutes ago)
- Only probes proxies that have had time to recover
- Avoids wasting time probing proxies blocked 30 seconds ago
- On the next CDN block (minutes later), the system already knows which proxies are likely still blocked

### DOP Reset on Rotation

When `AdaptiveConcurrencyLimiter.SlashDop()` fires on CDN block, DOP drops to `minDop`. After rotating to a fresh (unblocked) proxy, the DOP would be stuck at a low value — defeating the purpose of fast recovery.

**Solution:** Add `ResetForProxyRotation(int targetDop)` to `AdaptiveConcurrencyLimiter`:
- Restores DOP to `targetDop` (adds semaphore tokens, clears release debt)
- Clears `ssthresh` (no slow-start — fresh proxy shouldn't be penalized)
- Resets evaluation window (clean slate for AIMD)

**Shared limiter (not per-proxy):** A single `AdaptiveConcurrencyLimiter` across all proxies, with DOP reset on successful rotation. Simpler than per-proxy limiters, and the key benefit (escaping blocks by switching IP) is achieved either way. Can upgrade to per-proxy limiters later if needed.

---

## Rate Limiting Analysis

### Shared vs. Per-Proxy Limiter

**Option 1: Shared limiter with DOP reset** (recommended)
- Single DOP budget across all proxies
- `ResetForProxyRotation()` restores DOP after switching to unblocked proxy
- Global req/s cap stays enforced — won't hammer Epic harder with more IPs
- Simpler to implement; can upgrade later

**Option 2: Per-proxy limiter** (more robust, more complex)
- Each proxy gets its own `AdaptiveConcurrencyLimiter`
- CDN `SlashDop()` on proxy A doesn't affect proxy B's DOP
- Requires separating the rate limiter from `AdaptiveConcurrencyLimiter` (or wrapper)
- Total concurrent requests = sum of all proxies' DOPs

**Decision:** Option 1 — shared limiter with DOP reset.

---

## Implementation Plan

### Phase 1: Configuration

Add to `ScraperOptions`:

```csharp
/// <summary>
/// List of proxy URIs for CDN block rotation. The system starts with direct
/// (no proxy) and rotates through these on CDN block detection.
/// Format: http://host:port or socks5://user:pass@host:port
/// Set via Scraper__ProxyUrls__0, Scraper__ProxyUrls__1, etc.
/// </summary>
public List<string> ProxyUrls { get; set; } = [];

/// <summary>
/// When true, rotate to the next proxy on CDN block instead of probing
/// the same IP. Requires ProxyUrls to be configured. Default: true.
/// </summary>
public bool RotateOnCdnBlock { get; set; } = true;
```

### Phase 2: ProxyRotator Service

New file `FSTService/Scraping/ProxyRotator.cs`:

- Circular pool of `HttpClient` instances (one per proxy URI + one for direct/null)
- `CurrentClient` — active HttpClient
- `CurrentLabel` — human-readable label for logging ("direct", "gluetun-us", etc.)
- `RotateNext()` — advance to next proxy, skip recently-blocked proxies (cooldown tracking)
- `MarkBlocked(int index)` — record block timestamp for a proxy
- `GetOldestBlocked()` — return the proxy with the most recovery time
- `Count` — total available proxies (including direct)
- Thread-safe via `Interlocked`; `IDisposable` for cleanup
- Each `HttpClient` created with `SocketsHttpHandler { Proxy = new WebProxy(uri) }` copying existing handler config (timeouts, decompression, connection limits)

Register as singleton in DI.

### Phase 3: AdaptiveConcurrencyLimiter DOP Reset

Add to `AdaptiveConcurrencyLimiter`:

```csharp
/// <summary>
/// Reset DOP after rotating to a fresh proxy. Restores concurrency to the
/// target level, clears slow-start threshold, and resets the evaluation window.
/// Called after a successful CDN probe on a new proxy.
/// </summary>
public void ResetForProxyRotation(int targetDop)
```

### Phase 4: ResilientHttpExecutor Integration

- Add optional `ProxyRotator?` to constructor
- Modify `SendAsync()`: use `_rotator?.CurrentClient ?? _http` for sends
- Modify `LaunchCdnProbe()`:
  1. If `ProxyRotator` available + `RotateOnCdnBlock`:
     - Call `_rotator.MarkBlocked(currentIndex)`
     - Call `_rotator.RotateNext()` — skips recently-blocked proxies
     - Quick-probe using `_rotator.CurrentClient`
     - If success: swap `_http`, call `limiter.ResetForProxyRotation(initialDop)`, signal `_cdnResolved`
     - If still blocked: rotate to next, repeat
     - If all proxies exhausted: fall back to timed backoff on `_rotator.GetOldestBlocked()`
  2. Log: `"CDN block: rotating to {Label} ({Index}/{Count})"`

### Phase 5: Wire into Scraper Classes

Pass `ProxyRotator` through constructors → into `ResilientHttpExecutor`:
- `GlobalLeaderboardScraper`
- `AccountNameResolver`
- `HistoryReconstructor`

Update `Program.cs` DI to resolve and inject `ProxyRotator`.

### Phase 6: Docker / AirVPN Configuration

Add gluetun sidecar service definitions to `deploy/docker-compose.yml`:

```yaml
gluetun-us:
  image: qmcgaw/gluetun
  container_name: gluetun-us
  restart: unless-stopped
  cap_add:
    - NET_ADMIN
  devices:
    - /dev/net/tun:/dev/net/tun
  environment:
    - VPN_SERVICE_PROVIDER=airvpn
    - VPN_TYPE=wireguard
    - SERVER_COUNTRIES=United States
    - WIREGUARD_PRIVATE_KEY=${AIRVPN_WG_PRIVATE_KEY}
    - WIREGUARD_PRESHARED_KEY=${AIRVPN_WG_PRESHARED_KEY}
    - WIREGUARD_ADDRESSES=${AIRVPN_WG_ADDRESSES}
    - HTTPPROXY=on
    - HTTPPROXY_LISTENING_ADDRESS=:8888
    - HTTPPROXY_STEALTH=on

gluetun-eu:
  image: qmcgaw/gluetun
  container_name: gluetun-eu
  # ... same structure, SERVER_COUNTRIES=Netherlands

gluetun-asia:
  image: qmcgaw/gluetun
  container_name: gluetun-asia
  # ... same structure, SERVER_COUNTRIES=Japan
```

Mount `/var/run/docker.sock` and add the host Docker group only on
`fstworker`. Never inherit that volume or group onto `fstservice`.

FSTService env vars:
```yaml
- Scraper__ProxyUrls__0=http://gluetun-us:8888
- Scraper__ProxyUrls__1=http://gluetun-eu:8888
- Scraper__ProxyUrls__2=http://gluetun-asia:8888
```

### AirVPN Setup (One-Time)

1. Log in to AirVPN → Config Generator → select WireGuard
2. Generate configs for 3–4 different server regions
3. Extract from each config: `WIREGUARD_PRIVATE_KEY`, `WIREGUARD_PRESHARED_KEY`, `WIREGUARD_ADDRESSES`
4. Same keys can work for all gluetun instances — gluetun picks the server per `SERVER_COUNTRIES`
5. Add keys to `.env` on the Docker host (not committed to repo)

---

## Files Changed

| File | Change |
|---|---|
| `FSTService/ScraperOptions.cs` | Add `ProxyUrls`, `RotateOnCdnBlock` |
| `FSTService/Scraping/ProxyRotator.cs` | **New file** |
| `FortniteFestival.Core/Scraping/AdaptiveConcurrencyLimiter.cs` | Add `ResetForProxyRotation()` |
| `FSTService/Scraping/ResilientHttpExecutor.cs` | Accept `ProxyRotator`, modify probe + send |
| `FSTService/Scraping/GlobalLeaderboardScraper.cs` | Pass `ProxyRotator` to executor |
| `FSTService/Scraping/AccountNameResolver.cs` | Pass `ProxyRotator` to executor |
| `FSTService/Scraping/HistoryReconstructor.cs` | Pass `ProxyRotator` to executor |
| `FSTService/Program.cs` | Register `ProxyRotator`, inject into scrapers |
| `deploy/docker-compose.yml` | Gluetun sidecar definitions + proxy env vars |
| `docker-compose.yml` | Proxy env vars for local dev |

---

## Verification

1. **Unit test `ProxyRotator`** — rotation wrapping, client creation, cooldown tracking, label generation, disposal
2. **Unit test `ResetForProxyRotation()`** — DOP restored, ssthresh cleared, eval window reset
3. **Unit test `ResilientHttpExecutor`** — rotation on CDN block, client swap on probe success, fallback to timed backoff after exhausting pool, per-proxy cooldown respected
4. **Manual test** — configure gluetun sidecar with AirVPN, trigger CDN block, observe rotation + recovery

---

## Impact Summary

**Today:** CDN block → wait up to 7 min on same IP → might exhaust retries → scrape pass fails, retry in 4 hours.

**With proxy rotation:** CDN block → try 3–4 other IPs in ~10–20 seconds → likely one works → continue scraping immediately. If all blocked, fall back to timed backoff on oldest-blocked IP (which has had the most recovery time).

---

## Design Decisions

| Decision | Rationale |
|---|---|
| Gluetun HTTP proxy, not `network_mode` | Only scraper traffic routes through VPN; all other containers unaffected |
| AirVPN via gluetun sidecars, not commercial SOCKS5 | Already subscribed, unlimited bandwidth, $0/mo ongoing |
| Shared limiter with DOP reset, not per-proxy | Simpler, key benefit achieved either way, upgradeable |
| Reactive rotation only, not preemptive | User preference; avoids unnecessary proxy usage |
| Direct connection stays in pool | Can rotate back when block expires |
| One shared `ProxyRotator` across all scrapers | CDN blocks are IP-based; rotation benefits all |
| Per-proxy cooldown tracking | Avoids re-probing recently blocked proxies |
| 3–4 gluetun instances (diverse regions) | Enough rotation depth without exhausting AirVPN connection limit (5) |
| `HTTPPROXY_STEALTH=on` | Don't add X-Forwarded-For headers that reveal proxy usage |
