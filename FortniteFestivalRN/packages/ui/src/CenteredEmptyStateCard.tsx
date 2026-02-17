import React from 'react';
import {StyleProp, StyleSheet, Text, View, ViewStyle} from 'react-native';

import {FrostedSurface} from './FrostedSurface';
import {Colors, Radius, Font, LineHeight, Gap, Opacity} from './theme';

type Props = {
  title: string;
  body?: string;
  children?: React.ReactNode;
  style?: StyleProp<ViewStyle>;
  cardStyle?: StyleProp<ViewStyle>;
  tint?: 'light' | 'dark' | 'default';
  intensity?: number;
  maxWidth?: number;
};

export function CenteredEmptyStateCard(props: Props) {
  const {
    title,
    body,
    children,
    style,
    cardStyle,
    tint = 'dark',
    intensity = 18,
    maxWidth = 520,
  } = props;

  return (
    <View style={[styles.wrap, style]}>
      <FrostedSurface style={[styles.card, {maxWidth}, cardStyle]} tint={tint} intensity={intensity}>
        <Text style={styles.title}>{title}</Text>
        {body ? <Text style={styles.body}>{body}</Text> : null}
        {children}
      </FrostedSurface>
    </View>
  );
}

const styles = StyleSheet.create({
  wrap: {
    flex: 1,
    alignItems: 'center',
    justifyContent: 'center',
  },
  card: {
    width: '100%',
    borderRadius: Radius.md,
    paddingVertical: Gap.section,
    paddingHorizontal: Font.lg,
    gap: Gap.xl,
  },
  title: {
    color: Colors.textPrimary,
    fontSize: Font.lg,
    fontWeight: '700',
    textAlign: 'center',
  },
  body: {
    color: Colors.textSecondary,
    fontSize: 13,
    opacity: Opacity.pressed,
    textAlign: 'center',
    lineHeight: LineHeight.md,
    marginTop: 14,
  },
});
