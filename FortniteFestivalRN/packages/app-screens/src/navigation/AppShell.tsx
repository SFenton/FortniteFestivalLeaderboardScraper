import React from 'react';
import {
  ActivityIndicator,
  Animated,
  Platform,
  Pressable,
  StyleSheet,
  Text,
  View,
} from 'react-native';
import {
  getFocusedRouteNameFromRoute,
  DarkTheme,
  NavigationContainer,
  useNavigationContainerRef,
} from '@react-navigation/native';
import {createBottomTabNavigator} from '@react-navigation/bottom-tabs';
import {createNativeBottomTabNavigator} from '@bottom-tabs/react-navigation';
import Icon from 'react-native-vector-icons/Ionicons';

import {useFestival} from '@festival/contexts';
import {AnimatedBackground, Colors, FrostedSurface, Gap, Font, LineHeight, Radius} from '@festival/ui';

import {Routes} from './routes';
import {createSubNavigator} from './createSubNavigator';
import {useWindowsFlyoutUi, useRegisterOpenFlyout} from './windowsFlyoutUi';
import {WindowsFlyoutUiProvider} from './windowsFlyoutUi';
import {WindowsHostScreen} from '../screens/WindowsHostScreen';
import {SongsScreen} from '../screens/SongsScreen';
import {StatisticsScreen} from '../screens/StatisticsScreen';
import {SuggestionsScreen} from '../screens/SuggestionsScreen';
import {SettingsScreen} from '../screens/SettingsScreen';

// ---------------------------------------------------------------------------
// Sub-navigators (created once at module scope for stable references)
// ---------------------------------------------------------------------------

const SongsNavigator = createSubNavigator('SongsList', SongsScreen);
const SuggestionsNavigator = createSubNavigator('SuggestionsList', SuggestionsScreen);
const StatisticsNavigator = createSubNavigator('StatisticsHome', StatisticsScreen);

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

export type AppNavParamList = {
  [Routes.Songs]: undefined;
  [Routes.Suggestions]: undefined;
  [Routes.Statistics]: undefined;
  [Routes.Settings]: undefined;
};

/**
 * Configuration for the Windows flyout drawer.
 * Each app variant passes different visual settings.
 */
export type FlyoutConfig = {
  /** Background color of the root Windows view (behind the flyout). */
  winRootBackground: string;
  /** Background color of the flyout drawer. */
  flyoutBackground: string;
  /** Right-side border color of the flyout. */
  flyoutBorderColor: string;
  /** Right-side border width of the flyout. */
  flyoutBorderWidth: number;
  /** Whether to show the "Menu" header row with a × close button. */
  showFlyoutHeader: boolean;
};

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

const SCREEN_COMPONENTS: Record<keyof AppNavParamList, React.ComponentType> = {
  [Routes.Songs]: () => <WindowsHostScreen ListComponent={SongsScreen} />,
  [Routes.Suggestions]: () => <WindowsHostScreen ListComponent={SuggestionsScreen} />,
  [Routes.Statistics]: () => <WindowsHostScreen ListComponent={StatisticsScreen} />,
  [Routes.Settings]: SettingsScreen,
};

const FLYOUT_WIDTH = 280;

// ---------------------------------------------------------------------------
// Tab components (shared between both app variants)
// ---------------------------------------------------------------------------

const Tab = createBottomTabNavigator<AppNavParamList>();
const NativeTab = createNativeBottomTabNavigator<AppNavParamList>();

// getImageSourceSync requires the native RNVectorIcons module which isn't
// available on Windows.  These are only consumed by IOSNativeTabs, so we
// guard them behind a platform check to avoid a module-scope crash.
const songsIcon =
  Platform.OS !== 'windows'
    ? Icon.getImageSourceSync('musical-notes', 24)
    : undefined;
const suggestionsIcon =
  Platform.OS !== 'windows'
    ? Icon.getImageSourceSync('sparkles', 24)
    : undefined;
const statisticsIcon =
  Platform.OS !== 'windows'
    ? Icon.getImageSourceSync('stats-chart', 24)
    : undefined;
const settingsIcon =
  Platform.OS !== 'windows'
    ? Icon.getImageSourceSync('settings', 24)
    : undefined;

function MobileTabs() {
  console.log('[MobileTabs] Rendering MobileTabs');
  return (
    <Tab.Navigator
      initialRouteName={Routes.Songs}
      screenOptions={{
        animation: 'none',
        lazy: false,
        headerStyle: styles.header,
        headerTitleStyle: styles.headerTitle,
        tabBarStyle: styles.tabBar,
        tabBarBackground: () => (
          <FrostedSurface
            tint="dark"
            intensity={22}
            fallbackColor={Colors.surfaceTabBar}
            style={styles.tabBarFrostedBackground}
          />
        ),
        tabBarActiveTintColor: Colors.textPrimary,
        tabBarInactiveTintColor: Colors.textTertiary,
      }}>
      <Tab.Screen
        name={Routes.Songs}
        component={SongsNavigator}
        options={({route}) => {
          const nested = getFocusedRouteNameFromRoute(route);
          const tabBarStyle =
            nested === 'SongDetails'
              ? ({display: 'none'} as const)
              : styles.tabBar;

          return {
            headerShown: false,
            tabBarStyle,
            tabBarLabel: 'Songs',
            tabBarIcon: ({color, size}) => (
              <Icon name="musical-notes" size={size} color={color} />
            ),
          };
        }}
      />
      <Tab.Screen
        name={Routes.Suggestions}
        component={SuggestionsNavigator}
        options={({route}) => {
          const nested = getFocusedRouteNameFromRoute(route);
          const tabBarStyle =
            nested === 'SongDetails'
              ? ({display: 'none'} as const)
              : styles.tabBar;

          return {
            headerShown: false,
            tabBarStyle,
            tabBarLabel: 'Suggestions',
            tabBarIcon: ({color, size}) => (
              <Icon name="sparkles" size={size} color={color} />
            ),
          };
        }}
      />
      <Tab.Screen
        name={Routes.Statistics}
        component={StatisticsNavigator}
        options={({route}) => {
          const nested = getFocusedRouteNameFromRoute(route);
          const tabBarStyle =
            nested === 'SongDetails'
              ? ({display: 'none'} as const)
              : styles.tabBar;

          return {
            headerShown: false,
            tabBarStyle,
            tabBarLabel: 'Statistics',
            tabBarIcon: ({color, size}) => (
              <Icon name="stats-chart" size={size} color={color} />
            ),
          };
        }}
      />
      <Tab.Screen
        name={Routes.Settings}
        component={SettingsScreen}
        options={{
          headerShown: false,
          tabBarLabel: 'Settings',
          tabBarIcon: ({color, size}) => (
            <Icon name="settings" size={size} color={color} />
          ),
        }}
      />
    </Tab.Navigator>
  );
}

/**
 * Native iOS tabs using react-native-bottom-tabs for iOS 26+ liquid glass support.
 * Falls back to standard native tab bar on older iOS versions.
 */
function IOSNativeTabs() {
  console.log('[IOSNativeTabs] Rendering IOSNativeTabs');
  return (
    <NativeTab.Navigator initialRouteName={Routes.Songs}>
      <NativeTab.Screen
        name={Routes.Songs}
        component={SongsNavigator}
        options={{
          title: 'Songs',
          tabBarIcon: () => songsIcon,
          lazy: false,
        }}
      />
      <NativeTab.Screen
        name={Routes.Suggestions}
        component={SuggestionsNavigator}
        options={{
          title: 'Suggestions',
          tabBarIcon: () => suggestionsIcon,
          lazy: false,
        }}
      />
      <NativeTab.Screen
        name={Routes.Statistics}
        component={StatisticsNavigator}
        options={{
          title: 'Statistics',
          tabBarIcon: () => statisticsIcon,
          lazy: false,
        }}
      />
      <NativeTab.Screen
        name={Routes.Settings}
        component={SettingsScreen}
        options={{
          title: 'Settings',
          tabBarIcon: () => settingsIcon,
          lazy: false,
        }}
      />
    </NativeTab.Navigator>
  );
}

// ---------------------------------------------------------------------------
// Windows flyout (parameterized via FlyoutConfig)
// ---------------------------------------------------------------------------

function WindowsFlyout({config}: {config: FlyoutConfig}) {
  const {actions} = useFestival();
  const {chromeHidden} = useWindowsFlyoutUi();
  const [route, setRoute] = React.useState<keyof AppNavParamList>(Routes.Songs);
  const [open, setOpen] = React.useState(false);
  const translateX = React.useRef(new Animated.Value(-FLYOUT_WIDTH)).current;
  const prevRouteRef = React.useRef<keyof AppNavParamList>(Routes.Songs);

  const openFlyout = React.useCallback(() => setOpen(true), []);
  useRegisterOpenFlyout(openFlyout);

  React.useEffect(() => {
    if (chromeHidden && open) {
      setOpen(false);
    }
  }, [chromeHidden, open]);

  React.useEffect(() => {
    Animated.timing(translateX, {
      toValue: open ? 0 : -FLYOUT_WIDTH,
      duration: 160,
      // RNW hit-testing can be flaky with native-driver transforms; keep it JS-driven.
      useNativeDriver: Platform.OS !== 'windows',
    }).start();
  }, [open, translateX]);

  React.useEffect(() => {
    const prev = prevRouteRef.current;
    if (prev !== route) {
      actions.logUi(`[NAV] ${prev} -> ${route}`);
      prevRouteRef.current = route;
    }
  }, [actions, route]);

  const ScreenComponent = SCREEN_COMPONENTS[route];

  const flyoutDynamicStyle = {
    backgroundColor: config.flyoutBackground,
    borderRightColor: config.flyoutBorderColor,
    borderRightWidth: config.flyoutBorderWidth,
  };

  return (
    <View style={[styles.winRoot, {backgroundColor: config.winRootBackground}]}>
      <View style={styles.winContent}>
        <ScreenComponent />
      </View>

      {!chromeHidden ? (
        <>
          {open ? (
            <Pressable
              style={styles.scrim}
              onPress={() => setOpen(false)}
              accessibilityRole="button"
              accessibilityLabel="Close navigation menu"
            />
          ) : null}

          <Animated.View
            pointerEvents={open ? 'auto' : 'none'}
            style={[styles.flyout, flyoutDynamicStyle, {transform: [{translateX}]}]}>
            {config.showFlyoutHeader ? (
              <View style={styles.flyoutHeader}>
                <Text style={styles.flyoutHeaderTitle}>Menu</Text>
                <Pressable
                  onPress={() => setOpen(false)}
                  hitSlop={10}
                  style={styles.flyoutClose}
                  accessibilityRole="button"
                  accessibilityLabel="Close navigation menu">
                  <Text style={styles.flyoutCloseText}>×</Text>
                </Pressable>
              </View>
            ) : null}

            {(
              Object.keys(SCREEN_COMPONENTS) as Array<keyof AppNavParamList>
            ).map(r => {
              const active = r === route;
              return (
                <Pressable
                  key={r}
                  onPress={() => {
                    setRoute(r);
                    setOpen(false);
                  }}
                  style={({pressed}) => [
                    styles.flyoutItem,
                    active && styles.flyoutItemActive,
                    pressed && styles.flyoutItemPressed,
                  ]}
                  accessibilityRole="button"
                  accessibilityLabel={`Go to ${r}`}>
                  <Text
                    style={[
                      styles.flyoutItemText,
                      active && styles.flyoutItemTextActive,
                    ]}>
                    {r}
                  </Text>
                </Pressable>
              );
            })}
          </Animated.View>
        </>
      ) : null}
    </View>
  );
}

// ---------------------------------------------------------------------------
// AppShell — the shared navigator shell
// ---------------------------------------------------------------------------

function AppShellInner({flyoutConfig}: {flyoutConfig: FlyoutConfig}) {
  console.log('[AppNavigator] Rendering AppNavigator, Platform:', Platform.OS);
  const {chromeHidden} = useWindowsFlyoutUi();
  const {
    state: {isReady, progressPct, settings, songs},
    actions,
  } = useFestival();

  const showBootOverlay = !isReady;

  const bootTitle = settings.hasEverSyncedSongs
    ? 'Updating Songs'
    : 'Running First Sync';
  const bootBody = settings.hasEverSyncedSongs
    ? 'Checking for updates to the song catalog and refreshing cached artwork.'
    : 'Downloading the song catalog and caching artwork. This may take a minute on first launch.';

  // NOTE: Keep boot overlay simple (no opacity animations).
  // Under the RN new-arch/Fabric, Animated opacity + callback can be flaky and can
  // leave a semi-transparent overlay mounted that makes the whole UI look "dim".
  const navRef = useNavigationContainerRef<AppNavParamList>();
  const prevRouteNameRef = React.useRef<string | undefined>(undefined);

  const navTheme = React.useMemo(
    () => ({
      ...DarkTheme,
      colors: {
        ...DarkTheme.colors,
        primary: Colors.accentPurple,
        // Keep transparent so the global AnimatedBackground can show through.
        background: Colors.transparent,
        card: Colors.transparent,
        text: Colors.textPrimary,
        border: Colors.borderSubtle,
        notification: Colors.accentPurple,
      },
    }),
    [],
  );

  return (
    <View style={{flex: 1, backgroundColor: Colors.backgroundApp}}>
      {!(Platform.OS === 'windows' && chromeHidden) ? (
        <AnimatedBackground songs={songs} animate dimOpacity={0.7} />
      ) : null}
      <View
        style={{flex: 1}}
        pointerEvents={showBootOverlay ? 'none' : 'auto'}>
        <NavigationContainer
          ref={navRef}
          theme={navTheme}
          onReady={() => {
            const current = navRef.getCurrentRoute()?.name;
            prevRouteNameRef.current = current;
            if (current) actions.logUi(`[NAV] start ${current}`);
          }}
          onStateChange={() => {
            const prev = prevRouteNameRef.current;
            const current = navRef.getCurrentRoute()?.name;
            if (current && prev && current !== prev) {
              actions.logUi(`[NAV] ${prev} -> ${current}`);
            }
            prevRouteNameRef.current = current;
          }}>
          {Platform.OS === 'windows' ? (
            <WindowsFlyout config={flyoutConfig} />
          ) : Platform.OS === 'ios' &&
            parseInt(String(Platform.Version), 10) >= 26 ? (
            <IOSNativeTabs />
          ) : (
            <MobileTabs />
          )}
        </NavigationContainer>
      </View>

      {showBootOverlay ? (
        <View pointerEvents="auto" style={styles.bootOverlay}>
          <View style={styles.bootInner}>
            <ActivityIndicator size="large" color={Colors.textPrimary} />
            <Text style={styles.bootTitle}>{bootTitle}</Text>
            <Text style={styles.bootBody}>{bootBody}</Text>

            <View style={styles.bootProgressOuter}>
              <View
                style={[
                  styles.bootProgressInner,
                  {
                    width: `${Math.max(0, Math.min(100, progressPct))}%`,
                  },
                ]}
              />
            </View>
          </View>
        </View>
      ) : null}
    </View>
  );
}

/**
 * Shared app navigator shell.
 *
 * Handles MobileTabs, IOSNativeTabs, WindowsFlyout (parameterized),
 * boot overlay, navigation theme, and AnimatedBackground.
 *
 * Each app variant just renders `<AppShell flyoutConfig={...} />` with
 * different visual parameters for the Windows flyout drawer.
 */
export function AppShell({flyoutConfig}: {flyoutConfig: FlyoutConfig}) {
  return (
    <WindowsFlyoutUiProvider>
      <AppShellInner flyoutConfig={flyoutConfig} />
    </WindowsFlyoutUiProvider>
  );
}

// ---------------------------------------------------------------------------
// Styles
// ---------------------------------------------------------------------------

const styles = StyleSheet.create({
  header: {
    backgroundColor: Colors.backgroundApp,
  },
  headerTitle: {
    color: Colors.textPrimary,
    fontWeight: '700',
  },
  tabBar: {
    backgroundColor: Colors.transparent,
    borderTopColor: Colors.transparent,
    borderTopWidth: 0,
    elevation: 0,
  },
  tabBarFrostedBackground: {
    flex: 1,
    borderRadius: 0,
    borderTopLeftRadius: 18,
    borderTopRightRadius: 18,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: Colors.borderCard,
  },
  winRoot: {
    flex: 1,
  },
  winContent: {
    flex: 1,
  },
  scrim: {
    ...StyleSheet.absoluteFillObject,
    backgroundColor: Colors.overlayScrim,
    zIndex: 5,
  },
  flyout: {
    position: 'absolute',
    top: 0,
    bottom: 0,
    left: 0,
    width: FLYOUT_WIDTH,
    paddingTop: 8,
    zIndex: 10,
  },
  flyoutHeader: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    paddingHorizontal: Gap.xl,
    paddingVertical: Gap.md,
    borderBottomColor: Colors.borderSubtle,
    borderBottomWidth: StyleSheet.hairlineWidth,
  },
  flyoutHeaderTitle: {
    color: Colors.textPrimary,
    fontWeight: '700',
    fontSize: Font.lg,
  },
  flyoutClose: {
    paddingHorizontal: Gap.lg,
    paddingVertical: 6,
    borderRadius: Radius.xs,
  },
  flyoutCloseText: {
    color: Colors.textPrimary,
    fontSize: Font.xl,
    lineHeight: LineHeight.lg,
  },
  flyoutItem: {
    paddingHorizontal: 14,
    paddingVertical: Gap.xl,
  },
  flyoutItemActive: {
    backgroundColor: Colors.surfaceSubtle,
  },
  flyoutItemPressed: {
    backgroundColor: Colors.surfacePressed,
  },
  flyoutItemText: {
    color: Colors.textSecondary,
    fontSize: Font.md,
  },
  flyoutItemTextActive: {
    color: Colors.textPrimary,
    fontWeight: '700',
  },
  bootOverlay: {
    ...StyleSheet.absoluteFillObject,
    backgroundColor: Colors.backgroundBoot,
    justifyContent: 'center',
    alignItems: 'center',
    paddingHorizontal: 24,
    zIndex: 1000,
  },
  bootInner: {
    width: '100%',
    maxWidth: 520,
    alignItems: 'center',
  },
  bootTitle: {
    marginTop: 18,
    fontSize: 24,
    fontWeight: '700',
    color: Colors.textPrimary,
    textAlign: 'center',
  },
  bootBody: {
    marginTop: Gap.lg,
    fontSize: Font.md,
    lineHeight: LineHeight.lg,
    color: Colors.textSemiTransparent,
    textAlign: 'center',
  },
  bootProgressOuter: {
    marginTop: 18,
    width: '100%',
    height: Gap.lg,
    borderRadius: Radius.xs,
    backgroundColor: Colors.whiteOverlay,
    overflow: 'hidden',
  },
  bootProgressInner: {
    height: '100%',
    borderRadius: Radius.xs,
    backgroundColor: Colors.textPrimary,
  },
});
