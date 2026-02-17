import React from 'react';
import {AppNavigator} from './navigation/AppNavigator';

/**
 * Root component for the local (offline) mode of the app.
 *
 * Renders the full tab navigator with Songs, Suggestions, Statistics,
 * Settings, and Sync screens.  Must be rendered inside <FestivalProvider>
 * and <AuthProvider> from @festival/contexts.
 */
export function LocalApp() {
  return <AppNavigator />;
}
