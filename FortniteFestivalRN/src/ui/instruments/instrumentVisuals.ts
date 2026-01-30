import type { ImageSourcePropType } from 'react-native';
import type { InstrumentKey } from '../../core/instruments';

export const MAUI_STATUS_COLORS = {
  fullCombo: { fill: '#FFD700', stroke: '#CFA500' },
  hasScore: { fill: '#2ECC71', stroke: '#1E7F46' },
  noScore: { fill: '#C62828', stroke: '#8B0000' },
} as const;

export type InstrumentStatusVisual = {
  fill: string;
  stroke: string;
};

export function getInstrumentStatusVisual(args: {
  hasScore: boolean;
  isFullCombo: boolean;
}): InstrumentStatusVisual {
  if (args.isFullCombo) return MAUI_STATUS_COLORS.fullCombo;
  if (args.hasScore) return MAUI_STATUS_COLORS.hasScore;
  return MAUI_STATUS_COLORS.noScore;
}

// RN needs static requires for bundling; keep this mapping explicit.
const ICONS: Record<InstrumentKey, ImageSourcePropType> = {
  guitar: require('../../assets/instruments/guitar.png'),
  bass: require('../../assets/instruments/bass.png'),
  drums: require('../../assets/instruments/drums.png'),
  vocals: require('../../assets/instruments/vocals.png'),
  pro_guitar: require('../../assets/instruments/pro_guitar.png'),
  pro_bass: require('../../assets/instruments/pro_bass.png'),
};

export function getInstrumentIconSource(instrument: InstrumentKey): ImageSourcePropType {
  return ICONS[instrument];
}
