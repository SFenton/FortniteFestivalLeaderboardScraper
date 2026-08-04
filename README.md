# Fortnite Festival Score Tracker

A simple Windows utility for retrieving your highest Fortnite Festival scores across all instruments and across all seasons!

<img width="835" alt="image" src="https://github.com/user-attachments/assets/9c9f8213-8f5e-401f-a925-d26d1824f47b">
<img width="833" alt="image" src="https://github.com/user-attachments/assets/ca5dbd34-a7be-41c5-932c-57b9e9bc527b">

## Why?

The Fortnite Festival leaderboards reset at the beginning of every season. This means that, while Epic is still saving your highest scores from previous seasons in their leaderboards (actually, every single session you've played!), they aren't surfaced in the game anywhere, most importantly on the song select screen, as they have been for the past... nearly twenty years. I wanted to make this tool because I worked hard for the FCs I've gotten, and it's kind of maddening that I didn't have a foolproof in-game way to track that across seasons.

This app will also output scores you have for songs you don't own, which is a great way to track Battle Pass/Festival Pass/event songs that you missed out on. I don't think that information will ever be surfaced in game if you're playing solo/don't own those tracks and want to see your scores.

## How does the app work?

By supplying the application with an Exchange Token, we simulate the Epic authentication process and spoof being a Fortnite client. This gives us special permissions to read the Fortnite Festival leaderboards, and we query each song, season, and instrument, find your highest scores, and write them out to an Excel workbook that is easily digestible.

## What is an exchange token?

It is a special token required to authenticate with Epic's servers.

## Why do we need to authenticate?

Ideally, we'd just need your account ID, and leaderboards would be public and we could just make a GET request. Unfortunately, not only is leaderboard access locked behind authentication, but your authenticated token must also have special permissions that allow us to access the leaderboard API.

## Does this mean the app could potentially do other things with my account?

Yes. I highly encourage you to read the source code and build the app from scratch yourself to avoid any possibility of this happening. This code will always be open source and available, and while I will provide binaries for those who don't wish to build it themselves, I want to be clear that having your Epic account authenticated could *potentially* mean it could be used for purposes other than this. Always be wary of the executables you download.

## Can I be banned for this?

I won't say no, but I think the chances are not high. We spoof being the PC client and are only querying the leaderboards, though it's possible Harmonix and/or Epic may find it... *odd* that your account is querying every song for every season and instrument, in a way that is not supported in game.

## Is there any other way to see my scores?

FortniteTracker has I think the top 200 scores for each song and instrument, so if you're up there, you're good. If not, no, outside of in-game seeing the current season.

## Can I see band scores?

In theory this is possible, but I haven't figured it out yet. The good news is, the app still pulls your individual scores, even from band runs!

## I'm having an issue. What can I do?

Please file a bug report or feature request! I'll be watching!

## Autonomous repository operations

Repository-local autonomous execution guidance lives in `.github/skills/autonomous-plan-executor/SKILL.md`. It is tailored for FST production safety while preserving autonomous progress: scrapes should proceed normally, `fstworker`/`fstservice`/`festivalweb` may be restarted or briefly taken down for maintenance, redeploy/recover them as soon as possible, preserve published-scrape/public-read correctness, prove destructive database work with live-scrape A/B data parity, commit/push accepted changes before advancing, and execute destructive reclaim automatically after the parity/rollback/monitoring gate passes.

Autonomous phase and recap e-mails are rendered with:

```bash
node tools/agent-report-email.mjs --subject "FST Autonomous Agent: Recap - <Workflow> · <Status>" --input-md <report.md>
```

By default this creates dry-run HTML/JSON output under `.outbox/fst-autonomous-agent/`. Real sends require explicit `--send` plus `FST_AUTONOMOUS_EMAIL_ENABLED=true`, `FST_AUTONOMOUS_EMAIL_DRY_RUN=false`, `FST_AUTONOMOUS_EMAIL_TO`, and SMTP settings (`FST_AUTONOMOUS_EMAIL_SMTP_HOST`, `FST_AUTONOMOUS_EMAIL_SMTP_PORT`, `FST_AUTONOMOUS_EMAIL_SMTP_USER`, `FST_AUTONOMOUS_EMAIL_SMTP_PASSWORD`). When an operator identifies a trusted dotenv file that already contains equivalent `DAY_TRADER_EMAIL_*` settings, pass `--fallback-env-file <path>` to map only the allowlisted e-mail settings in process without executing or copying the dotenv file; explicit FST variables retain precedence.

FSTService has no tracked credential defaults. Production/local compose must
provide `PG_PASSWORD`, `API_KEY`, `EPIC_CLIENT_ID`,
`EPIC_CLIENT_SECRET`, and any enabled feature credentials through an ignored
`.env` file or secret store. Direct FSTService execution uses `Api__ApiKey`
instead of the compose alias `API_KEY`. The local template also requires
`WEBAPP_PASSWORD`. Check candidate changes without printing values:

```bash
node --test tools/secret-scan.test.mjs
node tools/secret-scan.mjs
```

Nullable `score_history` dedup is an explicit audited maintenance command, not
a startup migration. Dry run is the default and is PostgreSQL
`REPEATABLE READ`/`READ ONLY`; execute requires both flags:

```bash
# Dry run
dotnet FSTService.dll --score-history-dedup-maintenance

# Execute only after two matching accepted dry runs and a maintenance gate
dotnet FSTService.dll \
  --score-history-dedup-maintenance \
  --score-history-dedup-execute \
  --expected-score-history-dedup-digest <sha256>
```

The execute transaction immutably audits every original row, merges only the
approved zero-score/rank-metadata duplicates, and replaces `ix_sh_dedup` with
PostgreSQL 17 `UNIQUE ... NULLS NOT DISTINCT`. It is fail-closed on unexpected
history, digest drift, lock timeout, or an absent/inexact immutable audit
schema. Execute locks `score_history` before its first snapshot-establishing
query, then takes the advisory lock. Do not run the unbounded
`--initialize-schema-only` command solely as maintenance preparation; normal
release initialization owns schema repair. Rollback also refuses a reused
audited non-survivor ID before deleting anything. See
[`docs/database/ScoreHistoryDedupMaintenanceRunbook.md`](docs/database/ScoreHistoryDedupMaintenanceRunbook.md)
for the bounded catalog preflight, maintenance-window checks, locks, runtime
estimate, validation, and exact per-run rollback SQL.

Worker correctness rollout uses three rollback-safe environment switches:

- `Features__EnforceScopeCompletenessManifests=true`
- `Features__RequireSuccessfulScrapeWriters=true`
- `Features__EnforcePublicationCriticalPhases=true`

Enable them only on `fstworker` after the additive correctness schema has been
initialized. Rollback sets them to `false`; failed candidates and replay
artifacts remain diagnostic-only, and public reads stay on the prior mapped
published scrape.

Registration/backfill results are publication-pending until a successful
ranking pass and cache cut include them. New users therefore receive a
no-store `202 syncing/notYetPublished` profile/history response instead of
live candidate scores. Background registration and band-history workers pause
and drain at scrape boundaries; deferred rivals and post-cutoff registrations
remain queued for a later ranked publication.

The additive publication-generation ledger and browser bootstrap are present,
but request pinning remains disabled by default:

- `Features__EnablePublicationReadContext=false`

Do not enable it until catalog, shop, paths, caches, histories, overlays,
rankings, rivals, exports, and notification feeds all have ready
generation-addressable bindings. The current ledger deliberately marks legacy
live/inherited bindings as incomplete.

Publication contract version `1` explicitly maps all 55
`PublicationBound` route definitions to named required surfaces. The
`/api/publication` bootstrap reports `contractVersion`, `readyForPinning`, the
effective `pinningEnabled` value, and sorted `unreadySurfaces` reasons.
Configuration alone cannot activate pinning: a current-generation readiness
failure keeps effective pinning off and makes a configured pinned read fail
closed with `503`; a stale requested publication still returns `409` first.
Current `item_shop` and `path_artifacts` bindings remain
`legacy_live_unversioned`/`building`, and the remaining source cuts are still
required. The production flag remains false.

Required web-client follow-up before rollout: extend
`FortniteFestivalWeb/src/api/client.ts` to type and consume the additive
publication readiness fields. The backend JSON is already additive and
backward-compatible; this phase intentionally does not enable pinning.

API response caches now have generation-keyed live and staging storage.
Current and previous generations are retained; older cache generations and
failed candidate staging are removed independently of scrape-log retention.
The legacy cache remains as a rollback compatibility mirror.

CHOpt path generation has separate explicit and automatic controls:

- `Scraper__EnablePathGeneration=true` permits the protected single-song admin
  endpoint and coordinator.
- `Scraper__EnableAutomaticPathGeneration=true` permits background generation
  only for rows placed in the durable PostgreSQL pending queue by exact catalog
  persistence: new songs and changed songs that already use atomic
  generations.

Legacy path rows are never migrated automatically. The protected
`POST /api/admin/regenerate-paths?songId=<id>` route requires one exact song
ID; it cannot launch an unbounded full-catalog migration. Atomic promotions
use immutable generation directories and a PostgreSQL revision fence. Before
rolling back to a pre-atomic binary, disable both path-generation switches and
restore any promoted song rows from the recorded pre-deploy snapshot.

Song catalog sync now also persists a deterministic provider-only live
snapshot, including unknown Epic extension fields, plus exact per-song
`provider_json` for restart recovery. Catalog sync returns an explicit capture
result and persists only a successful, fully parsed provider response with no
safety merge or blocked eviction. The worker aborts before scrape allocation
when that result is inexact, otherwise it passes the returned version/hash
token and allocation accepts only that same exact token under the publication
lock. Legacy-column reconstruction is explicitly incomplete and produces a
`building` binding until a fresh provider capture exists; it is never promoted
as historical source-cut truth. Current, previous, and working exact
generations are retained. `/api/songs`,
`/api/shop`, and path readers still use their legacy live sources, so this
storage is additive and `Features__EnablePublicationReadContext` remains
`false`. Rollback deploys the prior binary and leaves the new columns/tables
in place; legacy writes remain accepted but automatically invalidate exactness
when their content changes. No schema or data removal is required.

Provider catalog refreshes are serialized per `FestivalService` instance
through fetch, merge, snapshot, and persistence. Artwork/image updates use a
separate local-state writer that can only update `songs.image_path`; failed or
safety-merged initialization therefore cannot stamp a provider-exact catalog.
Resume-only post-processing skips provider refresh and reloads the resumed
scrape's immutable publication catalog for song lists, requests, and expected
scopes. Bulk-removal protection applies only to a trusted exact baseline; a
complete provider response replaces reconstructed/inexact legacy state.
Rejected bulk-removal responses leave that trusted baseline/token intact, so
they do not mutate the trusted in-memory catalog and identical consecutive
partial responses remain rejected.

Improvement notifications can be recovered for the already-published scrape
without starting a full scrape:

```bash
docker compose run --rm --no-deps --entrypoint dotnet fstservice \
  FSTService.dll --recover-improvement-notifications \
  --published-scrape-id <id>
```

The command defaults to executing player and band detection and safely retries
an interrupted pending publication using the exact projection scope plan
stored with that publication. It never silently expands recovery to every
current scope. `--notification-dry-run` previews without writing,
`--notification-baseline-only` updates state without events, and
`--notification-skip-projection-refresh` is only for an operator who has
separately proved the persisted plan is unnecessary.

The worker holds before another scrape while the published scrape's
notification marker is incomplete and retries recovery once per minute. The
publication transaction independently refuses to replace an incomplete marker,
so a later scrape cannot silently discard pending notification work.

# How to run the app

Log in to Epic Games on your web browser of choice. Then, in the app, click "Generate Exchange Code" and that link will open in your browser. Copy the "code" value over to the app, and then click Run. You will see the console output with all the information that the app is querying. The app will run, and drop a file called "FortniteFestivalScores.xlsx" in the executing directory. These are all your scores- including scores on songs you don't own!

In addition, there is a tab in the app itself that allows you to view and sort your scores!

Note that exchange tokens from the Epic site expire after about 300 seconds, and once an exchange token is authenticated, you cannot use it again. If you need to run the tool multiple times, you'll need to generate a new exchange token each time.

## Can I play Fortnite while the tool runs?

No. This will cause a new client to authenticate, rendering your previous token useless, and will cause the application to stop working. This is applicable to all platforms- you cannot play Fortnite on *any* platform while this utility runs, or the utility will fail.

## Can I grab only certain songs?

Yes! Head over to the "Select Songs" tab, and select the songs you want to retrieve scores for. If no songs are selected, we will grab all scores for all songs by default.

## Can I grab only certain instruments?

Yes! Head over to the "Options" tab, and toggle on/off the instruments you want to retrieve/write out to the output workbook. Note that you need to have at least one instrument selected to run the tool.

## Can I change the output sort of data?

Yes! Head over to the "Options" tab, where you can change how your output workbook is sorted.
