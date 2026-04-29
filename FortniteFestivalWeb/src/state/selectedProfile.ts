import type { BandType } from '@festival/core/api/serverTypes';

export const SELECTED_PROFILE_STORAGE_KEY = 'fst:selectedProfile';
export const SELECTED_PROFILE_SYNC_EVENT = 'fst:selectedProfileChanged';
export const LEGACY_TRACKED_PLAYER_STORAGE_KEY = 'fst:trackedPlayer';
export const LEGACY_TRACKED_PLAYER_SYNC_EVENT = 'fst:trackedPlayerChanged';

const UNKNOWN_USER = 'Unknown User';
const UNKNOWN_BAND = 'Unknown Band';

export type SelectedPlayerProfile = {
  type: 'player';
  accountId: string;
  displayName: string;
};

export type SelectedBandProfile = {
  type: 'band';
  bandId: string;
  bandType: BandType;
  teamKey: string;
  displayName: string;
  members: SelectedBandMemberProfile[];
};

export type SelectedBandMemberProfile = {
  accountId: string;
  displayName: string;
};

export type SelectedProfile = SelectedPlayerProfile | SelectedBandProfile;

function getStorage(): Storage | null {
  if (typeof localStorage === 'undefined') return null;
  return localStorage;
}

function dispatchSelectionEvents(): void {
  if (typeof window === 'undefined') return;
  window.dispatchEvent(new Event(SELECTED_PROFILE_SYNC_EVENT));
  window.dispatchEvent(new Event(LEGACY_TRACKED_PLAYER_SYNC_EVENT));
}

function normalizeText(value: unknown): string {
  return typeof value === 'string' ? value.trim() : '';
}

function normalizePlayerProfile(value: unknown): SelectedPlayerProfile | null {
  if (!value || typeof value !== 'object') return null;
  const record = value as Record<string, unknown>;
  const accountId = normalizeText(record.accountId);
  if (!accountId) return null;
  return {
    type: 'player',
    accountId,
    displayName: normalizeText(record.displayName) || UNKNOWN_USER,
  };
}

function normalizeBandMembers(value: unknown): SelectedBandMemberProfile[] {
  if (!Array.isArray(value)) return [];
  return value.flatMap(member => {
    if (!member || typeof member !== 'object') return [];
    const record = member as Record<string, unknown>;
    const accountId = normalizeText(record.accountId);
    if (!accountId) return [];
    return [{
      accountId,
      displayName: normalizeText(record.displayName) || UNKNOWN_USER,
    }];
  });
}

function normalizeBandProfile(value: unknown): SelectedBandProfile | null {
  if (!value || typeof value !== 'object') return null;
  const record = value as Record<string, unknown>;
  const bandId = normalizeText(record.bandId);
  const bandType = normalizeText(record.bandType);
  const teamKey = normalizeText(record.teamKey);
  if (!bandId || !bandType || !teamKey) return null;
  return {
    type: 'band',
    bandId,
    bandType: bandType as BandType,
    teamKey,
    displayName: normalizeText(record.displayName) || UNKNOWN_BAND,
    members: normalizeBandMembers(record.members),
  };
}

export function normalizeSelectedProfile(value: unknown): SelectedProfile | null {
  if (!value || typeof value !== 'object') return null;
  const record = value as Record<string, unknown>;
  if (record.type === 'player') return normalizePlayerProfile(record);
  if (record.type === 'band') return normalizeBandProfile(record);
  return null;
}

function readJson(storage: Storage, key: string): unknown {
  const raw = storage.getItem(key);
  if (!raw) return null;
  return JSON.parse(raw);
}

function writeProfileToStorage(storage: Storage, profile: SelectedProfile): void {
  storage.setItem(SELECTED_PROFILE_STORAGE_KEY, JSON.stringify(profile));
  if (profile.type === 'player') {
    storage.setItem(LEGACY_TRACKED_PLAYER_STORAGE_KEY, JSON.stringify({
      accountId: profile.accountId,
      displayName: profile.displayName,
    }));
  } else {
    storage.removeItem(LEGACY_TRACKED_PLAYER_STORAGE_KEY);
  }
}

export function readSelectedProfile(): SelectedProfile | null {
  const storage = getStorage();
  if (!storage) return null;

  try {
    const selectedProfile = normalizeSelectedProfile(readJson(storage, SELECTED_PROFILE_STORAGE_KEY));
    if (selectedProfile) {
      if (selectedProfile.type === 'player' && !storage.getItem(LEGACY_TRACKED_PLAYER_STORAGE_KEY)) {
        storage.removeItem(SELECTED_PROFILE_STORAGE_KEY);
        return null;
      }
      return selectedProfile;
    }
    storage.removeItem(SELECTED_PROFILE_STORAGE_KEY);
  } catch {
    storage.removeItem(SELECTED_PROFILE_STORAGE_KEY);
  }

  try {
    const legacyPlayer = normalizePlayerProfile(readJson(storage, LEGACY_TRACKED_PLAYER_STORAGE_KEY));
    if (!legacyPlayer) return null;
    writeProfileToStorage(storage, legacyPlayer);
    return legacyPlayer;
  } catch {
    storage.removeItem(LEGACY_TRACKED_PLAYER_STORAGE_KEY);
    return null;
  }
}

export function writeSelectedProfile(profile: SelectedProfile): SelectedProfile {
  const normalized = normalizeSelectedProfile(profile);
  if (!normalized) throw new Error('Invalid selected profile');
  const storage = getStorage();
  if (storage) writeProfileToStorage(storage, normalized);
  dispatchSelectionEvents();
  return normalized;
}

export function clearSelectedProfileStorage(): void {
  const storage = getStorage();
  if (storage) {
    storage.removeItem(SELECTED_PROFILE_STORAGE_KEY);
    storage.removeItem(LEGACY_TRACKED_PLAYER_STORAGE_KEY);
  }
  dispatchSelectionEvents();
}