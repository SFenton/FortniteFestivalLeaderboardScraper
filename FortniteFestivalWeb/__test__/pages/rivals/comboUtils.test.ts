import { describe, it, expect } from 'vitest';
import { deriveComboFromSettings, deriveRivalScopeFromSettings, deriveRivalScopesFromSettings, getEnabledInstruments } from '../../../src/pages/rivals/helpers/comboUtils';
import { defaultAppSettings, type AppSettings } from '../../../src/contexts/SettingsContext';

function settings(overrides: Partial<AppSettings> = {}): AppSettings {
  return { ...defaultAppSettings(), ...overrides };
}

function visibleInstrumentSettings(overrides: Partial<AppSettings> = {}): AppSettings {
  return settings({
    showLead: false,
    showBass: false,
    showDrums: false,
    showVocals: false,
    showProLead: false,
    showProBass: false,
    showPeripheralVocals: false,
    showPeripheralCymbals: false,
    showPeripheralDrums: false,
    ...overrides,
  });
}

describe('getEnabledInstruments', () => {
  it('returns all instruments when all enabled', () => {
    expect(getEnabledInstruments(settings())).toEqual([
      'Solo_Guitar', 'Solo_Bass', 'Solo_Drums', 'Solo_Vocals',
      'Solo_PeripheralGuitar', 'Solo_PeripheralBass',
      'Solo_PeripheralVocals', 'Solo_PeripheralCymbals', 'Solo_PeripheralDrums',
    ]);
  });

  it('returns only enabled instruments', () => {
    const result = getEnabledInstruments(visibleInstrumentSettings({
      showLead: true, showBass: false, showDrums: true,
      showVocals: false, showProLead: false, showProBass: false,
    }));
    expect(result).toEqual(['Solo_Guitar', 'Solo_Drums']);
  });

  it('returns empty array when none enabled', () => {
    expect(getEnabledInstruments(visibleInstrumentSettings({
      showLead: false, showBass: false, showDrums: false,
      showVocals: false, showProLead: false, showProBass: false,
    }))).toEqual([]);
  });
});

describe('deriveComboFromSettings', () => {
  it('returns combo ID for 2+ instruments', () => {
    const result = deriveComboFromSettings(visibleInstrumentSettings({
      showLead: true, showBass: true, showDrums: false,
      showVocals: false, showProLead: false, showProBass: false,
    }));
    expect(result).toBe('03'); // Guitar(0) + Bass(1) = 0x03
  });

  it('returns null for single instrument', () => {
    const result = deriveComboFromSettings(visibleInstrumentSettings({
      showLead: true, showBass: false, showDrums: false,
      showVocals: false, showProLead: false, showProBass: false,
    }));
    expect(result).toBeNull();
  });

  it('returns null for zero instruments', () => {
    const result = deriveComboFromSettings(visibleInstrumentSettings({
      showLead: false, showBass: false, showDrums: false,
      showVocals: false, showProLead: false, showProBass: false,
    }));
    expect(result).toBeNull();
  });

  it('returns null when enabled instruments span unsupported groups', () => {
    const result = deriveComboFromSettings(settings());
    expect(result).toBeNull();
  });

  it('returns null for cross-group instrument selections', () => {
    const result = deriveComboFromSettings(visibleInstrumentSettings({
      showLead: false, showBass: true, showDrums: false,
      showVocals: true, showProLead: false, showProBass: true,
    }));
    expect(result).toBeNull();
  });
});

describe('deriveRivalScopeFromSettings', () => {
  it('returns pro_drums when exactly both pro drums family instruments are enabled', () => {
    const result = deriveRivalScopeFromSettings(visibleInstrumentSettings({
      showPeripheralCymbals: true,
      showPeripheralDrums: true,
    }));

    expect(result).toBe('pro_drums');
  });

  it('does not collapse broader settings to only pro_drums', () => {
    const result = deriveRivalScopeFromSettings(visibleInstrumentSettings({
      showLead: true,
      showBass: true,
      showPeripheralCymbals: true,
      showPeripheralDrums: true,
    }));

    expect(result).toBeNull();
  });

  it('does not return pro_drums when only pro cymbals is enabled', () => {
    const result = deriveRivalScopeFromSettings(visibleInstrumentSettings({
      showPeripheralCymbals: true,
    }));

    expect(result).toBeNull();
  });

  it('does not return pro_drums when only pro drums is enabled', () => {
    const result = deriveRivalScopeFromSettings(visibleInstrumentSettings({
      showPeripheralDrums: true,
    }));

    expect(result).toBeNull();
  });
});

describe('deriveRivalScopesFromSettings', () => {
  it('returns visible multi-instrument groups plus pro drums family for broad settings', () => {
    const result = deriveRivalScopesFromSettings(settings());

    expect(result).toEqual(['0f', '30', 'pro_drums']);
  });

  it('returns pad and pro drums scopes together when both groups are visible', () => {
    const result = deriveRivalScopesFromSettings(visibleInstrumentSettings({
      showLead: true,
      showDrums: true,
      showPeripheralCymbals: true,
      showPeripheralDrums: true,
    }));

    expect(result).toEqual(['05', 'pro_drums']);
  });

  it('falls back to a single exact instrument only when no multi-scope exists', () => {
    const result = deriveRivalScopesFromSettings(visibleInstrumentSettings({
      showPeripheralDrums: true,
    }));

    expect(result).toEqual(['Solo_PeripheralDrums']);
  });
});
