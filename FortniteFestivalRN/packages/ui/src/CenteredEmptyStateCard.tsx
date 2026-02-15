import React from 'react';
import {StyleProp, StyleSheet, Text, View, ViewStyle} from 'react-native';

import {FrostedSurface} from './FrostedSurface';

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
    borderRadius: 12,
    paddingVertical: 24,
    paddingHorizontal: 16,
    gap: 12,
  },
  title: {
    color: '#FFFFFF',
    fontSize: 16,
    fontWeight: '700',
    textAlign: 'center',
  },
  body: {
    color: '#D7DEE8',
    fontSize: 13,
    opacity: 0.85,
    textAlign: 'center',
    lineHeight: 18,
    marginTop: 14,
  },
});
