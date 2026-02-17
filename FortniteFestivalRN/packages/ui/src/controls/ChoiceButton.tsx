/**
 * A selectable "chip" button used for single-choice option groups.
 *
 * Renders a frosted-glass pill with selected/unselected styling.
 * Typically placed inside a `<View style={{flexDirection: 'row', gap: 8}}>`.
 */
import React from 'react';
import {Pressable, StyleSheet, Text} from 'react-native';
import type {StyleProp, ViewStyle} from 'react-native';
import {FrostedSurface} from '../FrostedSurface';
import {Colors, Radius, Font, Gap, Opacity} from '../theme';

// ── Props ───────────────────────────────────────────────────────────

export interface ChoiceButtonProps {
  /** Display label. */
  label: string;
  /** Whether this option is currently active. */
  selected: boolean;
  /** Called when the button is pressed. */
  onPress: () => void;
  /** Extra styles applied to the outer Pressable. */
  style?: StyleProp<ViewStyle>;
}

// ── Component ───────────────────────────────────────────────────────

export const ChoiceButton = React.memo(function ChoiceButton(props: ChoiceButtonProps) {
  const {label, selected, onPress, style} = props;

  return (
    <Pressable
      onPress={onPress}
      style={({pressed}) => [{flex: 1}, pressed && styles.pressed, style]}>
      <FrostedSurface
        style={[styles.chip, selected && styles.chipSelected]}
        tint="dark"
        intensity={12}>
        <Text style={[styles.text, selected && styles.textSelected]}>
          {label}
        </Text>
      </FrostedSurface>
    </Pressable>
  );
});

// ── Styles ──────────────────────────────────────────────────────────

const styles = StyleSheet.create({
  pressed: {
    opacity: Opacity.pressed,
  },
  chip: {
    flex: 1,
    paddingHorizontal: Gap.xl,
    paddingVertical: Gap.md,
    borderRadius: Radius.md,
    borderWidth: 1,
    borderColor: Colors.borderPrimary,
    alignItems: 'center',
  },
  chipSelected: {
    borderColor: Colors.accentBlue,
    backgroundColor: Colors.chipSelectedBgSubtle,
  },
  text: {
    color: Colors.textSecondary,
    fontSize: Font.sm,
    fontWeight: '700',
  },
  textSelected: {
    color: Colors.textPrimary,
  },
});
