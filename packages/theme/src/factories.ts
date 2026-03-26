/**
 * Reusable style factory objects for the most common CSS patterns.
 * Spread these into useStyles factories: `{ ...flexColumn, gap: Gap.md }`
 */
import type { CSSProperties } from 'react';

export const flexColumn: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
};

export const flexRow: CSSProperties = {
  display: 'flex',
  alignItems: 'center',
};

export const flexCenter: CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'center',
};

export const flexBetween: CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'space-between',
};

export const textBold: CSSProperties = {
  fontWeight: 700,
};

export const textSemibold: CSSProperties = {
  fontWeight: 600,
};

export const truncate: CSSProperties = {
  overflow: 'hidden',
  textOverflow: 'ellipsis',
  whiteSpace: 'nowrap',
};

export const absoluteFill: CSSProperties = {
  position: 'absolute',
  inset: 0,
};

export const fixedFill: CSSProperties = {
  position: 'fixed',
  inset: 0,
};

/** Vertically center an absolutely-positioned element. */
export const centerVertical: CSSProperties = {
  top: '50%',
  transform: 'translateY(-50%)',
};
