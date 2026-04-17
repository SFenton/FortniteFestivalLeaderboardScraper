export const InstrumentKeys = [
  'guitar',
  'bass',
  'drums',
  'vocals',
  'pro_guitar',
  'pro_bass',
  'peripheral_vocals',
  'peripheral_cymbals',
  'peripheral_drums',
] as const;

export type InstrumentKey = (typeof InstrumentKeys)[number];
