/**
 * Convenience hook combining ShopContext + Settings for consumers.
 * Respects hideItemShop and disableShopHighlighting toggles.
 */
import { useCallback, useMemo } from 'react';
import { useShop } from '../../contexts/ShopContext';
import { useSettings } from '../../contexts/SettingsContext';
import { useFeatureFlags } from '../../contexts/FeatureFlagsContext';

export function useShopState() {
  const { shopSongIds, leavingTomorrowIds, getShopUrl, shopSongs, connected } = useShop();
  const { settings } = useSettings();
  const flags = useFeatureFlags();

  const shopFeatureOff = !flags.shop;
  const effectiveHighlightDisabled = shopFeatureOff || settings.hideItemShop || settings.disableShopHighlighting;

  /** True if the song is in the shop AND highlighting is not disabled. */
  const isShopHighlighted = useCallback((songId: string): boolean => {
    if (effectiveHighlightDisabled) return false;
    return shopSongIds?.has(songId) ?? false;
  }, [shopSongIds, effectiveHighlightDisabled]);

  /** True if the song is in the shop (regardless of highlighting setting). */
  const isInShop = useCallback((songId: string): boolean => {
    return shopSongIds?.has(songId) ?? false;
  }, [shopSongIds]);

  /** True if the song's offer expires tomorrow and highlighting is enabled. */
  const isLeavingTomorrow = useCallback((songId: string): boolean => {
    if (effectiveHighlightDisabled) return false;
    return leavingTomorrowIds?.has(songId) ?? false;
  }, [leavingTomorrowIds, effectiveHighlightDisabled]);

  /** True if shop UI elements should be visible. */
  const isShopVisible = !shopFeatureOff && !settings.hideItemShop;

  /** Filtered shop songs (empty when shop is hidden). */
  const visibleShopSongs = useMemo(() => {
    return isShopVisible ? shopSongs : [];
  }, [isShopVisible, shopSongs]);

  return {
    isShopHighlighted,
    isInShop,
    isLeavingTomorrow,
    isShopVisible,
    getShopUrl,
    shopSongs: visibleShopSongs,
    connected,
  };
}
