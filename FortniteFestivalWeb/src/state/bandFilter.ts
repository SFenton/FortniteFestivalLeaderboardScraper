import {
  SERVER_INSTRUMENT_KEYS,
  type BandConfiguration,
  type PlayerBandType,
  type ServerInstrumentKey,
} from '@festival/core/api/serverTypes';
import type { AppliedBandComboFilter, BandInstrumentFilterAssignment } from '../types/bandFilter';
import type { SelectedProfile } from './selectedProfile';

export const BAND_FILTER_STORAGE_KEY = 'fst:bandFilter';

const BAND_TYPES = new Set<PlayerBandType>(['Band_Duets', 'Band_Trios', 'Band_Quad']);
const SERVER_INSTRUMENTS = new Set<ServerInstrumentKey>(SERVER_INSTRUMENT_KEYS);

function getStorage(): Storage | null {
  if (typeof localStorage === 'undefined') return null;
  return localStorage;
}

function normalizeText(value: unknown): string {
  return typeof value === 'string' ? value.trim() : '';
}

function isPlayerBandType(value: string): value is PlayerBandType {
  return BAND_TYPES.has(value as PlayerBandType);
}

function isServerInstrument(value: unknown): value is ServerInstrumentKey {
  return typeof value === 'string' && SERVER_INSTRUMENTS.has(value as ServerInstrumentKey);
}

function normalizeAssignment(value: unknown): BandInstrumentFilterAssignment | null {
  if (!value || typeof value !== 'object') return null;
  const record = value as Record<string, unknown>;
  const accountId = normalizeText(record.accountId);
  const instrument = record.instrument;
  if (!accountId || !isServerInstrument(instrument)) return null;
  return { accountId, instrument };
}

function normalizeMemberInstruments(value: unknown): Record<string, ServerInstrumentKey> | null {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return null;
  const output: Record<string, ServerInstrumentKey> = {};
  for (const [accountId, instrument] of Object.entries(value as Record<string, unknown>)) {
    const normalizedAccountId = normalizeText(accountId);
    if (!normalizedAccountId || !isServerInstrument(instrument)) return null;
    output[normalizedAccountId] = instrument;
  }
  return output;
}

function normalizeBandConfiguration(value: unknown): BandConfiguration | null {
  if (!value || typeof value !== 'object') return null;
  const record = value as Record<string, unknown>;
  const rawInstrumentCombo = normalizeText(record.rawInstrumentCombo);
  const comboId = normalizeText(record.comboId);
  const assignmentKey = normalizeText(record.assignmentKey);
  const appearanceCount = typeof record.appearanceCount === 'number' && Number.isFinite(record.appearanceCount)
    ? record.appearanceCount
    : null;
  const instruments = Array.isArray(record.instruments)
    ? record.instruments.filter(isServerInstrument)
    : [];
  const memberInstruments = normalizeMemberInstruments(record.memberInstruments);
  if (!rawInstrumentCombo || !comboId || !assignmentKey || appearanceCount == null || instruments.length === 0 || !memberInstruments) {
    return null;
  }
  return { rawInstrumentCombo, comboId, instruments, assignmentKey, appearanceCount, memberInstruments };
}

function normalizeConfigurations(value: unknown): BandConfiguration[] | undefined {
  if (!Array.isArray(value)) return undefined;
  const configurations = value.flatMap(configuration => {
    const normalized = normalizeBandConfiguration(configuration);
    return normalized ? [normalized] : [];
  });
  return configurations.length > 0 ? configurations : undefined;
}

export function normalizeAppliedBandFilter(value: unknown): AppliedBandComboFilter | null {
  if (!value || typeof value !== 'object') return null;
  const record = value as Record<string, unknown>;
  const bandId = normalizeText(record.bandId);
  const bandType = normalizeText(record.bandType);
  const teamKey = normalizeText(record.teamKey);
  const comboId = normalizeText(record.comboId);
  const assignments = Array.isArray(record.assignments)
    ? record.assignments.flatMap(assignment => {
        const normalized = normalizeAssignment(assignment);
        return normalized ? [normalized] : [];
      })
    : [];
  if (!bandId || !isPlayerBandType(bandType) || !teamKey || !comboId || assignments.length === 0) return null;
  const configurations = normalizeConfigurations(record.configurations);
  return {
    bandId,
    bandType,
    teamKey,
    comboId,
    assignments,
    ...(configurations ? { configurations } : {}),
  };
}

export function readAppliedBandFilter(): AppliedBandComboFilter | null {
  const storage = getStorage();
  if (!storage) return null;
  try {
    const raw = storage.getItem(BAND_FILTER_STORAGE_KEY);
    if (!raw) return null;
    const normalized = normalizeAppliedBandFilter(JSON.parse(raw));
    if (normalized) return normalized;
  } catch { /* ignore */ }
  storage.removeItem(BAND_FILTER_STORAGE_KEY);
  return null;
}

export function writeAppliedBandFilter(filter: AppliedBandComboFilter): AppliedBandComboFilter {
  const normalized = normalizeAppliedBandFilter(filter);
  if (!normalized) throw new Error('Invalid applied band filter');
  const storage = getStorage();
  if (storage) storage.setItem(BAND_FILTER_STORAGE_KEY, JSON.stringify(normalized));
  return normalized;
}

export function clearAppliedBandFilter(): void {
  const storage = getStorage();
  if (storage) storage.removeItem(BAND_FILTER_STORAGE_KEY);
}

export function isBandFilterForSelectedProfile(filter: AppliedBandComboFilter | null, selectedProfile: SelectedProfile | null): boolean {
  return !!filter
    && selectedProfile?.type === 'band'
    && filter.bandId === selectedProfile.bandId
    && filter.bandType === selectedProfile.bandType
    && filter.teamKey === selectedProfile.teamKey;
}

export function readAppliedBandFilterForSelectedProfile(selectedProfile: SelectedProfile | null): AppliedBandComboFilter | null {
  const filter = readAppliedBandFilter();
  if (!filter) return null;
  if (isBandFilterForSelectedProfile(filter, selectedProfile)) return filter;
  clearAppliedBandFilter();
  return null;
}