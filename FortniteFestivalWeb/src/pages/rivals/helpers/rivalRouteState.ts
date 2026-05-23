import type { AppSettings } from '../../../contexts/SettingsContext';
import { deriveRivalScopeFromSettings, deriveRivalScopesFromSettings, getEnabledInstruments } from './comboUtils';

export const RIVAL_COMBO_SCOPE_SETTINGS = 'settings';

export type RivalRouteState = {
  combo?: string;
  comboScope?: typeof RIVAL_COMBO_SCOPE_SETTINGS;
  rivalName?: string;
  allowLiveFallback?: boolean;
  source?: 'song' | 'leaderboard';
  instrument?: string;
  rankBy?: string;
};

export function resolveRivalCombo(state: RivalRouteState | null, settings: AppSettings): string {
  return resolveRivalCombos(state, settings)[0] ?? 'Solo_Guitar';
}

export function resolveRivalCombos(state: RivalRouteState | null, settings: AppSettings): string[] {
  if (state?.comboScope === RIVAL_COMBO_SCOPE_SETTINGS) {
    const scopes = deriveRivalScopesFromSettings(settings);
    return scopes.length > 0 ? scopes : ['Solo_Guitar'];
  }

  return [state?.combo ?? deriveRivalScopeFromSettings(settings) ?? getEnabledInstruments(settings)[0] ?? 'Solo_Guitar'];
}

export function rivalComboStateForNavigation(state: RivalRouteState | null, combo: string | undefined): Pick<RivalRouteState, 'combo' | 'comboScope' | 'allowLiveFallback'> {
  const liveFallbackState = state?.allowLiveFallback ? { allowLiveFallback: true } : {};

  if (state?.comboScope === RIVAL_COMBO_SCOPE_SETTINGS) {
    return { comboScope: RIVAL_COMBO_SCOPE_SETTINGS, ...liveFallbackState };
  }

  return combo ? { combo, ...liveFallbackState } : liveFallbackState;
}