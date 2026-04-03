import React from 'react';
import {AppNavigator} from './navigation/AppNavigator';

/**
 * Root component for the server-enabled mode of the app.
 *
 * Must be rendered inside <FestivalProvider> and <AuthProvider>
 * from @festival/contexts.
 */
export function ServerApp() {
  return <AppNavigator />;
}
