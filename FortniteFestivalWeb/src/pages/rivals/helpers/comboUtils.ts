import type { AppSettings } from '../../../contexts/SettingsContext';
import type { ServerInstrumentKey } from '@festival/core/api/serverTypes';
import { comboIdFromInstruments, isWithinGroupComboId } from '@festival/core/combos';

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
