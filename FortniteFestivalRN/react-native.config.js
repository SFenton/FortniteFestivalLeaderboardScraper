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
  },
};
