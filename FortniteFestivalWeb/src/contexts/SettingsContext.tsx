import {
  createContext,
  useContext,
  useState,
  useCallback,
  useEffect,
  useMemo,
  type ReactNode,
} from 'react';
import type { ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { INSTRUMENT_KEYS } from '@festival/core/api/serverTypes';
import { DEFAULT_METADATA_ORDER } from '../utils/songSettings';

/* ── Settings shape ── */

export type AppSettings = {
  /* App settings */
  songsHideInstrumentIcons: boolean;
  songRowVisualOrderEnabled: boolean;
  songRowVisualOrder: string[];
  filterInvalidScores: boolean;
  filterInvalidScoresLeeway: number;
  enableExperimentalRanks: boolean;
  disableLightTrails: boolean;

  /* Item Shop */
  hideItemShop: boolean;
  disableShopHighlighting: boolean;

  /* Show instruments */
  showLead: boolean;
  showBass: boolean;
  showDrums: boolean;
  showVocals: boolean;
  showProLead: boolean;
  showProBass: boolean;

  /* Show instrument metadata */
  metadataShowScore: boolean;
  metadataShowPercentage: boolean;
  metadataShowPercentile: boolean;
  metadataShowSeasonAchieved: boolean;
  metadataShowDifficulty: boolean;
  metadataShowStars: boolean;
  metadataShowMaxDistance: boolean;
};

export const defaultAppSettings = (): AppSettings => ({
  songsHideInstrumentIcons: false,
  songRowVisualOrderEnabled: false,
  songRowVisualOrder: [...DEFAULT_METADATA_ORDER],
  filterInvalidScores: false,
  filterInvalidScoresLeeway: 1,
  enableExperimentalRanks: false,
  disableLightTrails: false,

  hideItemShop: false,
  disableShopHighlighting: false,

  showLead: true,
  showBass: true,
  showDrums: true,
  showVocals: true,
  showProLead: true,
  showProBass: true,

  metadataShowScore: true,
  metadataShowPercentage: true,
  metadataShowPercentile: true,
  metadataShowSeasonAchieved: true,
  metadataShowDifficulty: true,
  metadataShowStars: true,
  metadataShowMaxDistance: true,
});

/* ── Show-key mapping ── */

type ShowKey =
  | 'showLead'
  | 'showBass'
  | 'showDrums'
  | 'showVocals'
  | 'showProLead'
  | 'showProBass';

const SHOW_KEY_FOR_INSTRUMENT: Record<InstrumentKey, ShowKey> = {
  Solo_Guitar: 'showLead',
  Solo_Bass: 'showBass',
  Solo_Drums: 'showDrums',
  Solo_Vocals: 'showVocals',
  Solo_PeripheralGuitar: 'showProLead',
  Solo_PeripheralBass: 'showProBass',
};

export function isInstrumentVisible(settings: AppSettings, key: InstrumentKey): boolean {
  return settings[SHOW_KEY_FOR_INSTRUMENT[key]];
}

export function visibleInstruments(settings: AppSettings): InstrumentKey[] {
  return INSTRUMENT_KEYS.filter(k => isInstrumentVisible(settings, k));
}

/* ── Persistence ── */

const STORAGE_KEY = 'fst:appSettings';

function loadSettings(): AppSettings {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return defaultAppSettings();
    const parsed = JSON.parse(raw);
    const defaults = defaultAppSettings();
    const merged = { ...defaults, ...parsed };
    // Migrate songRowVisualOrder: append new DEFAULT_METADATA_ORDER keys
    if (Array.isArray(merged.songRowVisualOrder)) {
      const missing = DEFAULT_METADATA_ORDER.filter(k => !merged.songRowVisualOrder.includes(k));
      if (missing.length > 0) merged.songRowVisualOrder = [...merged.songRowVisualOrder, ...missing];
    }
    return merged;
  } catch {
    return defaultAppSettings();
  }
}

function saveSettings(settings: AppSettings): void {
  localStorage.setItem(STORAGE_KEY, JSON.stringify(settings));
}

/* ── Context ── */

type SettingsContextValue = {
  settings: AppSettings;
  setSettings: (s: AppSettings) => void;
  updateSettings: (partial: Partial<AppSettings>) => void;
  resetSettings: () => void;
};

const SettingsContext = createContext<SettingsContextValue | null>(null);

export function SettingsProvider({ children }: { children: ReactNode }) {
  const [settings, setSettingsState] = useState<AppSettings>(loadSettings);

  useEffect(() => {
    saveSettings(settings);
  }, [settings]);

  const setSettings = useCallback((s: AppSettings) => {
    setSettingsState(s);
  }, []);

  const updateSettings = useCallback((partial: Partial<AppSettings>) => {
    setSettingsState(prev => ({ ...prev, ...partial }));
  }, []);

  const resetSettings = useCallback(() => {
    setSettingsState(defaultAppSettings());
  }, []);

  const value = useMemo<SettingsContextValue>(() => ({
    settings, setSettings, updateSettings, resetSettings,
  }), [settings, setSettings, updateSettings, resetSettings]);

  return (
    <SettingsContext.Provider value={value}>
      {children}
    </SettingsContext.Provider>
  );
}

export function useSettings(): SettingsContextValue {
  const ctx = useContext(SettingsContext);
  if (!ctx) {
    throw new Error('useSettings must be used within a SettingsProvider');
  }
  return ctx;
}
