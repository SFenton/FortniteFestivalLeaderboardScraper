---
description: "Use when writing Playwright E2E tests for FortniteFestivalWeb. Covers viewport matrix, page objects, test naming."
applyTo: "FortniteFestivalWeb/e2e/**/*.ts"
---

# Playwright Conventions — FortniteFestivalWeb

- Config: `playwright.config.ts` — 6 viewports (wide-desktop 1920x1080, desktop-wide 1440x900, desktop 1280x800, desktop-narrow 800x800, mobile 375x812, mobile-narrow 320x568).
- Test naming: `*.fre.spec.ts` (Festival Run E2E).
- BaseURL: `http://localhost:3000`.
- Timeout: 30s.
- Test responsive behavior across all 4 viewports.
- Use `page.getByRole()`, `page.getByText()` for selectors.
- Avoid CSS selectors — prefer semantic selectors.
