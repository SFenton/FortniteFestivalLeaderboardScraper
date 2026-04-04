/**
 * Shared song-row layout styles used by both production SongRow and first-run demos.
 * Replaces songRow.module.css — import and spread into style props.
 */
import type { CSSProperties } from 'react';
import { frostedCard, flexRow, flexColumn, flexCenter, Gap, Radius, InstrumentSize } from '@festival/theme';

/** Desktop row: horizontal flex with frosted card surface. */
export const songRow: CSSProperties = {
  ...frostedCard,
  ...flexRow,
  gap: Gap.xl,
  padding: `0 ${Gap.xl}px`,
  height: 64,
  borderRadius: Radius.md,
};

/** Mobile row: vertical flex with frosted card surface. */
export const songRowMobile: CSSProperties = {
  ...frostedCard,
  ...flexColumn,
  gap: Gap.md,
  padding: `${Gap.lg}px ${Gap.xl}px`,
  borderRadius: Radius.md,
};

/** Inner top row for mobile two-line layout. */
export const mobileTopRow: CSSProperties = {
  ...flexRow,
  gap: Gap.xl,
};

/** Right-aligned detail strip (desktop). */
export const detailStrip: CSSProperties = {
  ...flexRow,
  gap: Gap.xl,
  flexShrink: 0,
  marginLeft: 'auto',
};

/** Score metadata container. */
export const scoreMeta: CSSProperties = {
  ...flexRow,
  gap: Gap.xl,
  flexShrink: 1,
};

/** Wrapping metadata row (mobile). */
export const metadataWrap: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  alignItems: 'center',
  justifyContent: 'flex-end',
  gap: Gap.lg,
};

/** Instrument status chip row. */
export const instrumentStatusRow: CSSProperties = {
  display: 'flex',
  gap: Gap.sm,
  alignItems: 'center',
  flexShrink: 0,
};

/** Single instrument status chip (round icon with bg/border color). */
export const instrumentStatusChip: CSSProperties = {
  ...flexCenter,
  width: InstrumentSize.chip,
  height: InstrumentSize.chip,
  borderRadius: '50%',
  borderWidth: 2,
  borderStyle: 'solid',
  flexShrink: 0,
};

/** Metadata item wrapper (for neighbor-detection padding). */
export const metadataItemAlone: CSSProperties = {};

/** Metadata item with left neighbor. */
export const metadataItemLeft: CSSProperties = {};

/** Metadata item with right neighbor. */
export const metadataItemRight: CSSProperties = {};

/** Metadata item with both neighbors (adds horizontal padding for breathing room). */
export const metadataItemBoth: CSSProperties = {
  padding: '0 8px',
};
