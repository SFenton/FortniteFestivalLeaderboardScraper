---
description: "Use when modifying CSS module files in FortniteFestivalWeb. References CSS migration rules from docs/refactor/."
applyTo: "FortniteFestivalWeb/**/*.module.css"
---

# CSS Module Conventions

- Follow migration rules in `docs/refactor/CSS_MIGRATION_RULES.md`.
- Use CSS custom properties from `src/styles/theme.css` — don't create duplicates.
- Shared effects go in `src/styles/effects.module.css`.
- Animations go in `src/styles/animations.module.css`.
- Mobile-first responsive design — use `@festival/theme` breakpoints.
- Class names: camelCase (e.g., `.navFrosted`, `.headerFrosted`).
