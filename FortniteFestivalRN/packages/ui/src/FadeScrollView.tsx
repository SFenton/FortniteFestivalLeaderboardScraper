import React from 'react';
import {StyleSheet, View} from 'react-native';
import type {StyleProp, ViewStyle} from 'react-native';
import MaskedView from '@react-native-masked-view/masked-view';
import LinearGradient from 'react-native-linear-gradient';

/**
 * Wraps children in a `MaskedView` with top/bottom fade gradients so that
 * content fades in from the top and fades out at the bottom.  Drop this around
 * any `ScrollView` / `FlatList` to get the effect with zero boilerplate.
 *
 * ```tsx
 * <FadeScrollView>
 *   <FlatList ... />
 * </FadeScrollView>
 * ```
 */
export function FadeScrollView({
  style,
  gradientHeight = 32,
  children,
}: {
  /** Style applied to the outer MaskedView container (defaults to `flex: 1`). */
  style?: StyleProp<ViewStyle>;
  /** Height of each fade gradient edge in points. Default `32`. */
  gradientHeight?: number;
  children: React.ReactNode;
}) {
  const gradientStyle = {height: gradientHeight};

  return (
    <MaskedView
      style={[styles.container, style]}
      maskElement={
        <View style={styles.maskContainer}>
          <LinearGradient
            colors={['transparent', 'black']}
            style={gradientStyle}
          />
          <View style={styles.maskOpaque} />
          <LinearGradient
            colors={['black', 'transparent']}
            style={gradientStyle}
          />
        </View>
      }>
      {children}
    </MaskedView>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
  },
  maskContainer: {
    flex: 1,
  },
  maskOpaque: {
    flex: 1,
    backgroundColor: '#000000',
  },
});
