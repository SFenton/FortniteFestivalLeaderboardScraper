import React from 'react';
import { StyleSheet, View, ViewProps } from 'react-native';
import {SafeAreaView, type Edge} from 'react-native-safe-area-context';

type Props = {
  children: React.ReactNode;
  withSafeArea?: boolean;
  safeAreaEdges?: Edge[];
} & ViewProps;

export function Screen({children, withSafeArea = true, safeAreaEdges, style, ...rest}: Props) {
  const Container = withSafeArea ? SafeAreaView : View;

  // By default, avoid applying the bottom safe-area inset because the bottom
  // tab bar already accounts for it; otherwise you get a persistent “dead band”
  // above the navbar on iOS/Android.
  const edges: Edge[] = safeAreaEdges ?? ['top', 'left', 'right'];

  return (
    <Container {...(withSafeArea ? {edges} : null)} style={[styles.container, style]} {...rest}>
      {children}
    </Container>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    backgroundColor: 'transparent', // Changed from #1A0830 to allow background to show through
  },
});
