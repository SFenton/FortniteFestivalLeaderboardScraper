# Phase 16: CSS Modules → useStyles() + Theme Restructure

**Status:** ✅ Complete
**Completed:** March 25, 2026

## Result

93 of 96 CSS module files deleted. Only 3 irreducible CSS modules remain (`animations.module.css`, `effects.module.css`, `rivals.module.css`) — containing `::after`/`::before`, `@keyframes`, `@media`, `@container`, `:hover`, `backdrop-filter`, `mask-image`, `::placeholder` that cannot be inline styles.

All components use the `useStyles()` + `useMemo` pattern (or module-level const for non-hook class component `ErrorBoundary` and shared `Page` exports).

### Cleanup pass (March 25, 2026)

Fixed issues left from the initial migration:

- **Deleted dead files**: `shared.module.css` (0 importers), `PathsModal.module.css` (0 importers)
- **Fixed broken imports**: `SongRow.tsx` and `PlayerSongRow.tsx` imported non-existent `.module.css` files — migrated to `useStyles()` using `songRowStyles.ts` shared exports
- **Migrated instrumentSelector.module.css**: No pseudo-elements — converted to `InstrumentSelector` `styles` prop (4 consumers: `ScoreHistoryChart`, `FilterDemo`, `InstrumentFilterDemo`, `PathPreviewDemo`)
- **Convention alignment**: Converted 4 module-level `const styles = {}` to `useStyles()` hooks (`SongsToolbar`, `ScoreHistoryChart`, `SongInfoHeader`, `FirstRunCarousel`)
- **Removed coverage exclusion**: `src/components/songs/rows/**` removed from `vite.config.ts` exclude list
- **Fixed pre-existing type bugs**: `Size.lg` → `Size.iconLg`, `Size.xs` → `Size.iconXs`, `Size.xl` → `Size.iconXl` in ScoreHistoryChart (exposed by moving code into hook)
- **Updated test**: PathPreviewDemo `className` assertion → inline `style.backgroundColor` check

### Details

See PLAN.md Phase 16 section for full metrics and batch strategy.
