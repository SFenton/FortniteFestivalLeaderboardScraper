import React from 'react';
import {StyleProp, StyleSheet, View, type ViewProps, ViewStyle} from 'react-native';
import {BlurView} from '@react-native-community/blur';
import {LiquidGlassView, isLiquidGlassSupported} from '@callstack/liquid-glass';
import {useFestival} from '../app/festival/FestivalContext';

type FrostedTint = 'light' | 'dark' | 'default';

type Props = {
  children?: React.ReactNode;
  style?: StyleProp<ViewStyle>;
  tint?: FrostedTint;
  intensity?: number;
  fallbackColor?: string;
} & Omit<ViewProps, 'style' | 'children'>;

export function FrostedSurface(props: Props) {
  const {
    children,
    style,
    tint = 'dark',
    intensity = 18,
    fallbackColor = 'rgba(18,24,38,0.78)',
    ...viewProps
  } = props;

  const {state} = useFestival();
  const {iosBlurEnabled, iosLiquidGlassEnabled, iosLiquidGlassStyle} = state.settings;

  // Use LiquidGlassView on iOS 26+ when enabled (independent of blur setting)
  if (isLiquidGlassSupported && iosLiquidGlassEnabled) {
    return (
      <LiquidGlassView
        {...viewProps}
        style={[styles.liquidGlass, style]}
        effect={iosLiquidGlassStyle}
        colorScheme={tint === 'light' ? 'light' : tint === 'dark' ? 'dark' : 'system'}
      >
        <View style={styles.content}>{children}</View>
      </LiquidGlassView>
    );
  }

  if (!iosBlurEnabled) {
    return (
      <View {...viewProps} style={[styles.chrome, style, {backgroundColor: fallbackColor}]}>
        {children}
      </View>
    );
  }

  // Fallback to BlurView for older iOS versions
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
  liquidGlass: {
    borderRadius: 16,
    overflow: 'hidden',
  },
  content: {
    flexGrow: 1,
  },
});
