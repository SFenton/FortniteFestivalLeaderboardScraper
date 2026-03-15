/**
 * Instrument icon component using the same PNG assets as the React Native mobile app.
 * Assets are served from /instruments/ in the public folder.
 */
import { memo } from 'react';
import type { InstrumentKey } from '@festival/core/instruments';
import type { ServerInstrumentKey } from '@festival/core/api/serverTypes';

type AnyInstrumentKey = InstrumentKey | ServerInstrumentKey;

const BASE = import.meta.env.BASE_URL;

const ICON_PATHS: Record<AnyInstrumentKey, string> = {
  guitar: `${BASE}instruments/guitar.png`,
  bass: `${BASE}instruments/bass.png`,
  drums: `${BASE}instruments/drums.png`,
  vocals: `${BASE}instruments/vocals.png`,
  pro_guitar: `${BASE}instruments/pro_guitar.png`,
  pro_bass: `${BASE}instruments/pro_bass.png`,
  Solo_Guitar: `${BASE}instruments/guitar.png`,
  Solo_Bass: `${BASE}instruments/bass.png`,
  Solo_Drums: `${BASE}instruments/drums.png`,
  Solo_Vocals: `${BASE}instruments/vocals.png`,
  Solo_PeripheralGuitar: `${BASE}instruments/pro_guitar.png`,
  Solo_PeripheralBass: `${BASE}instruments/pro_bass.png`,
};

type IconProps = { size?: number; style?: React.CSSProperties };

export const InstrumentIcon = memo(function InstrumentIcon({ instrument, size = 20, style }: IconProps & { instrument: AnyInstrumentKey }) {
  return (
    <img
      src={ICON_PATHS[instrument]}
      alt={instrument}
      width={size}
      height={size}
      style={{ objectFit: 'contain', ...style }}
    />
  );
});

/** Status colors matching the mobile app's instrument badge chips. */
export const INSTRUMENT_STATUS_COLORS = {
  fullCombo: { fill: '#FFD700', stroke: '#CFA500' },
  hasScore: { fill: '#2ECC71', stroke: '#1E7F46' },
  noScore: { fill: '#C62828', stroke: '#8B0000' },
} as const;

export function getInstrumentStatusVisual(hasScore: boolean, isFullCombo: boolean) {
  if (isFullCombo) return INSTRUMENT_STATUS_COLORS.fullCombo;
  if (hasScore) return INSTRUMENT_STATUS_COLORS.hasScore;
  return INSTRUMENT_STATUS_COLORS.noScore;
}
