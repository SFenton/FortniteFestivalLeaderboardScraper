import {Platform, useWindowDimensions} from 'react-native';

/**
 * Returns `true` when the device should display a 2-column card grid instead
 * of a vertical list.
 *
 * Detection logic (no extra packages needed):
 * - **iOS** – `Info.plist` locks phones to portrait, so landscape can only
 *   mean iPad.  We simply check `width > height`.
 * - **Android** – `Math.min(width, height) >= 600` mirrors Android's own
 *   `sw600dp` resource qualifier (the standard tablet-class breakpoint).
 *   Because `useWindowDimensions` updates reactively on fold / unfold, this
 *   naturally catches unfolded foldables too while excluding phones (whose
 *   min dimension is ~360-430 even in landscape).
 *
 * @param effectiveWidth  Optional override for the horizontal dimension
 *   (e.g. a measured container width).  Falls back to
 *   `useWindowDimensions().width` when omitted or ≤ 0.
 */
export function useCardGrid(effectiveWidth?: number): boolean {
  const {width, height} = useWindowDimensions();

  const w = effectiveWidth != null && effectiveWidth > 0 ? effectiveWidth : width;

  // Windows always renders at minimum 720p (1280×720), so it is always in
  // landscape / dual-column mode.  No need to check a breakpoint.
  if (Platform.OS === 'windows') {
    return true;
  }

  if (Platform.OS === 'ios') {
    return w > height;
  }

  if (Platform.OS === 'android') {
    return Math.min(w, height) >= 600;
  }

  // Non-mobile platforms (Windows, web, etc.) – use a width breakpoint so the
  // card grid kicks in at the same point where mobile tablets would show it.
  return w >= 720;
}
