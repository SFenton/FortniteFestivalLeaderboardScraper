const { getDefaultConfig, mergeConfig } = require('@react-native/metro-config');

const fs = require('fs');
const path = require('path');
const rnwPath = fs.realpathSync(
  path.resolve(require.resolve('react-native-windows/package.json'), '..'),
);
const rnPath = fs.realpathSync(
  path.resolve(require.resolve('react-native/package.json'), '..'),
);

// Workspace packages that Metro needs to watch and resolve
const corePackagePath = path.resolve(__dirname, 'packages/core');
const uiPackagePath = path.resolve(__dirname, 'packages/ui');
const contextsPackagePath = path.resolve(__dirname, 'packages/contexts');
const appScreensPackagePath = path.resolve(__dirname, 'packages/app-screens');
const localAppPackagePath = path.resolve(__dirname, 'packages/local-app');
const serverAppPackagePath = path.resolve(__dirname, 'packages/server-app');

//

/**
 * Metro configuration
 * https://facebook.github.io/metro/docs/configuration
 *
 * @type {import('metro-config').MetroConfig}
 */

const config = {
  // Watch workspace packages so Metro picks up changes in real-time
  watchFolders: [corePackagePath, uiPackagePath, contextsPackagePath, appScreensPackagePath, localAppPackagePath, serverAppPackagePath],
  //
  resolver: {
    // Register 'windows' so Metro resolves .windows.js platform overrides
    // shipped by react-native-windows (e.g. ReactDevToolsSettingsManager).
    platforms: ['android', 'ios', 'windows'],
    // Watchman is great when installed/healthy, but when its socket isn't available Metro can hang
    // for minutes trying to `watch-project`. Disable it so `yarn start` is reliable on fresh setups.
    useWatchman: false,
    blockList: [
      // This stops "npx @react-native-community/cli run-windows" from causing the metro server to crash if its already running
      new RegExp(
        `${path.resolve(__dirname, 'windows').replace(/[/\\]/g, '/')}.*`,
      ),
      // This prevents "npx @react-native-community/cli run-windows" from hitting: EBUSY: resource busy or locked, open msbuild.ProjectImports.zip or other files produced by msbuild
      new RegExp(`${rnwPath}/build/.*`),
      new RegExp(`${rnwPath}/target/.*`),
      /.*\.ProjectImports\.zip/,
    ],
    // Providing a custom resolveRequest replaces the CLI's built-in
    // reactNativePlatformResolver (installed via setFrameworkDefaults).
    // We must replicate its react-native → react-native-windows
    // redirect here so that all files load from react-native-windows
    // when bundling for the windows platform.  Without this, relative
    // requires inside react-native (e.g. ReactDevToolsSettingsManager)
    // fail because .windows.js overrides only exist in react-native-windows.
    //
    // See: https://github.com/react-native-community/cli/pull/1115
    //      https://github.com/facebook/react-native/blob/main/packages/community-cli-plugin/src/utils/metroPlatformResolver.js
    resolveRequest: (context, moduleName, platform) => {
      // ── 1. Out-of-tree platform redirect ──
      // Replicates the CLI's reactNativePlatformResolver so that
      // `require('react-native')` and `require('react-native/...')`
      // resolve to react-native-windows when bundling for windows.
      let resolvedModuleName = moduleName;
      if (platform === 'windows') {
        if (moduleName === 'react-native') {
          resolvedModuleName = 'react-native-windows';
        } else if (moduleName.startsWith('react-native/')) {
          resolvedModuleName = `react-native-windows/${moduleName.slice(
            'react-native/'.length,
          )}`;
        }
      }

      // ── 2. LinearGradient shim ──
      // On Windows the native BVLinearGradient module targets UWP XAML
      // which won't compile under RN-Windows 0.81 (WinUI 3).  Redirect
      // to an SVG-based shim so the same import works everywhere.
      if (
        platform === 'windows' &&
        resolvedModuleName === 'react-native-linear-gradient'
      ) {
        return {
          filePath: path.resolve(
            __dirname,
            'src/shims/LinearGradientShim.tsx',
          ),
          type: 'sourceFile',
        };
      }

      // ── 3. MaskedView stub ──
      // @react-native-masked-view/masked-view has no Windows native
      // component (RNCMaskedView).  Redirect to a JS-only stub that
      // simply renders children without a mask.
      if (
        platform === 'windows' &&
        (resolvedModuleName === '@react-native-masked-view/masked-view' ||
         resolvedModuleName.startsWith('@react-native-masked-view/masked-view/'))
      ) {
        return {
          filePath: path.resolve(__dirname, 'src/shims/MaskedViewStub.js'),
          type: 'sourceFile',
        };
      }

      // ── 4. Gesture Handler stub ──
      // react-native-gesture-handler has no Windows native module.
      // Its module-level code calls getViewManagerConfig('getConstants')
      // which produces a LogBox error.  Redirect all imports (including
      // transitive ones from react-native-draggable-flatlist) to a JS stub.
      if (
        platform === 'windows' &&
        (resolvedModuleName === 'react-native-gesture-handler' ||
         resolvedModuleName.startsWith('react-native-gesture-handler/'))
      ) {
        return {
          filePath: path.resolve(__dirname, 'src/shims/gestureHandlerStub.js'),
          type: 'sourceFile',
        };
      }

      // ── 5. SVG stub ──
      // react-native-svg's native modules (RNSVGGroup, RNSVGSvgView)
      // aren't available on the RN-Windows WinUI 3 Composition renderer.
      // Redirect to a JS stub that renders Views/null for SVG elements.
      if (
        platform === 'windows' &&
        (resolvedModuleName === 'react-native-svg' ||
         resolvedModuleName.startsWith('react-native-svg/'))
      ) {
        return {
          filePath: path.resolve(__dirname, 'src/shims/svgStub.js'),
          type: 'sourceFile',
        };
      }

      // ── 6. Vector Icons create-icon-set shim ──
      // react-native-vector-icons' create-icon-set.js produces a XAML-style
      // font URI ("/Assets/Ionicons.ttf#Ionicons") for Windows.  RN-Windows'
      // Composition renderer uses DirectWrite which needs bare family names.
      // Redirect the internal create-icon-set module to our shim.
      if (
        platform === 'windows' &&
        context.originModulePath &&
        context.originModulePath
          .replace(/[\\/]/g, '/')
          .includes('/react-native-vector-icons/') &&
        (moduleName === './lib/create-icon-set' ||
         moduleName === '../lib/create-icon-set' ||
         moduleName.endsWith('/lib/create-icon-set'))
      ) {
        return {
          filePath: path.resolve(
            __dirname,
            'src/shims/createIconSetShim.js',
          ),
          type: 'sourceFile',
        };
      }

      // ── 7. Reanimated / Worklets stubs ──
      // react-native-reanimated and react-native-worklets have no Windows
      // native modules.  Redirect ALL imports (direct and transitive, e.g.
      // from react-native-draggable-flatlist) to lightweight JS-only stubs
      // so the modules never enter the Windows dependency graph.
      if (platform === 'windows') {
        if (
          resolvedModuleName === 'react-native-reanimated' ||
          resolvedModuleName.startsWith('react-native-reanimated/')
        ) {
          return {
            filePath: path.resolve(__dirname, 'src/shims/reanimatedStub.js'),
            type: 'sourceFile',
          };
        }
        if (
          resolvedModuleName === 'react-native-worklets' ||
          resolvedModuleName.startsWith('react-native-worklets/')
        ) {
          return {
            filePath: path.resolve(__dirname, 'src/shims/workletsStub.js'),
            type: 'sourceFile',
          };
        }
      }

      // ── 8. Default resolution with .windows.js fallback ──
      try {
        return context.resolveRequest(context, resolvedModuleName, platform);
      } catch (error) {
        // When a relative require originating inside react-native fails
        // on the windows platform, check whether react-native-windows
        // ships a .windows.js override for the same relative path.
        if (
          platform === 'windows' &&
          resolvedModuleName.startsWith('.') &&
          context.originModulePath &&
          context.originModulePath
            .replace(/\\/g, '/')
            .includes('/react-native/')
        ) {
          const originDir = path.dirname(context.originModulePath);
          const absTarget = path.resolve(originDir, resolvedModuleName);
          const normalRn = rnPath.replace(/\\/g, '/');
          const normalTarget = absTarget.replace(/\\/g, '/');

          if (normalTarget.startsWith(normalRn + '/')) {
            const relFromRn = path.relative(rnPath, absTarget);
            const rnwEquiv = path.join(rnwPath, relFromRn);
            const windowsFile = rnwEquiv + '.windows.js';
            if (fs.existsSync(windowsFile)) {
              return {filePath: windowsFile, type: 'sourceFile'};
            }
          }
        }
        throw error;
      }
    },
    //
  },
  transformer: {
    getTransformOptions: async () => ({
      transform: {
        experimentalImportSupport: false,
        inlineRequires: true,
      },
    }),
  },
};

module.exports = mergeConfig(getDefaultConfig(__dirname), config);
