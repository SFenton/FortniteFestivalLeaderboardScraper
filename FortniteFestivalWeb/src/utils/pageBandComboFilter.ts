import { instrumentsFromComboId } from '@festival/core';
import {
  SERVER_INSTRUMENT_KEYS,
  serverInstrumentLabel,
  type BandComboCatalogEntry,
  type PlayerBandType,
  type ServerInstrumentKey,
} from '@festival/core/api/serverTypes';
import type { AppliedBandComboFilter } from '../types/bandFilter';

export const PAGE_BAND_COMBO_ALL_VALUE = 'all';

export type PageBandComboSource = 'page' | 'global' | 'all' | 'none';

export type PageBandComboState = {
  comboId?: string;
  source: PageBandComboSource;
};

const SERVER_INSTRUMENT_SET = new Set<ServerInstrumentKey>(SERVER_INSTRUMENT_KEYS);
const SERVER_INSTRUMENT_ORDER = new Map<ServerInstrumentKey, number>(SERVER_INSTRUMENT_KEYS.map((instrument, index) => [instrument, index]));

export function resolvePageBandComboState(
  bandType: PlayerBandType | null,
  searchParams: URLSearchParams,
  appliedFilter: AppliedBandComboFilter | null,
): PageBandComboState {
  const rawCombo = searchParams.get('combo')?.trim().replace(/ /g, '+');
  if (rawCombo === PAGE_BAND_COMBO_ALL_VALUE) return { source: 'all' };
  if (rawCombo) return { comboId: rawCombo, source: 'page' };
  if (bandType && appliedFilter?.bandType === bandType) return { comboId: appliedFilter.comboId, source: 'global' };
  return { source: 'none' };
}

export function getPageBandComboInstruments(
  state: PageBandComboState,
  bandType: PlayerBandType | null,
  appliedFilter: AppliedBandComboFilter | null,
  catalogEntries?: readonly BandComboCatalogEntry[],
): ServerInstrumentKey[] {
  if (!state.comboId) return [];

  const catalogEntry = catalogEntries?.find(entry => entry.comboId === state.comboId);
  if (catalogEntry?.instruments.length) return [...catalogEntry.instruments];

  const parsed = parseComboIdInstruments(state.comboId);
  if (parsed) return parsed;

  if (bandType && appliedFilter?.bandType === bandType && appliedFilter.comboId === state.comboId) {
    return appliedFilter.assignments.map(assignment => assignment.instrument);
  }

  return [];
}

export function formatPageBandComboLabel(instruments: readonly ServerInstrumentKey[]): string {
  return instruments.map(serverInstrumentLabel).join(' / ');
}

export function parseComboIdInstruments(comboId: string): ServerInstrumentKey[] | null {
  try {
    return instrumentsFromComboId(comboId);
  } catch {
    const legacyParts = comboId.split('+').filter((part): part is ServerInstrumentKey => SERVER_INSTRUMENT_SET.has(part as ServerInstrumentKey));
    return legacyParts.length > 0 ? legacyParts : null;
  }
}

export function bandComboIdFromInstruments(instruments: readonly ServerInstrumentKey[]): string {
  return [...instruments]
    .sort((a, b) => (SERVER_INSTRUMENT_ORDER.get(a) ?? Number.MAX_SAFE_INTEGER) - (SERVER_INSTRUMENT_ORDER.get(b) ?? Number.MAX_SAFE_INTEGER) || a.localeCompare(b))
    .join('+');
}