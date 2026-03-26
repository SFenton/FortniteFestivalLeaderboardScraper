# Phase 8: Playwright E2E Tests

**Status:** ⬜ Not Started
**Depends on:** Phase 7 (infrastructure); Phases 1-5 (test writing)

## Goal
~131 Playwright tests across 9 spec files, 4 viewport projects (~524 executions).

## Steps

### 8.0 — Infrastructure
- [ ] Create `playwright.config.ts` (4 projects: mobile 375, wide-mobile 520, compact-desktop 1024, wide-desktop 1600)
- [ ] Create `e2e/fixtures/mockApi.ts` — page.route() interceptors for all 14 API endpoints
- [ ] Create `e2e/fixtures/fixtures.ts` — reusable test data
- [ ] Create `e2e/fixtures/localStorage.ts` — seed helpers for settings, FRE, player
- [ ] Create `e2e/fixtures/helpers.ts` — waitForContentIn, waitForSpinnerGone, etc.

### 8.1 — Landing Pages (~14 tests)
- [ ] Write `e2e/tests/landing.spec.ts`

### 8.2 — Navigation Flows (~16 tests)
- [ ] Write `e2e/tests/navigation.spec.ts`

### 8.3 — Page Interactions (~34 tests)
- [ ] Write `e2e/tests/interactions.spec.ts`

### 8.4 — Settings Toggle Impact (~14 tests)
- [ ] Write `e2e/tests/settings-impact.spec.ts`

### 8.5 — First Run Experience (~17 tests)
- [ ] Write `e2e/tests/first-run.spec.ts`

### 8.6 — Responsive Layout (~17 tests)
- [ ] Write `e2e/tests/responsive.spec.ts`

### 8.7 — Resize During Operation (~7 tests)
- [ ] Write `e2e/tests/resize.spec.ts`

### 8.8 — Scroll & Animation (~8 tests)
- [ ] Write `e2e/tests/scroll-animation.spec.ts`

### 8.9 — Changelog (~4 tests)
- [ ] Write `e2e/tests/changelog.spec.ts`

## Verification Checks

- [ ] `npx playwright test` passes all tests across all 4 viewport projects
- [ ] CI pipeline runs Playwright after vitest
- [ ] Video-on-failure captures available for debugging
