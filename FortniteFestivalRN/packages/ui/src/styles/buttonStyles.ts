/**
 * Shared button styles extracted from `SettingsScreen`.
 *
 * All colour-variant buttons share the same structural base. The secondary,
 * purple, and destructive variants only change `backgroundColor` and
 * `borderColor`.
 */
import {StyleSheet} from 'react-native';
import {Colors, Radius, Gap, Opacity} from '../theme';

export const buttonStyles = StyleSheet.create({
  /** Primary action button (blue). */
  button: {
    backgroundColor: Colors.chipSelectedBg,
    paddingVertical: Gap.xl,
    paddingHorizontal: Gap.xl,
    borderRadius: Radius.md,
    borderWidth: 1,
    borderColor: Colors.chipSelectedBg,
    alignItems: 'center',
    justifyContent: 'center',
    flexGrow: 1,
  },
  /** Secondary / outline button. */
  buttonSecondary: {
    backgroundColor: Colors.cardOverlay,
    paddingVertical: Gap.xl,
    paddingHorizontal: Gap.xl,
    borderRadius: Radius.md,
    borderWidth: 1,
    borderColor: Colors.borderPrimary,
    alignItems: 'center',
    justifyContent: 'center',
  },
  /** Purple / brand accent button. */
  buttonPurple: {
    backgroundColor: Colors.purpleButtonBg,
    paddingVertical: Gap.xl,
    paddingHorizontal: Gap.xl,
    borderRadius: Radius.md,
    borderWidth: 1,
    borderColor: Colors.purpleButtonBg,
    alignItems: 'center',
    justifyContent: 'center',
  },
  /** Destructive / danger button (red). */
  buttonDestructive: {
    backgroundColor: Colors.dangerBg,
    paddingVertical: Gap.xl,
    paddingHorizontal: Gap.xl,
    borderRadius: Radius.md,
    borderWidth: 1,
    borderColor: Colors.dangerBg,
    alignItems: 'center',
    justifyContent: 'center',
  },
  /** Pressed state overlay. */
  buttonPressed: {
    opacity: Opacity.pressed,
  },
  /** Disabled state. */
  buttonDisabled: {
    opacity: Opacity.disabled,
  },
  /** Button label text. */
  buttonText: {
    color: Colors.textPrimary,
    fontWeight: '800',
  },
});
