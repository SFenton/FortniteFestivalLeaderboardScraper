/**
 * React Native CLI configuration.
 *
 * We selectively disable native autolinking for packages that don't currently
 * build against the WinUI3-based RNW app template.
 */
module.exports = {
  assets: ["./node_modules/react-native-vector-icons/Fonts"],
  dependencies: {
    'react-native-screens': {
      platforms: {
        windows: null,
      },
    },
    // The native Windows module targets UWP XAML (Windows.UI.Xaml) which is
    // incompatible with RN-Windows 0.81 (WinUI 3).  We use an SVG-based JS
    // shim instead — see metro.config.js resolveRequest and src/shims/.
    'react-native-linear-gradient': {
      platforms: {
        windows: null,
      },
    },
    // react-native-fs has no Windows native module; accessing its constants
    // (e.g. RNFSFileTypeRegular) at import time crashes the bundle.
    'react-native-fs': {
      platforms: {
        windows: null,
      },
    },
    // react-native-reanimated / react-native-worklets have no Windows
    // native modules.  Importing them crashes with "Native part of
    // Worklets doesn't seem to be initialized."
    'react-native-reanimated': {
      platforms: {
        windows: null,
      },
    },
    'react-native-worklets': {
      platforms: {
        windows: null,
      },
    },
    // react-native-gesture-handler has no Windows native module.
    // We redirect to a JS-only stub in metro.config.js.
    'react-native-gesture-handler': {
      platforms: {
        windows: null,
      },
    },
    // react-native-draggable-flatlist depends on both reanimated and
    // gesture-handler at the native level; disable autolinking on Windows.
    'react-native-draggable-flatlist': {
      platforms: {
        windows: null,
      },
    },
    // react-native-vector-icons has no Windows native module.  The Icon
    // component itself renders via <Text> + font glyphs and doesn't need
    // native code, but autolinking would fail without a windows/ project.
    'react-native-vector-icons': {
      platforms: {
        windows: null,
      },
    },
    // @react-native-masked-view/masked-view has no Windows native
    // implementation.  We redirect to a JS-only stub in metro.config.js.
    '@react-native-masked-view/masked-view': {
      platforms: {
        windows: null,
      },
    },
    // react-native-svg has no Windows native module for the WinUI 3
    // Composition renderer.  We redirect to a JS-only stub.
    'react-native-svg': {
      platforms: {
        windows: null,
      },
    },
  },
};
