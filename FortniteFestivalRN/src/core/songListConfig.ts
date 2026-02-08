import type {InstrumentKey} from './instruments';

export type SongSortMode = 'title' | 'artist' | 'hasfc';

export type AdvancedMissingFilters = {
  missingPadFCs: boolean;
  missingProFCs: boolean;
  missingPadScores: boolean;
  missingProScores: boolean;
  includeLead: boolean;
  includeBass: boolean;
  includeDrums: boolean;
  includeVocals: boolean;
  includeProGuitar: boolean;
  includeProBass: boolean;
};

export const defaultAdvancedMissingFilters = (): AdvancedMissingFilters => ({
  missingPadFCs: false,
  missingProFCs: false,
  missingPadScores: false,
  missingProScores: false,
  includeLead: true,
  includeBass: true,
  includeDrums: true,
  includeVocals: true,
  includeProGuitar: true,
  includeProBass: true,
});

export type InstrumentOrderItem = {key: InstrumentKey; displayName: string};

export const defaultPrimaryInstrumentOrder = (): InstrumentOrderItem[] => [
  {key: 'guitar', displayName: 'Lead'},
  {key: 'drums', displayName: 'Drums'},
  {key: 'vocals', displayName: 'Vocals'},
  {key: 'bass', displayName: 'Bass'},
  {key: 'pro_guitar', displayName: 'Pro Lead'},
  {key: 'pro_bass', displayName: 'Pro Bass'},
];

export const normalizeInstrumentOrder = (keys: ReadonlyArray<InstrumentKey> | undefined): InstrumentOrderItem[] => {
  const base = defaultPrimaryInstrumentOrder();
  if (!keys || keys.length === 0) return base;

  const map = new Map<InstrumentKey, InstrumentOrderItem>(base.map(i => [i.key, i]));
  const out: InstrumentOrderItem[] = [];

  for (const k of keys) {
    const it = map.get(k);
    if (it) {
      out.push(it);
      map.delete(k);
    }
  }

  // Append any missing keys (for forward-compat if keys list is stale)
  for (const it of map.values()) out.push(it);

  return out;
};
