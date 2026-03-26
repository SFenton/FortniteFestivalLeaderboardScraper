# Phase 14: Documentation Wiki (~92 pages)

**Status:** ⬜ Not Started
**Depends on:** Nothing (updated as phases complete)
**Parallel with:** Anything

## Goal
Comprehensive Wikipedia-style documentation. ~92 pages across 14 sections.

## Steps

### 14.0 — Cleanup
- [ ] Delete `docs/EpicLoginDesign.md` (duplicate)
- [ ] Delete `docs/FSTServiceDatabaseDesign.md` (duplicate)
- [ ] Delete `docs/UserDeviceRegistrationDesign.md` (duplicate)
- [ ] Delete `docs/UserRegistrationBackfillDesign.md` (duplicate)
- [ ] Add status headers to all 7 design docs

### 14.1 — Root + Index (2 pages)
- [ ] Rewrite root README.md
- [ ] Create docs/README.md index

### 14.2 — Architecture (5 pages)
- [ ] docs/architecture/Overview.md
- [ ] docs/architecture/DataFlow.md
- [ ] docs/architecture/DeploymentTopology.md
- [ ] docs/architecture/SharedPackages.md
- [ ] docs/architecture/SecurityModel.md

### 14.3 — Service (14 pages)
- [ ] docs/service/RunModes.md
- [ ] docs/service/ScrapePhase1-Auth.md
- [ ] docs/service/ScrapePhase2-CatalogSync.md
- [ ] docs/service/ScrapePhase3-PathGeneration.md
- [ ] docs/service/ScrapePhase4-GlobalScrape.md
- [ ] docs/service/ScrapePhase5-FirstSeenSeason.md
- [ ] docs/service/ScrapePhase6-NameResolution.md
- [ ] docs/service/ScrapePhase7-PersonalDbBuild.md
- [ ] docs/service/ScrapePhase8-PostScrapeRefresh.md
- [ ] docs/service/ScrapePhase9-Backfill.md
- [ ] docs/service/ScrapePhase10-HistoryRecon.md
- [ ] docs/service/ScrapePhase11-Cleanup.md
- [ ] docs/service/AdaptiveConcurrency.md
- [ ] docs/service/ResilientHttp.md

### 14.4 — Database (6 pages)
- [ ] docs/database/MetaDatabase.md
- [ ] docs/database/InstrumentDatabases.md
- [ ] docs/database/PersonalDatabases.md
- [ ] docs/database/CoreSongDatabase.md
- [ ] docs/database/DataAccuracyGuide.md
- [ ] docs/database/StorageOptimization.md

### 14.5 — API (5 pages)
- [ ] docs/api/Endpoints.md
- [ ] docs/api/Authentication.md
- [ ] docs/api/RateLimiting.md
- [ ] docs/api/WebSocket.md
- [ ] docs/api/ErrorHandling.md

### 14.6 — Web Architecture (9 pages)
- [ ] docs/web/Architecture.md
- [ ] docs/web/DesignTokens.md
- [ ] docs/web/ResponsiveDesign.md
- [ ] docs/web/StateManagement.md
- [ ] docs/web/ScrollModel.md
- [ ] docs/web/AnimationSystem.md
- [ ] docs/web/CachingStrategy.md
- [ ] docs/web/TestingGuide.md
- [ ] docs/web/BuildAndDeploy.md

### 14.7 — Web Per-Page (12 pages)
- [ ] docs/web/pages/SongsPage.md
- [ ] docs/web/pages/SongDetailPage.md
- [ ] docs/web/pages/LeaderboardPage.md
- [ ] docs/web/pages/PlayerHistoryPage.md
- [ ] docs/web/pages/PlayerPage.md
- [ ] docs/web/pages/RivalsPage.md
- [ ] docs/web/pages/RivalDetailPage.md
- [ ] docs/web/pages/RivalryPage.md
- [ ] docs/web/pages/AllRivalsPage.md
- [ ] docs/web/pages/SuggestionsPage.md
- [ ] docs/web/pages/ShopPage.md
- [ ] docs/web/pages/SettingsPage.md

### 14.8 — Web FRE (6 pages)
- [ ] docs/web/fre/FREOverview.md
- [ ] docs/web/fre/FRESongsSlides.md
- [ ] docs/web/fre/FRESongInfoSlides.md
- [ ] docs/web/fre/FREStatisticsSlides.md
- [ ] docs/web/fre/FRESuggestionsSlides.md
- [ ] docs/web/fre/FREPlayerHistorySlides.md

### 14.9 — Web Algorithms (6 pages)
- [ ] docs/web/algorithms/ScoringMath.md
- [ ] docs/web/algorithms/PercentileCalculation.md
- [ ] docs/web/algorithms/RivalMatchingAlgorithm.md
- [ ] docs/web/algorithms/RivalCategorizationAlgorithm.md
- [ ] docs/web/algorithms/SongFilteringPipeline.md
- [ ] docs/web/algorithms/StaggerAnimationMath.md

### 14.10 — Web Components (7 pages)
- [ ] docs/web/components/PageShell.md
- [ ] docs/web/components/FrostedCard.md
- [ ] docs/web/components/PageHeader.md
- [ ] docs/web/components/Paginator.md
- [ ] docs/web/components/ActionPill.md
- [ ] docs/web/components/ModalSystem.md
- [ ] docs/web/components/InstrumentSelector.md

### 14.11 — Web Styling (4 pages)
- [ ] docs/web/styling/StylingGuide.md
- [ ] docs/web/styling/ThemePackage.md
- [ ] docs/web/styling/DesignTokenReference.md
- [ ] docs/web/styling/ResponsivePatterns.md

### 14.12 — Web Testing (2 pages)
- [ ] docs/web/testing/VitestGuide.md
- [ ] docs/web/testing/PlaywrightGuide.md

### 14.13 — Tooling (5 pages)
- [ ] docs/tooling/MCP-Overview.md
- [ ] docs/tooling/MCP-ToolReference.md
- [ ] docs/tooling/Agent-Guardian.md
- [ ] docs/tooling/CodingStandards.md
- [ ] docs/tooling/PromptTemplates.md

### 14.14 — PercentileService (2 pages)
- [ ] docs/percentile/Overview.md
- [ ] docs/percentile/PercentileAlgorithm.md

### 14.15 — Core Library (3 pages)
- [ ] docs/core/Overview.md
- [ ] docs/core/FestivalService.md
- [ ] docs/core/Models.md

### 14.16 — Component READMEs (4 files)
- [ ] FortniteFestivalWeb/README.md
- [ ] FSTService/README.md
- [ ] FortniteFestival.Core/README.md
- [ ] PercentileService/README.md

## Verification Checks

- [ ] Every page renders on GitHub
- [ ] No broken cross-links
- [ ] docs/README.md lists every page with status
- [ ] Every algorithm page includes formulas/pseudocode
- [ ] Every UX page documents every interactive element
