import React from 'react';
import {Pressable, StyleSheet, Text} from 'react-native';
import {Colors, Radius, Font, Gap} from './theme';

export function HamburgerButton({onPress}: {onPress: () => void}) {
  return (
    <Pressable
      onPress={onPress}
      hitSlop={12}
      style={({pressed}) => [styles.hamburger, pressed && styles.hamburgerPressed]}
      accessibilityRole="button"
      accessibilityLabel="Open navigation menu"
    >
      <Text style={styles.hamburgerText}>≡</Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  hamburger: {
    paddingRight: Gap.lg,
    paddingVertical: 6,
    borderRadius: Radius.xs,
  },
  hamburgerPressed: {
    backgroundColor: '#162133',
  },
  hamburgerText: {
    color: Colors.textPrimary,
    fontSize: Font.title,
    lineHeight: Font.title,
  },
});
