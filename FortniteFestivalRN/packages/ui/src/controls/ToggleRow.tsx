/**
 * A reusable settings/filter toggle row with an optional icon, optional
 * description, and a Switch control.
 *
 * Unifies `ToggleRow` / `DescriptorToggleRow` from SettingsScreen and the
 * per-modal `ToggleRow` variants from FilterModal / SuggestionsFilterModal.
 */
import React from 'react';
import {Image, Pressable, StyleSheet, Switch, Text, View} from 'react-native';
import type {ImageSourcePropType, StyleProp, ViewStyle} from 'react-native';
import {Colors, Font, LineHeight, Gap, Opacity} from '../theme';

// ── Props ───────────────────────────────────────────────────────────

export interface ToggleRowProps {
  /** Primary label text. */
  label: string;
  /** Optional secondary description rendered below the label. */
  description?: string;
  /** Instrument or category icon shown to the left of the label. */
  icon?: ImageSourcePropType;
  /** Current toggle value. */
  checked: boolean;
  /** Called when the switch value changes or the row is pressed. */
  onToggle: () => void;
  /** When true the row is greyed-out and non-interactive. */
  disabled?: boolean;
  /** Apply extra top margin (e.g. first row in a section). */
  first?: boolean;
  /** Unused today; kept for symmetry with `first`. */
  last?: boolean;
  /** Extra styles applied to the outer Pressable container. */
  style?: StyleProp<ViewStyle>;
}

// ── Component ───────────────────────────────────────────────────────

export const ToggleRow = React.memo(function ToggleRow(props: ToggleRowProps) {
  const {label, description, icon, checked, onToggle, disabled, first, style} = props;
  const hasDescription = !!description;

  return (
    <Pressable
      onPress={disabled ? undefined : onToggle}
      disabled={disabled}
      style={({pressed}) => [
        styles.row,
        hasDescription && styles.rowAlignStart,
        first && styles.rowFirst,
        pressed && !disabled && styles.rowPressed,
        disabled && styles.rowDisabled,
        style,
      ]}
      accessibilityRole="switch">
      {icon ? (
        <Image source={icon} style={styles.icon} resizeMode="contain" />
      ) : null}

      <View style={styles.labelWrap}>
        <Text style={[styles.label, disabled && styles.textDisabled]}>
          {label}
        </Text>
        {hasDescription ? (
          <Text style={[styles.description, disabled && styles.textDisabled]}>
            {description}
          </Text>
        ) : null}
      </View>

      <View
        style={disabled ? styles.switchDisabled : undefined}
        pointerEvents={disabled ? 'none' : 'auto'}>
        <Switch
          value={checked}
          onValueChange={onToggle}
          trackColor={{false: Colors.switchTrackOff, true: Colors.switchTrackOn}}
          thumbColor={checked ? Colors.textPrimary : Colors.textMuted}
        />
      </View>
    </Pressable>
  );
});

// ── Styles ──────────────────────────────────────────────────────────

const styles = StyleSheet.create({
  row: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    paddingVertical: Gap.md,
    paddingHorizontal: Gap.xl,
  },
  rowAlignStart: {
    alignItems: 'flex-start',
  },
  rowFirst: {
    marginTop: 6,
  },
  rowPressed: {
    opacity: Opacity.pressed,
  },
  rowDisabled: {
    opacity: 0.45,
  },
  icon: {
    width: 36,
    height: 36,
    marginRight: Gap.md,
  },
  labelWrap: {
    flex: 1,
    marginRight: Gap.xl,
  },
  label: {
    color: Colors.textPrimary,
    fontSize: Font.md,
    fontWeight: '700',
  },
  description: {
    color: Colors.textMuted,
    fontSize: Font.sm,
    lineHeight: LineHeight.sm,
    marginTop: Gap.xs,
  },
  textDisabled: {
    color: Colors.textDisabled,
  },
  switchDisabled: {
    opacity: 0.45,
  },
});
