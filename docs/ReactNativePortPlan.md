# MAUI → React Native Port Plan (Android / iOS / Windows)

Date: 2026-01-28

## 1) Executive Summary

This document describes a practical, end-to-end migration plan to port this app from .NET MAUI to React Native while supporting:

- Android (React Native)
- iOS (React Native)
- Windows (React Native Windows)

The strategy is **mobile-first best-in-class**, with **Windows-compatible fallbacks** where needed.

### Goals

- Preserve core functionality (auth → sync → browse/sort → stats)
- Improve long-term maintainability and UI iteration speed
- Keep Android/iOS optimized (no “lowest common denominator” compromises)
- Deliver a usable Windows desktop build (even if some UX differs)

### Non-goals (initially)

- Adding new product features
- Changing the upstream Epic API behavior
- Perfect 1:1 UI replication (exact pixel parity)

### Success Criteria

- Reliable sync flow with progress + cancellation
- Data persistence works on all platforms
- Mobile performance: smooth scrolling on song lists, responsive UI during sync
- Windows build can sync + browse + display stats

---

## 2) Current System Overview

### Codebase Structure

- Core logic: FortniteFestival.Core
  - HTTP scraping/sync engine
  - Models, settings, persistence interfaces
  - SQLite persistence implementation
- UI app: FortniteFestival.LeaderboardScraper.MAUI
  - MAUI pages + XAML
  - Uses Syncfusion list view for drag reorder
  - Uses MAUI FileSystem.AppDataDirectory for data paths

### Key Runtime Flows (Today)

1) User obtains an Epic exchange code via browser
2) App exchanges code for token
3) App syncs:
   - song catalog
   - images (download to disk)
   - leaderboard scores per song/instrument (throttled parallel fetch)
4) Results are persisted (SQLite) and displayed
5) User browses songs, sorts/filters, views stats

### Current Notable Dependencies

- Core
  - Microsoft.Data.Sqlite
  - Newtonsoft.Json (plus System.Text.Json usage)
- MAUI
  - Syncfusion.Maui.ListView (drag reorder)
  - MAUI controls

---

## 3) Target Architecture (React Native)

### Design Principles

- Keep business logic in **pure TypeScript** wherever possible
- Hide platform differences behind **adapters**
- Prefer **foreground-safe** sync (mobile OS restrictions)
- Treat “Windows parity” as **compatibility**, not “mobile UX parity”

### Proposed Layers

- Presentation (React components)
  - Screens, UI components, navigation
- State
  - UI state (filters, selection, navigation state)
  - Server/cache state (React Query)
  - App-level stores (Zustand or Redux)
- Domain (TypeScript)
  - Sync engine (jobs, progress, throttling, normalization)
  - Derived stats computation
- Data / Platform adapters
  - Persistence (SQLite / KV)
  - Filesystem + image cache
  - Logging

### Concurrency Model

- Use async tasks with concurrency limiting (e.g., Bottleneck / p-limit)
- Stream progress to UI via event callbacks or observable state
- Support cancellation (AbortController pattern + cooperative cancellation)

---

## 4) Package Selection Strategy (Hybrid)

We will select **best packages for Android/iOS** and only use Windows-compatible alternatives when needed.

### Category Matrix

| Category | Mobile (Best-in-class) | Windows (Compatible fallback) | Notes |
|---|---|---|---|
| Navigation | @react-navigation/native + native-stack | @react-navigation/stack (JS stack if needed) | Keep nav simple for RNW |
| HTTP | axios or fetch | axios or fetch | Pure JS |
| API cache/retry | @tanstack/react-query | @tanstack/react-query | Pure JS |
| Throttling | bottleneck (or p-limit) | bottleneck (or p-limit) | Pure JS |
| Song list perf | @shopify/flash-list | FlatList (or recyclerlistview) | Validate RNW support for FlashList |
| Drag reorder | react-native-draggable-flatlist | Windows: reorder buttons / custom | Avoid blocking mobile UX |
| KV settings | react-native-mmkv | @react-native-async-storage/async-storage | Windows support differs |
| SQLite DB | (choose best mobile SQLite) | react-native-sqlite-storage | Validate Windows early |
| Filesystem | react-native-file-access (or RNFS) | react-native-fs | RNFS has Windows support |
| Image caching | react-native-fast-image (or expo-image) | built-in Image + RNFS download | Windows likely needs DIY |
| Crash reporting | Sentry or Crashlytics | Sentry (JS) + file logs | Validate RNW behavior |
| UI kit (optional) | react-native-paper | @fluentui/react-native | Optional; can also go custom |

---

## 5) Feature Parity Matrix

This matrix tracks what must be ported and where Windows may need alternate UX.

| Feature | Priority | Mobile plan | Windows plan | Risk |
|---|---:|---|---|---|
| Exchange code UX (open browser + paste code) | P0 | Implement with Linking + text input | Same | Low |
| Token exchange + verification | P0 | axios + typed parsing | Same | Low |
| Sync job (songs + images + scores) | P0 | Throttled async pipeline + progress | Same | Medium |
| Cancel sync | P0 | AbortController + cooperative checks | Same | Medium |
| Persist songs/scores | P0 | SQLite adapter | SQLite adapter proven on RNW | High |
| Download/cache images to disk | P0 | FS download + cached path | FS download + cached path | Medium |
| Song list browse/search/sort | P0 | FlashList + memoized rows | FlatList + perf tuning | Medium |
| Instrument priority reorder | P1 | Drag reorder | Reorder buttons/edit mode | Medium |
| Statistics view | P1 | Derived stats in TS + memoization | Same | Medium |
| Log export | P1 | Write + share/export | Write + open folder | Medium |

Risk legend:
- Low: straightforward port
- Medium: needs careful UX/perf/cancellation
- High: RNW module compatibility or heavy refactor

---

## 6) Data Model & Persistence

### Entities

- Song (id, title, artist, difficulties, image URL/path, lastModified)
- LeaderboardData (per-song score trackers per instrument)
- Settings (enabled instruments, sort preferences, primary instrument order)

### Persistence Approach

Define adapter interfaces (TypeScript) so platform differences do not leak.

- ScoreDb
  - open()
  - loadSongs(), saveSongs()
  - loadScores(), upsertScores()
  - migration/versioning
- SettingsStore
  - loadSettings(), saveSettings()
- FileStore
  - getAppDataDir(), writeText(), appendText(), readFile(), exists(), mkdirp()
- ImageCache
  - getLocalPathForSong(song)
  - ensureCached(song) → localUri

SQLite strategy:
- Start by mirroring the existing schema (Songs + Scores)
- Add migrations gradually (version table)

---

## 7) Networking & Auth Port

### Auth Flow

- Use the same user experience:
  - open a browser to the exchange code page
  - user pastes the code into the app
- Exchange code → access token
- Verify token before starting the heavy sync

### HTTP Reliability Rules

- Timeouts and retries with exponential backoff for transient failures
- Detect unauthorized → stop sync and prompt for a new exchange code
- Do not log tokens or secrets

---

## 8) Sync Engine (TypeScript) Design

### Requirements

- Fetch song catalog
- Fetch/update cached images
- Fetch scores per song and per instrument
- Persist incrementally
- Report progress frequently
- Allow cancellation and (optionally) resume

### Recommended Job Model

- SyncSession
  - sessionId
  - startedAt
  - settingsSnapshot
  - progress counters
  - lastCheckpoint

- Work queue
  - items: songId + instrument key + season scope
  - throttled concurrency
  - priority support (optional)

### Progress Reporting

- Provide:
  - total tasks
  - completed tasks
  - current song title
  - current phase (songs / images / scores)

---

## 9) UI/UX Port Plan

### Screens (Proposed)

- Home / Sync screen
  - exchange code input
  - start/cancel sync
  - progress log
- Songs screen
  - searchable list
  - sort options
  - instrument order settings
- Song detail screen
  - per-instrument score view
- Stats screen
  - derived stats cards
- Settings screen
  - instruments enabled
  - other preferences
- Logs screen (optional)

### Windows UX Differences (Acceptable)

- Reorder instruments via buttons instead of drag
- Potentially fewer animation/gesture flourishes

---

## 10) Windows-Specific Strategy (RNW)

Windows is primarily risky for:

- SQLite binding compatibility
- Filesystem module compatibility
- Drag/gesture-driven reorder controls

Windows mitigation strategy:

- Run a Windows spike early for:
  - DB open + migrate + CRUD
  - FS write/append logs
  - image download to app data
  - list rendering perf
- Prefer feature fallbacks over fragile polyfills

---

## 11) Testing Strategy

- Unit tests (Jest)
  - sync engine logic
  - throttling/cancellation
  - derived stats computation
- Integration tests
  - DB migrations
  - persistence adapters
- Manual smoke tests per platform
  - exchange code flow
  - full sync + cancel + resume
  - browse songs + stats

---

## 12) Security & Privacy

- Tokens are sensitive:
  - store in memory where possible
  - if persisted, use secure storage on mobile
  - redact from logs
- Provide clear user messaging about risks of authenticating

---

## 13) CI/CD & Release Plan (High Level)

- Mobile
  - Android: Play internal testing track
  - iOS: TestFlight
- Windows
  - MSIX or packaged installer
  - optional auto-update strategy

---

## 14) Migration Phases & Milestones

### Phase 0: Spikes (timeboxed, 1–3 days each)

- Windows spike
  - SQLite module works end-to-end
  - filesystem write + image download
  - minimal UI list rendering
- Mobile spike
  - FlashList + draggable reorder
  - MMKV
  - fast image

Exit: we have verified package feasibility and decided on final packages.

### Phase 1: Scaffold App Shell

- Repo setup
- Navigation skeleton
- Theming + base components
- Logging plumbing

### Phase 2: Port Sync Engine (Minimal UI)

- Auth + token exchange
- Sync songs + scores + persist
- Progress UI + cancel

### Phase 3: Port Browsing UX

- Song list screen
- Sort/filter options
- Song detail view

### Phase 4: Port Statistics

- Derived stats implementation
- Stats screen UI
- Performance pass

### Phase 5: Windows Hardening

- Platform fallbacks
- Packaging
- Stability/perf fixes

### Phase 6: Release Prep

- QA, telemetry, documentation
- Store submission

---

## 15) Risk Register

| Risk | Impact | Likelihood | Mitigation |
|---|---|---|---|
| RNW SQLite module issues | High | Medium | Spike early; swap storage; write small bridge if necessary |
| Drag reorder breaks on RNW | Medium | High | Use Windows reorder buttons/edit mode |
| Mobile OS background limits | Medium | High | Foreground-first, resumable jobs, clear UX |
| Performance regressions on large song list | Medium | Medium | FlashList on mobile; FlatList tuning; memoization |
| Upstream API changes | Medium | Medium | Better error handling + feature flags + logging |

---

## 16) Appendix: Implementation Interfaces (Suggested)

These interfaces should exist early to keep platform differences isolated.

- ScoreDb
- SettingsStore
- FileStore
- ImageCache
- SyncEngine

A pattern that works well:

- src/platform/index.ts exports implementations based on Platform.OS
- Domain code depends only on interfaces

---

## Next Decisions to Make

1) Is SQLite mandatory on Windows, or can Windows use a different local store?
2) Is drag reorder required on Windows, or is reorder-by-buttons acceptable?
3) Do we want Expo (mobile) or bare RN? (Expo improves mobile DX but complicates RNW parity.)
