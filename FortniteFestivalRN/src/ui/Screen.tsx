import React from 'react';
import { StyleSheet, View, ViewProps } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';

type Props = {
  children: React.ReactNode;
  withSafeArea?: boolean;
} & ViewProps;

export function Screen({ children, withSafeArea = true, style, ...rest }: Props) {
  const Container = withSafeArea ? SafeAreaView : View;

  return (
    <Container style={[styles.container, style]} {...rest}>
      {children}
    </Container>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    backgroundColor: '#1A0830',
  },
});
