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

/**
 * Error boundary that silently falls back to a plain View when BlurView
 * crashes during native view creation (e.g. null activity context on some
 * Android devices / lifecycle states).
 */
class BlurErrorBoundary extends React.Component<
  {children: React.ReactNode; fallback: React.ReactNode},
  {hasError: boolean}
> {
  state = {hasError: false};
  static getDerivedStateFromError() {
    return {hasError: true};
  }
  render() {
    return this.state.hasError ? this.props.fallback : this.props.children;
  }
}

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

  const plain = (
    <View {...viewProps} style={[styles.chrome, style, {backgroundColor: fallbackColor}]}>
      {children}
    </View>
  );

  return (
    <BlurErrorBoundary fallback={plain}>
      <View {...viewProps} style={[styles.chrome, style]}>
        <BlurView
          style={StyleSheet.absoluteFillObject}
          blurType={tint === 'default' ? 'light' : tint}
          blurAmount={intensity}
          reducedTransparencyFallbackColor={fallbackColor}
        />
        <View style={styles.content}>{children}</View>
      </View>
    </BlurErrorBoundary>
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
