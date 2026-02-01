import React from 'react';
import {StyleProp, StyleSheet, View, type ViewProps, ViewStyle} from 'react-native';
import {BlurView} from '@react-native-community/blur';

type FrostedTint = 'light' | 'dark' | 'default';

type Props = {
  children?: React.ReactNode;
  style?: StyleProp<ViewStyle>;
  tint?: FrostedTint;
  intensity?: number;
  fallbackColor?: string;
  blurEnabled?: boolean;
} & Omit<ViewProps, 'style' | 'children'>;

export function FrostedSurface(props: Props) {
  const {
    children,
    style,
    tint = 'dark',
    intensity = 18,
    fallbackColor = 'rgba(18,24,38,0.78)',
    blurEnabled = true,
    ...viewProps
  } = props;

  if (!blurEnabled) {
    return (
      <View {...viewProps} style={[styles.chrome, style, {backgroundColor: fallbackColor}]}> 
        {children}
      </View>
    );
  }

  return (
    <View {...viewProps} style={[styles.chrome, style]}>
      <BlurView
        style={StyleSheet.absoluteFillObject}
        blurType={tint === 'default' ? 'light' : tint}
        blurAmount={intensity}
        reducedTransparencyFallbackColor={fallbackColor}
      />
      <View style={styles.content}>{children}</View>
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
  content: {
    flexGrow: 1,
  },
});
