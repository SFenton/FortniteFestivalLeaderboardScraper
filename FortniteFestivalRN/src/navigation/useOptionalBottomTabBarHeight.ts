import {useBottomTabBarHeight} from '@react-navigation/bottom-tabs';

export function useOptionalBottomTabBarHeight(): number {
  try {
    return useBottomTabBarHeight();
  } catch {
    return 0;
  }
}
