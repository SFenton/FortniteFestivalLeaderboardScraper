import type { AppSettings } from '../../../contexts/SettingsContext';
import type { ServerInstrumentKey } from '@festival/core/api/serverTypes';

/** Maps AppSettings show-keys to server instrument keys. */
const SETTING_TO_KEY: [keyof AppSettings, ServerInstrumentKey][] = [
  ['showLead', 'Solo_Guitar'],
  ['showBass', 'Solo_Bass'],
  ['showDrums', 'Solo_Drums'],
  ['showVocals', 'Solo_Vocals'],
  ['showProLead', 'Solo_PeripheralGuitar'],
  ['showProBass', 'Solo_PeripheralBass'],
];

/** Returns the list of enabled server instrument keys based on app settings. */
export function getEnabledInstruments(settings: AppSettings): ServerInstrumentKey[] {
  return SETTING_TO_KEY.filter(([key]) => settings[key]).map(([, inst]) => inst);
}

/**
 * Derives the combo string from enabled instruments.
 * Returns null if 0 or 1 instruments are enabled (combo needs 2+).
 * Instruments are joined with '+' in the canonical order.
 */
export function deriveComboFromSettings(settings: AppSettings): string | null {
  const instruments = getEnabledInstruments(settings);
  if (instruments.length < 2) return null;
  return instruments.join('+');
}
