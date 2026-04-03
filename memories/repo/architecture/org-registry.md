# Agent Organization Registry

> Last updated: 2026-04-03
> Updated by: festival-score-tracker (root)

## Hierarchy

```
festival-score-tracker (root / user entry point)
├── fst-head (FSTService domain head)
│   ├── fst-api (API endpoints, controllers)
│   ├── fst-auth (Epic OAuth, API key auth)
│   ├── fst-persistence (database queries, repositories)
│   ├── fst-scrape-pipeline (9-phase scraper, orchestrators)
│   ├── fst-rivals (rivals/opps calculation)
│   ├── fst-performance (service-level perf)
│   └── fst-testing (xUnit tests, coverage)
├── web-head (FortniteFestivalWeb domain head)
│   ├── web-components (reusable UI components)
│   ├── web-features-coord (feature area coordinator)
│   │   ├── web-feat-songs
│   │   ├── web-feat-rivals
│   │   ├── web-feat-shop
│   │   ├── web-feat-player
│   │   ├── web-feat-leaderboards
│   │   ├── web-feat-suggestions
│   │   ├── web-feat-settings
│   │   └── web-feat-shell
│   ├── web-state (React Query, contexts, state management)
│   ├── web-styling (CSS modules, theme, responsive)
│   ├── web-performance (bundle size, render perf)
│   └── web-test-lead (test coordination)
│       ├── web-test-vitest (unit tests)
│       └── web-test-playwright (E2E tests)
├── fst-principal-architect (service architecture, .NET patterns)
├── fst-principal-api-designer (API design, REST conventions, caching)
├── fst-principal-db (PostgreSQL, schema, queries, migrations)
├── web-principal-architect (React/TS architecture, build tooling)
├── web-principal-designer (UX, responsive, accessibility, visual)
├── api-contract (cross-repo API alignment)
├── performance (system-wide performance)
├── security (OWASP, auth, rate limiting)
├── cicd (GitHub Actions, Docker, coverage gates)
├── shared-packages (packages/core, theme, ui-utils, auth)
└── testing-vteam (cross-repo test coordination)
```

## Agent Count: 37 total
- 1 root
- 2 heads
- 5 principals
- 5 cross-cutting
- 7 FST leaf agents
- 17 web leaf agents

## Communication Links
- Heads report to root
- Principals advise root + both heads
- Cross-cutting agents are invoked by root or heads as needed
- Leaf agents report to their parent head
- web-features-coord coordinates all web-feat-* agents

## Memory Ownership
| Agent | Memory area |
|---|---|
| fst-principal-architect | /memories/repo/architecture/fst-service-patterns.md |
| fst-principal-api-designer | /memories/repo/architecture/api-consistency-registry.md |
| fst-principal-db | /memories/repo/architecture/database-registry.md |
| web-principal-architect | /memories/repo/architecture/web-architecture-patterns.md |
| web-principal-designer | /memories/repo/architecture/web-design-patterns.md |
| api-contract | /memories/repo/architecture/api-contract-registry.md |
| performance | /memories/repo/architecture/performance-baselines.md |
| security | /memories/repo/architecture/security-posture.md |
| cicd | /memories/repo/architecture/cicd-pipelines.md |
| shared-packages | /memories/repo/architecture/shared-packages-registry.md |
| testing-vteam | /memories/repo/architecture/testing-strategy.md |
| fst-head | /memories/repo/architecture/fst-service-overview.md |
| web-head | /memories/repo/architecture/web-app-overview.md |
