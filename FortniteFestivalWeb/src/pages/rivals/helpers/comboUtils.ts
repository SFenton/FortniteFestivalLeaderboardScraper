import type { AppSettings } from '../../../contexts/SettingsContext';
import type { ServerInstrumentKey } from '@festival/core/api/serverTypes';
import { comboIdFromInstruments, isWithinGroupComboId } from '@festival/core/combos';

export const PRO_DRUMS_RIVAL_SCOPE = 'pro_drums';

const PRO_DRUMS_FAMILY_INSTRUMENTS: readonly ServerInstrumentKey[] = [
  'Solo_PeripheralCymbals',
  'Solo_PeripheralDrums',
];

const PAD_INSTRUMENTS: readonly ServerInstrumentKey[] = [
  'Solo_Guitar',
  'Solo_Bass',
  'Solo_Drums',
  'Solo_Vocals',
];

const PRO_STRINGS_INSTRUMENTS: readonly ServerInstrumentKey[] = [
  'Solo_PeripheralGuitar',
  'Solo_PeripheralBass',
];

/** Maps AppSettings show-keys to server instrument keys. */
const SETTING_TO_KEY: [keyof AppSettings, ServerInstrumentKey][] = [
  ['showLead', 'Solo_Guitar'],
  ['showBass', 'Solo_Bass'],
  ['showDrums', 'Solo_Drums'],
  ['showVocals', 'Solo_Vocals'],
  ['showProLead', 'Solo_PeripheralGuitar'],
  ['showProBass', 'Solo_PeripheralBass'],
  ['showPeripheralVocals', 'Solo_PeripheralVocals'],
  ['showPeripheralCymbals', 'Solo_PeripheralCymbals'],
  ['showPeripheralDrums', 'Solo_PeripheralDrums'],
];

/** Returns the list of enabled server instrument keys based on app settings. */
export function getEnabledInstruments(settings: AppSettings): ServerInstrumentKey[] {
  return SETTING_TO_KEY.filter(([key]) => settings[key]).map(([, inst]) => inst);
}

/**
 * Derives a supported combo ID (hex bitmask) from enabled instruments.
 * Returns null when 0 or 1 instruments are enabled or when the selection does
 * not map to a supported within-group combo.
 */
export function deriveComboFromSettings(settings: AppSettings): string | null {
  const instruments = getEnabledInstruments(settings);
  if (instruments.length < 2) return null;

  const comboId = comboIdFromInstruments(instruments);
  return isWithinGroupComboId(comboId) ? comboId : null;
}

export function deriveRivalScopeFromSettings(settings: AppSettings): string | null {
  const instruments = getEnabledInstruments(settings);
  if (isExactlyProDrumsFamily(instruments)) return PRO_DRUMS_RIVAL_SCOPE;
  return deriveComboFromSettings(settings);
}

export function deriveRivalScopesFromSettings(settings: AppSettings): string[] {
  const instruments = getEnabledInstruments(settings);
  const scopes: string[] = [];

  const padInstruments = enabledFromGroup(instruments, PAD_INSTRUMENTS);
  if (padInstruments.length >= 2) scopes.push(comboIdFromInstruments(padInstruments));

  const proStringsInstruments = enabledFromGroup(instruments, PRO_STRINGS_INSTRUMENTS);
  if (proStringsInstruments.length >= 2) scopes.push(comboIdFromInstruments(proStringsInstruments));

  if (hasProDrumsFamily(instruments)) scopes.push(PRO_DRUMS_RIVAL_SCOPE);

  if (scopes.length > 0) return scopes;
  return instruments[0] ? [instruments[0]] : [];
}

export function isProDrumsRivalScope(scope: string | null | undefined): boolean {
  return scope === PRO_DRUMS_RIVAL_SCOPE;
}

function hasProDrumsFamily(instruments: readonly ServerInstrumentKey[]): boolean {
  return PRO_DRUMS_FAMILY_INSTRUMENTS.every((instrument) => instruments.includes(instrument));
}

function isExactlyProDrumsFamily(instruments: readonly ServerInstrumentKey[]): boolean {
  return instruments.length === PRO_DRUMS_FAMILY_INSTRUMENTS.length && hasProDrumsFamily(instruments);
}

function enabledFromGroup(instruments: readonly ServerInstrumentKey[], group: readonly ServerInstrumentKey[]): ServerInstrumentKey[] {
  return group.filter((instrument) => instruments.includes(instrument));
}
