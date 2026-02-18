import React from 'react';
import {ServiceSyncProvider} from '@festival/contexts';
import {AppNavigator} from './navigation/AppNavigator';

/**
 * Root component for the server-enabled mode of the app.
 *
 * Wraps navigation in <ServiceSyncProvider> which manages the WebSocket
 * connection to the FST service and handles personal DB download/sync.
 *
 * Must be rendered inside <FestivalProvider> and <AuthProvider>
 * from @festival/contexts.
 */
export function ServerApp() {
  return (
    <ServiceSyncProvider>
      <AppNavigator />
    </ServiceSyncProvider>
  );
}
