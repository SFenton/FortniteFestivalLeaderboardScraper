export const InstrumentKeys = [
  'guitar',
  'bass',
  'drums',
  'vocals',
  'pro_guitar',
  'pro_bass',
] as const;

export type InstrumentKey = (typeof InstrumentKeys)[number];
