---
description: "Use when writing or modifying React/TSX components in FortniteFestivalWeb. Covers component patterns, hook rules, CSS module pairing."
applyTo: "FortniteFestivalWeb/src/**/*.tsx"
---

# React Conventions — FortniteFestivalWeb

- All pages use `<Page scrollRestoreKey loadPhase firstRun before after>` shell.
- Loading: `useLoadPhase()` → `<ArcSpinner />` → content fade-in.
- Error: `<EmptyState>` with `parseApiError()`.
- Modal state: `useModalState<T>(defaults)`.
- Stagger: `useStaggerStyle()` for items, `buildStaggerStyle()` in `.map()`.
- CSS module for ≥3 rules, inline style for <3.
- Co-locate `.module.css` with component.
- All display components support `style?` + `onAnimationEnd?` for stagger.
- Navigation state → URL searchParams. User preferences → localStorage.
