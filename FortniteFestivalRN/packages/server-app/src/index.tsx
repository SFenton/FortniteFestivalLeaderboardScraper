import React from 'react';
import {AppNavigator} from './navigation/AppNavigator';

/**
 * Root component for the server-enabled mode of the app.
 *
 * Initially identical to LocalApp. Will diverge as server-specific
 * features land (Opps, Rankings, server-driven navigation, etc.).
 * Must be rendered inside <FestivalProvider> and <AuthProvider>
 * from @festival/contexts.
 */
export function ServerApp() {
  return <AppNavigator />;
}
