import React from 'react';
import {Platform, StyleProp, StyleSheet, View, type ViewProps, ViewStyle} from 'react-native';

type FrostedTint = 'light' | 'dark' | 'default';

const DEFAULT_FALLBACK = Platform.select({
  windows: 'rgba(18,24,38,0.97)',
  default: 'rgba(18,24,38,0.78)',
});

type Props = {
  children?: React.ReactNode;
  style?: StyleProp<ViewStyle>;
  tint?: FrostedTint;
  intensity?: number;
  fallbackColor?: string;
  blurEnabled?: boolean;
} & Omit<ViewProps, 'style' | 'children'>;

export function FrostedSurface(props: Props) {
  const {children, style, fallbackColor = DEFAULT_FALLBACK, ...viewProps} = props;
  return (
    <View {...viewProps} style={[styles.chrome, style, {backgroundColor: fallbackColor}]}>
      {children}
    </View>
  );
}

const styles = StyleSheet.create({
  chrome: {
    borderRadius: 16,
    overflow: 'hidden',
    borderWidth: 1,
    borderColor: '#263244',
  },
});
