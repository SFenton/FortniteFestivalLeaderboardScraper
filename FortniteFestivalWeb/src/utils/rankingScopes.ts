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
  instruments: readonly ServerInstrumentKey[];
}> = [
  {
    family: 'og-band',
    instruments: ['Solo_Guitar', 'Solo_Bass', 'Solo_Drums', 'Solo_Vocals'],
  },
  {
    family: 'pro-strings',
    instruments: ['Solo_PeripheralGuitar', 'Solo_PeripheralBass'],
  },
  {
    family: 'peripherals',
    instruments: ['Solo_PeripheralVocals', 'Solo_PeripheralCymbals', 'Solo_PeripheralDrums'],
  },
];

export function resolveSupportedRankingScopes(
  selectedInstruments: readonly ServerInstrumentKey[],
): RankingScope[] {
  const selectedSet = new Set(selectedInstruments);

  return SCOPE_FAMILIES.flatMap(({ family, instruments }) => {
    const familySelections = instruments.filter((instrument) => selectedSet.has(instrument));

    if (familySelections.length === 0) {
      return [];
    }

    if (familySelections.length === 1) {
      const instrument = familySelections[0]!;
      return [{
        kind: 'instrument',
        family,
        instruments: [instrument],
        instrument,
        queryValue: instrument,
        scopeKey: instrument,
      }];
    }

    const comboId = comboIdFromInstruments(familySelections);
    return [{
      kind: 'combo',
      family,
      instruments: familySelections,
      comboId,
      queryValue: comboId,
      scopeKey: comboId,
    }];
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