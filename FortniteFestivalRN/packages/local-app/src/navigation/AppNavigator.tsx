import React from 'react';
import {AppShell} from '@festival/app-screens';
import type {FlyoutConfig} from '@festival/app-screens';

const flyoutConfig: FlyoutConfig = {
  winRootBackground: 'transparent',
  flyoutBackground: 'rgba(18,24,38,0.97)',
  flyoutBorderColor: '#263244',
  flyoutBorderWidth: 1,
  showFlyoutHeader: false,
};

export function AppNavigator() {
  return <AppShell flyoutConfig={flyoutConfig} />;
}
