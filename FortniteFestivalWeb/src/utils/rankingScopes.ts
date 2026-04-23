import { comboIdFromInstruments, instrumentsFromComboId, isWithinGroupComboId } from '@festival/core/combos';
import { serverInstrumentLabel, type ServerInstrumentKey } from '@festival/core/api/serverTypes';

export type RankingScopeFamily = 'og-band' | 'pro-strings' | 'peripherals';

export type RankingScope =
  | {
      kind: 'instrument';
      family: RankingScopeFamily;
      instruments: readonly [ServerInstrumentKey];
      instrument: ServerInstrumentKey;
      queryValue: ServerInstrumentKey;
      scopeKey: ServerInstrumentKey;
    }
  | {
      kind: 'combo';
      family: RankingScopeFamily;
      instruments: readonly ServerInstrumentKey[];
      comboId: string;
      queryValue: string;
      scopeKey: string;
    };

const SCOPE_FAMILIES: ReadonlyArray<{
  family: RankingScopeFamily;
  supportsCombo: boolean;
  instruments: readonly ServerInstrumentKey[];
}> = [
  {
    family: 'og-band',
    supportsCombo: true,
    instruments: ['Solo_Guitar', 'Solo_Bass', 'Solo_Drums', 'Solo_Vocals'],
  },
  {
    family: 'pro-strings',
    supportsCombo: true,
    instruments: ['Solo_PeripheralGuitar', 'Solo_PeripheralBass'],
  },
  {
    family: 'peripherals',
    supportsCombo: false,
    instruments: ['Solo_PeripheralVocals', 'Solo_PeripheralCymbals', 'Solo_PeripheralDrums'],
  },
];

export function resolveSupportedRankingScopes(
  selectedInstruments: readonly ServerInstrumentKey[],
): RankingScope[] {
  const selectedSet = new Set(selectedInstruments);

  return SCOPE_FAMILIES.flatMap(({ family, instruments, supportsCombo }) => {
    const familySelections = instruments.filter((instrument) => selectedSet.has(instrument));

    if (familySelections.length === 0) {
      return [];
    }

    const instrumentScopes = familySelections.map((instrument) => ({
      kind: 'instrument' as const,
      family,
      instruments: [instrument] as const,
      instrument,
      queryValue: instrument,
      scopeKey: instrument,
    }));

    if (familySelections.length === 1 || !supportsCombo) {
      return instrumentScopes;
    }

    const comboId = comboIdFromInstruments(familySelections);
    return [{
      kind: 'combo',
      family,
      instruments: familySelections,
      comboId,
      queryValue: comboId,
      scopeKey: comboId,
    }, ...instrumentScopes];
  });
}

export function rankingScopeLabel(scope: RankingScope): string {
  return scope.instruments.map((instrument) => serverInstrumentLabel(instrument)).join(' + ');
}

export function comboScopeLabel(comboId: string): string {
  if (!isWithinGroupComboId(comboId)) {
    return comboId;
  }

  return instrumentsFromComboId(comboId)
    .map((instrument) => serverInstrumentLabel(instrument))
    .join(' + ');
}

export function isRankingScopeComboId(category: string): boolean {
  return isWithinGroupComboId(category);
}