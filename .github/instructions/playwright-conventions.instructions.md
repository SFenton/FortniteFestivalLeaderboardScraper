---
description: "Use when writing Playwright E2E tests for FortniteFestivalWeb. Covers viewport matrix, page objects, test naming."
applyTo: "FortniteFestivalWeb/e2e/**/*.ts"
---

# Playwright Conventions — FortniteFestivalWeb

- Config: `playwright.config.ts` — 4 viewports (desktop 1280x800, desktop-narrow 800x800, mobile 375x812, mobile-narrow 320x568).
- Test naming: `*.fre.spec.ts` (Festival Run E2E).
- BaseURL: `http://localhost:5173`.
- Timeout: 30s.
- Test responsive behavior across all 4 viewports.
- Use `page.getByRole()`, `page.getByText()` for selectors.
- Avoid CSS selectors — prefer semantic selectors.
