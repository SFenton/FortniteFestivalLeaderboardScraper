import {
  createContext,
  useContext,
  useReducer,
  useCallback,
  useEffect,
  useMemo,
  type ReactNode,
} from 'react';
import type { ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { INSTRUMENT_KEYS } from '@festival/core/api/serverTypes';
import { DEFAULT_METADATA_ORDER } from '../utils/songSettings';
import { DEFAULT_COLUMN_ORDER } from '../pages/songinfo/components/path/PathDataTable';
import type { ColumnKey } from '../pages/songinfo/components/path/PathDataTable';
import { isSearchTarget, type SearchTarget } from '../types/search';

/* ── Settings shape ── */

export type AppSettings = {
  /* App settings */
  songsHideInstrumentIcons: boolean;
  songRowVisualOrderEnabled: boolean;
  songRowVisualOrder: string[];
  pathColumnOrder: ColumnKey[];
  pathDefaultView: 'image' | 'text';
  pathUnavailableWarningDismissed: boolean;
  filterInvalidScores: boolean;
  filterInvalidScoresLeeway: number;
  enableExperimentalRanks: boolean;
  disableLightTrails: boolean;
  showButtonsInHeaderMobile: boolean;
  defaultSearchTarget: SearchTarget;

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
  showPeripheralVocals: boolean;
  showPeripheralCymbals: boolean;
  showPeripheralDrums: boolean;

  /* Show instrument metadata */
  metadataShowScore: boolean;
  metadataShowPercentage: boolean;
  metadataShowPercentile: boolean;
  metadataShowSeasonAchieved: boolean;
  metadataShowIntensity: boolean;
  metadataShowGameDifficulty: boolean;
  metadataShowStars: boolean;
  metadataShowLastPlayed: boolean;
};

export const defaultAppSettings = (): AppSettings => ({
  songsHideInstrumentIcons: false,
  songRowVisualOrderEnabled: false,
  songRowVisualOrder: [...DEFAULT_METADATA_ORDER],
  pathColumnOrder: [...DEFAULT_COLUMN_ORDER],
  pathDefaultView: 'image',
  pathUnavailableWarningDismissed: false,
  filterInvalidScores: false,
  filterInvalidScoresLeeway: 1,
  enableExperimentalRanks: false,
  disableLightTrails: false,
  showButtonsInHeaderMobile: true,
  defaultSearchTarget: 'songs',

  hideItemShop: false,
  disableShopHighlighting: false,

  showLead: true,
  showBass: true,
  showDrums: true,
  showVocals: true,
  showProLead: true,
  showProBass: true,
  showPeripheralVocals: true,
  showPeripheralCymbals: true,
  showPeripheralDrums: true,

  metadataShowScore: true,
  metadataShowPercentage: true,
  metadataShowPercentile: true,
  metadataShowSeasonAchieved: true,
  metadataShowIntensity: true,
  metadataShowGameDifficulty: true,
  metadataShowStars: true,
  metadataShowLastPlayed: true,
});

/* ── Show-key mapping ── */

type ShowKey =
  | 'showLead'
  | 'showBass'
  | 'showDrums'
  | 'showVocals'
  | 'showProLead'
  | 'showProBass'
  | 'showPeripheralVocals'
  | 'showPeripheralCymbals'
  | 'showPeripheralDrums';

const SHOW_KEY_FOR_INSTRUMENT: Record<InstrumentKey, ShowKey> = {
  Solo_Guitar: 'showLead',
  Solo_Bass: 'showBass',
  Solo_Drums: 'showDrums',
  Solo_Vocals: 'showVocals',
  Solo_PeripheralGuitar: 'showProLead',
  Solo_PeripheralBass: 'showProBass',
  Solo_PeripheralVocals: 'showPeripheralVocals',
  Solo_PeripheralCymbals: 'showPeripheralCymbals',
  Solo_PeripheralDrums: 'showPeripheralDrums',
};

export const PATH_UNAVAILABLE_INSTRUMENTS: readonly InstrumentKey[] = [
  'Solo_PeripheralVocals',
  'Solo_PeripheralDrums',
  'Solo_PeripheralCymbals',
];

const PATH_UNAVAILABLE_INSTRUMENT_SET = new Set<InstrumentKey>(PATH_UNAVAILABLE_INSTRUMENTS);

export function isInstrumentVisible(settings: AppSettings, key: InstrumentKey): boolean {
  return settings[SHOW_KEY_FOR_INSTRUMENT[key]];
}

export function visibleInstruments(settings: AppSettings): InstrumentKey[] {
  return INSTRUMENT_KEYS.filter(k => isInstrumentVisible(settings, k));
}

export function visiblePathInstruments(settings: AppSettings): InstrumentKey[] {
  return visibleInstruments(settings).filter(key => !PATH_UNAVAILABLE_INSTRUMENT_SET.has(key));
}

export function enabledUnavailablePathInstruments(settings: AppSettings): InstrumentKey[] {
  return PATH_UNAVAILABLE_INSTRUMENTS.filter(key => isInstrumentVisible(settings, key));
}

export function hasUnavailablePathInstrumentsEnabled(settings: AppSettings): boolean {
  return enabledUnavailablePathInstruments(settings).length > 0;
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
    // Migrate songRowVisualOrder: strip keys not in DEFAULT_METADATA_ORDER, append new keys
    if (Array.isArray(merged.songRowVisualOrder)) {
      const allowed = new Set(DEFAULT_METADATA_ORDER);
      merged.songRowVisualOrder = merged.songRowVisualOrder.filter((k: string) => allowed.has(k));
      const missing = DEFAULT_METADATA_ORDER.filter(k => !merged.songRowVisualOrder.includes(k));
      if (missing.length > 0) merged.songRowVisualOrder = [...merged.songRowVisualOrder, ...missing];
    }
    // Migrate pathColumnOrder: strip invalid keys, append new keys
    if (Array.isArray(merged.pathColumnOrder)) {
      const allowedCols = new Set<string>(DEFAULT_COLUMN_ORDER);
      merged.pathColumnOrder = merged.pathColumnOrder.filter((k: string) => allowedCols.has(k));
      const missingCols = DEFAULT_COLUMN_ORDER.filter(k => !merged.pathColumnOrder.includes(k));
      if (missingCols.length > 0) merged.pathColumnOrder = [...merged.pathColumnOrder, ...missingCols];
    }
    // Strip removed settings keys
    delete (merged as Record<string, unknown>).metadataShowMaxDistance;
    // Migrate metadataShowDifficulty → metadataShowIntensity (one-time rename)
    if ('metadataShowDifficulty' in parsed && !('metadataShowIntensity' in parsed)) {
      merged.metadataShowIntensity = (parsed as Record<string, unknown>).metadataShowDifficulty;
    }
    delete (merged as Record<string, unknown>).metadataShowDifficulty;
    if (!isSearchTarget(merged.defaultSearchTarget)) {
      merged.defaultSearchTarget = defaults.defaultSearchTarget;
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
  pendingMobileHeaderTransitionToken: number | null;
  consumeMobileHeaderTransitionToken: (token: number) => void;
};

interface SettingsState {
  settings: AppSettings;
  pendingMobileHeaderTransitionToken: number | null;
  nextMobileHeaderTransitionToken: number;
}

type SettingsAction =
  | { type: 'set-settings'; settings: AppSettings }
  | { type: 'update-settings'; partial: Partial<AppSettings> }
  | { type: 'reset-settings' }
  | { type: 'consume-mobile-header-transition'; token: number };

function applySettings(state: SettingsState, nextSettings: AppSettings): SettingsState {
  if (state.settings.showButtonsInHeaderMobile === nextSettings.showButtonsInHeaderMobile) {
    return { ...state, settings: nextSettings };
  }

  const nextToken = state.nextMobileHeaderTransitionToken + 1;
  return {
    settings: nextSettings,
    pendingMobileHeaderTransitionToken: nextToken,
    nextMobileHeaderTransitionToken: nextToken,
  };
}

function settingsReducer(state: SettingsState, action: SettingsAction): SettingsState {
  switch (action.type) {
    case 'set-settings':
      return applySettings(state, action.settings);
    case 'update-settings':
      return applySettings(state, { ...state.settings, ...action.partial });
    case 'reset-settings':
      return applySettings(state, defaultAppSettings());
    case 'consume-mobile-header-transition':
      return state.pendingMobileHeaderTransitionToken === action.token
        ? { ...state, pendingMobileHeaderTransitionToken: null }
        : state;
    default:
      return state;
  }
}

const SettingsContext = createContext<SettingsContextValue | null>(null);

export function SettingsProvider({ children }: { children: ReactNode }) {
  const [state, dispatch] = useReducer(settingsReducer, undefined, () => ({
    settings: loadSettings(),
    pendingMobileHeaderTransitionToken: null,
    nextMobileHeaderTransitionToken: 0,
  }));

  useEffect(() => {
    saveSettings(state.settings);
  }, [state.settings]);

  const setSettings = useCallback((s: AppSettings) => {
    dispatch({ type: 'set-settings', settings: s });
  }, []);

  const updateSettings = useCallback((partial: Partial<AppSettings>) => {
    dispatch({ type: 'update-settings', partial });
  }, []);

  const resetSettings = useCallback(() => {
    dispatch({ type: 'reset-settings' });
  }, []);

  const consumeMobileHeaderTransitionToken = useCallback((token: number) => {
    dispatch({ type: 'consume-mobile-header-transition', token });
  }, []);

  const value = useMemo<SettingsContextValue>(() => ({
    settings: state.settings,
    setSettings,
    updateSettings,
    resetSettings,
    pendingMobileHeaderTransitionToken: state.pendingMobileHeaderTransitionToken,
    consumeMobileHeaderTransitionToken,
  }), [state.settings, state.pendingMobileHeaderTransitionToken, setSettings, updateSettings, resetSettings, consumeMobileHeaderTransitionToken]);

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
