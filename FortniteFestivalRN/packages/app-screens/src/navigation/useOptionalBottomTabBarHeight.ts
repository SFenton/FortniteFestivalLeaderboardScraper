import {useContext} from 'react';
import {Platform} from 'react-native';
import {BottomTabBarHeightContext as JsTabBarHeightContext} from '@react-navigation/bottom-tabs';
import {useSafeAreaInsets} from 'react-native-safe-area-context';

/**
 * Standard UITabBar height in points (49pt).  react-native-safe-area-context
 * measures at the window level so it only reports the home-indicator inset
 * (34pt on notched iPhones).  The native UITabBarController's safe-area
 * contribution is NOT reflected, so we add the bar height manually.
 */
const IOS_TAB_BAR_HEIGHT = 49;

export type TabBarLayout = {
  /** Total overlap height of the tab bar (use for paddingBottom & scrollIndicatorInsets). */
  height: number;
  /**
   * Bottom margin to apply to scroll views.
   *
   * - **JS tabs**: `-height` — the scene stops at the tab bar, so negative margin
   *   extends the list into the translucent bar area.
   * - **Native iOS tabs**: `0` — the scene already extends behind the bar, so no
   *   margin adjustment is needed.
   */
  marginBottom: number;
};

/**
 * Returns layout information about the bottom tab bar so scroll content can
 * be padded/margined to avoid being hidden behind it.
 */
export function useTabBarLayout(): TabBarLayout {
  const jsHeight = useContext(JsTabBarHeightContext);
  const insets = useSafeAreaInsets();

  // JS tabs (Android / MobileTabs): scene stops at tab bar top.
  // Use negative margin so the list extends into the translucent tab bar.
  if (jsHeight !== undefined) {
    return {height: jsHeight, marginBottom: -jsHeight};
  }

  // Native iOS tabs (react-native-screens Tabs.Host / UITabBarController):
  // The scene content extends behind the translucent tab bar, so the list
  // already visually overlaps the bar — no negative margin required.
  if (Platform.OS === 'ios') {
    const h = IOS_TAB_BAR_HEIGHT + insets.bottom;
    return {height: h, marginBottom: 0};
  }

  // Windows / no tab navigator.
  return {height: 0, marginBottom: 0};
}

/** @deprecated Use `useTabBarLayout()` instead. */
export function useOptionalBottomTabBarHeight(): number {
  return useTabBarLayout().height;
}
