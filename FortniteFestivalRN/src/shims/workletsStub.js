/**
 * react-native-worklets stub for Windows.
 *
 * react-native-worklets has no Windows native module.  Metro redirects all
 * `require('react-native-worklets')` calls to this file when bundling for
 * platform === 'windows'.
 *
 * This prevents the "Native part of Worklets doesn't seem to be initialized"
 * crash.  In practice, the reanimatedStub already prevents worklets from being
 * loaded (since reanimated is the only consumer), but this serves as a safety
 * net for any other transitive import.
 */

'use strict';

module.exports = {
  __esModule: true,
  // NativeWorklets would normally access the JSI-based native module.
  // Provide a no-op stand-in.
  NativeWorklets: {
    installRuntime: () => {},
  },
};
