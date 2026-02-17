import React from 'react';
import {StyleSheet} from 'react-native';
import {AppShell} from '@festival/app-screens';
import type {FlyoutConfig} from '@festival/app-screens';

const flyoutConfig: FlyoutConfig = {
  winRootBackground: '#1A0830',
  flyoutBackground: '#1A0830',
  flyoutBorderColor: '#1E2A3A',
  flyoutBorderWidth: StyleSheet.hairlineWidth,
  showFlyoutHeader: true,
};

export function AppNavigator() {
  return <AppShell flyoutConfig={flyoutConfig} />;
}
