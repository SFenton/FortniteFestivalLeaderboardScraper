import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import type { ReactNode } from 'react';
import {
  SettingsProvider,
  useSettings,
  defaultAppSettings,
  isInstrumentVisible,
  visibleInstruments,
} from '../../src/contexts/SettingsContext';
import type { AppSettings } from '../../src/contexts/SettingsContext';


const STORAGE_KEY = 'fst:appSettings';

function wrapper({ children }: { children: ReactNode }) {
  return <SettingsProvider>{children}</SettingsProvider>;
}

beforeEach(() => {
  localStorage.clear();
});

describe('SettingsContext', () => {
  describe('defaultAppSettings', () => {
    it('returns all instruments visible by default', () => {
      const d = defaultAppSettings();
      expect(d.showLead).toBe(true);
      expect(d.showBass).toBe(true);
      expect(d.showDrums).toBe(true);
      expect(d.showVocals).toBe(true);
      expect(d.showProLead).toBe(true);
      expect(d.showProBass).toBe(true);
    });

    it('returns all metadata visible by default', () => {
      const d = defaultAppSettings();
      expect(d.metadataShowScore).toBe(true);
      expect(d.metadataShowPercentage).toBe(true);
      expect(d.metadataShowPercentile).toBe(true);
      expect(d.metadataShowSeasonAchieved).toBe(true);
      expect(d.metadataShowDifficulty).toBe(true);
      expect(d.metadataShowStars).toBe(true);
    });

    it('has instrument icons shown by default', () => {
      expect(defaultAppSettings().songsHideInstrumentIcons).toBe(false);
    });

    it('has visual order disabled by default', () => {
      expect(defaultAppSettings().songRowVisualOrderEnabled).toBe(false);
    });
  });

  describe('isInstrumentVisible', () => {
    it('returns true for visible instruments', () => {
      const s = defaultAppSettings();
      expect(isInstrumentVisible(s, 'Solo_Guitar')).toBe(true);
      expect(isInstrumentVisible(s, 'Solo_PeripheralBass')).toBe(true);
    });

    it('returns false when instrument is hidden', () => {
      const s = { ...defaultAppSettings(), showLead: false };
      expect(isInstrumentVisible(s, 'Solo_Guitar')).toBe(false);
    });
  });

  describe('visibleInstruments', () => {
    it('returns all instruments when all visible', () => {
      expect(visibleInstruments(defaultAppSettings())).toHaveLength(6);
    });

    it('excludes hidden instruments', () => {
      const s = { ...defaultAppSettings(), showDrums: false, showProBass: false };
      const visible = visibleInstruments(s);
      expect(visible).toHaveLength(4);
      expect(visible).not.toContain('Solo_Drums');
      expect(visible).not.toContain('Solo_PeripheralBass');
    });
  });

  describe('useSettings hook', () => {
    it('provides default settings when localStorage is empty', () => {
      const { result } = renderHook(() => useSettings(), { wrapper });
      expect(result.current.settings).toEqual(defaultAppSettings());
    });

    it('loads persisted settings from localStorage', () => {
      const custom: AppSettings = { ...defaultAppSettings(), showLead: false, metadataShowStars: false };
      localStorage.setItem(STORAGE_KEY, JSON.stringify(custom));

      const { result } = renderHook(() => useSettings(), { wrapper });
      expect(result.current.settings.showLead).toBe(false);
      expect(result.current.settings.metadataShowStars).toBe(false);
    });

    it('merges partial persisted settings with defaults', () => {
      localStorage.setItem(STORAGE_KEY, JSON.stringify({ showBass: false }));

      const { result } = renderHook(() => useSettings(), { wrapper });
      expect(result.current.settings.showBass).toBe(false);
      // Other settings should have defaults
      expect(result.current.settings.showLead).toBe(true);
      expect(result.current.settings.metadataShowScore).toBe(true);
    });

    it('handles corrupt localStorage gracefully', () => {
      localStorage.setItem(STORAGE_KEY, 'not-json');

      const { result } = renderHook(() => useSettings(), { wrapper });
      expect(result.current.settings).toEqual(defaultAppSettings());
    });

    it('updateSettings merges partial updates', () => {
      const { result } = renderHook(() => useSettings(), { wrapper });

      act(() => {
        result.current.updateSettings({ showLead: false });
      });

      expect(result.current.settings.showLead).toBe(false);
      expect(result.current.settings.showBass).toBe(true);
    });

    it('setSettings replaces the entire settings object', () => {
      const { result } = renderHook(() => useSettings(), { wrapper });
      const custom = { ...defaultAppSettings(), showDrums: false };

      act(() => {
        result.current.setSettings(custom);
      });

      expect(result.current.settings.showDrums).toBe(false);
      expect(result.current.settings.showLead).toBe(true);
    });

    it('resetSettings restores defaults', () => {
      const { result } = renderHook(() => useSettings(), { wrapper });

      act(() => {
        result.current.updateSettings({ showLead: false, showBass: false });
      });
      expect(result.current.settings.showLead).toBe(false);

      act(() => {
        result.current.resetSettings();
      });
      expect(result.current.settings).toEqual(defaultAppSettings());
    });

    it('persists settings to localStorage on change', async () => {
      const { result } = renderHook(() => useSettings(), { wrapper });

      act(() => {
        result.current.updateSettings({ showVocals: false });
      });

      // Settings are persisted via useEffect, wait for it
      await vi.waitFor(() => {
        const stored = JSON.parse(localStorage.getItem(STORAGE_KEY)!);
        expect(stored.showVocals).toBe(false);
      });
    });
  });

  describe('useSettings outside provider', () => {
    it('throws when used outside SettingsProvider', () => {
      // Suppress console.error for the expected error
      const spy = vi.spyOn(console, 'error').mockImplementation(() => {});
      expect(() => renderHook(() => useSettings())).toThrow(
        'useSettings must be used within a SettingsProvider',
      );
      spy.mockRestore();
    });
  });
});
