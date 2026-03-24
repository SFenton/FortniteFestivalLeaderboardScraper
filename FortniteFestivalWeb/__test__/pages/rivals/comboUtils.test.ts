import { describe, it, expect } from 'vitest';
import { deriveComboFromSettings, getEnabledInstruments } from '../../../src/pages/rivals/helpers/comboUtils';
import { defaultAppSettings, type AppSettings } from '../../../src/contexts/SettingsContext';

function settings(overrides: Partial<AppSettings> = {}): AppSettings {
  return { ...defaultAppSettings(), ...overrides };
}

describe('getEnabledInstruments', () => {
  it('returns all 6 instruments when all enabled', () => {
    expect(getEnabledInstruments(settings())).toEqual([
      'Solo_Guitar', 'Solo_Bass', 'Solo_Drums', 'Solo_Vocals',
      'Solo_PeripheralGuitar', 'Solo_PeripheralBass',
    ]);
  });

  it('returns only enabled instruments', () => {
    const result = getEnabledInstruments(settings({
      showLead: true, showBass: false, showDrums: true,
      showVocals: false, showProLead: false, showProBass: false,
    }));
    expect(result).toEqual(['Solo_Guitar', 'Solo_Drums']);
  });

  it('returns empty array when none enabled', () => {
    expect(getEnabledInstruments(settings({
      showLead: false, showBass: false, showDrums: false,
      showVocals: false, showProLead: false, showProBass: false,
    }))).toEqual([]);
  });
});

describe('deriveComboFromSettings', () => {
  it('returns combo string for 2+ instruments', () => {
    const result = deriveComboFromSettings(settings({
      showLead: true, showBass: true, showDrums: false,
      showVocals: false, showProLead: false, showProBass: false,
    }));
    expect(result).toBe('Solo_Guitar+Solo_Bass');
  });

  it('returns null for single instrument', () => {
    const result = deriveComboFromSettings(settings({
      showLead: true, showBass: false, showDrums: false,
      showVocals: false, showProLead: false, showProBass: false,
    }));
    expect(result).toBeNull();
  });

  it('returns null for zero instruments', () => {
    const result = deriveComboFromSettings(settings({
      showLead: false, showBass: false, showDrums: false,
      showVocals: false, showProLead: false, showProBass: false,
    }));
    expect(result).toBeNull();
  });

  it('returns full combo when all enabled', () => {
    const result = deriveComboFromSettings(settings());
    expect(result).toBe(
      'Solo_Guitar+Solo_Bass+Solo_Drums+Solo_Vocals+Solo_PeripheralGuitar+Solo_PeripheralBass',
    );
  });

  it('preserves canonical instrument order', () => {
    const result = deriveComboFromSettings(settings({
      showLead: false, showBass: true, showDrums: false,
      showVocals: true, showProLead: false, showProBass: true,
    }));
    // Bass comes before Vocals which comes before ProBass
    expect(result).toBe('Solo_Bass+Solo_Vocals+Solo_PeripheralBass');
  });
});
