/**
 * Instrument icon component using the same PNG assets as the React Native mobile app.
 * Assets are served from /instruments/ in the public folder.
 */
import type { InstrumentKey } from '@festival/core/instruments';
import type { ServerInstrumentKey } from '@festival/core/api/serverTypes';

type AnyInstrumentKey = InstrumentKey | ServerInstrumentKey;

const ICON_PATHS: Record<AnyInstrumentKey, string> = {
  guitar: '/instruments/guitar.png',
  bass: '/instruments/bass.png',
  drums: '/instruments/drums.png',
  vocals: '/instruments/vocals.png',
  pro_guitar: '/instruments/pro_guitar.png',
  pro_bass: '/instruments/pro_bass.png',
  Solo_Guitar: '/instruments/guitar.png',
  Solo_Bass: '/instruments/bass.png',
  Solo_Drums: '/instruments/drums.png',
  Solo_Vocals: '/instruments/vocals.png',
  Solo_PeripheralGuitar: '/instruments/pro_guitar.png',
  Solo_PeripheralBass: '/instruments/pro_bass.png',
};

type IconProps = { size?: number; style?: React.CSSProperties };

export function InstrumentIcon({ instrument, size = 20, style }: IconProps & { instrument: AnyInstrumentKey }) {
  return (
    <img
      src={ICON_PATHS[instrument]}
      alt={instrument}
      width={size}
      height={size}
      style={{ objectFit: 'contain', ...style }}
    />
  );
}

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
