import React from 'react';
import { ActivityIndicator, Animated, Platform, Pressable, StyleSheet, Text, View } from 'react-native';
import {
  getFocusedRouteNameFromRoute,
  DarkTheme,
  NavigationContainer,
  useNavigationContainerRef,
} from '@react-navigation/native';
import { createBottomTabNavigator } from '@react-navigation/bottom-tabs';
import { createNativeBottomTabNavigator } from '@bottom-tabs/react-navigation';
import Icon from 'react-native-vector-icons/Ionicons';

import { Routes } from './routes';
import { SettingsScreen } from '../screens/SettingsScreen';
import { WindowsSongsHost } from '../screens/WindowsSongsHost';
import { WindowsSuggestionsHost } from '../screens/WindowsSuggestionsHost';
import { WindowsStatisticsHost } from '../screens/WindowsStatisticsHost';
import { useFestival } from '../app/festival/FestivalContext';
import { SongsNavigator } from './SongsNavigator';
import { SuggestionsNavigator } from './SuggestionsNavigator';
import { StatisticsNavigator } from './StatisticsNavigator';
import {useWindowsFlyoutUi, WindowsFlyoutUiProvider} from './windowsFlyoutUi';
import { AnimatedBackground } from '../ui/AnimatedBackground';
import { FrostedSurface } from '../ui/FrostedSurface';

export type AppNavParamList = {
  [Routes.Songs]: undefined;
  [Routes.Suggestions]: undefined;
  [Routes.Statistics]: undefined;
  [Routes.Settings]: undefined;
};

const Tab = createBottomTabNavigator<AppNavParamList>();
const NativeTab = createNativeBottomTabNavigator<AppNavParamList>();

const songsIcon = Icon.getImageSourceSync('musical-notes', 24);
const suggestionsIcon = Icon.getImageSourceSync('sparkles', 24);
const statisticsIcon = Icon.getImageSourceSync('stats-chart', 24);
const settingsIcon = Icon.getImageSourceSync('settings', 24);

function HamburgerButton({ onPress }: { onPress: () => void }) {
  return (
    <Pressable
      onPress={onPress}
      hitSlop={12}
      style={({ pressed }) => [styles.hamburger, pressed && styles.hamburgerPressed]}
      accessibilityRole="button"
      accessibilityLabel="Open navigation menu"
    >
      <Text style={styles.hamburgerText}>≡</Text>
    </Pressable>
  );
}

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
            fallbackColor="rgba(18,24,38,0.72)"
            style={styles.tabBarFrostedBackground}
          />
        ),
        tabBarActiveTintColor: '#FFFFFF',
        tabBarInactiveTintColor: '#9AA6B2',
      }}
    >
      <Tab.Screen
        name={Routes.Songs}
        component={SongsNavigator}
        options={({route}) => {
          const nested = getFocusedRouteNameFromRoute(route);
          const tabBarStyle = nested === 'SongDetails' ? ({display: 'none'} as const) : styles.tabBar;

          return {
            headerShown: false,
            tabBarStyle,
            tabBarLabel: 'Songs',
            tabBarIcon: ({color, size}) => <Icon name="musical-notes" size={size} color={color} />,
          };
        }}
      />
      <Tab.Screen
        name={Routes.Suggestions}
        component={SuggestionsNavigator}
        options={({route}) => {
          const nested = getFocusedRouteNameFromRoute(route);
          const tabBarStyle = nested === 'SongDetails' ? ({display: 'none'} as const) : styles.tabBar;

          return {
            headerShown: false,
            tabBarStyle,
            tabBarLabel: 'Suggestions',
            tabBarIcon: ({color, size}) => <Icon name="sparkles" size={size} color={color} />,
          };
        }}
      />
      <Tab.Screen
        name={Routes.Statistics}
        component={StatisticsNavigator}
        options={({route}) => {
          const nested = getFocusedRouteNameFromRoute(route);
          const tabBarStyle = nested === 'SongDetails' ? ({display: 'none'} as const) : styles.tabBar;

          return {
            headerShown: false,
            tabBarStyle,
            tabBarLabel: 'Statistics',
            tabBarIcon: ({color, size}) => <Icon name="stats-chart" size={size} color={color} />,
          };
        }}
      />
      <Tab.Screen 
        name={Routes.Settings} 
        component={SettingsScreen}
        options={{
          headerShown: false,
          tabBarLabel: 'Settings',
          tabBarIcon: ({color, size}) => <Icon name="settings" size={size} color={color} />,
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
    <NativeTab.Navigator
      initialRouteName={Routes.Songs}
    >
      <NativeTab.Screen
        name={Routes.Songs}
        component={SongsNavigator}
        options={{
          title: 'Songs',
          tabBarIcon: () => songsIcon,
          tabBarBlurEffect: undefined,
          tabBarStyle: { backgroundColor: 'transparent' },
          lazy: false,
        }}
      />
      <NativeTab.Screen
        name={Routes.Suggestions}
        component={SuggestionsNavigator}
        options={{
          title: 'Suggestions',
          tabBarIcon: () => suggestionsIcon,
          tabBarBlurEffect: undefined,
          tabBarStyle: { backgroundColor: 'transparent' },
          lazy: false,
        }}
      />
      <NativeTab.Screen
        name={Routes.Statistics}
        component={StatisticsNavigator}
        options={{
          title: 'Statistics',
          tabBarIcon: () => statisticsIcon,
          tabBarBlurEffect: undefined,
          tabBarStyle: { backgroundColor: 'transparent' },
          lazy: false,
        }}
      />
      <NativeTab.Screen
        name={Routes.Settings}
        component={SettingsScreen}
        options={{
          title: 'Settings',
          tabBarIcon: () => settingsIcon,
          tabBarBlurEffect: undefined,
          tabBarStyle: { backgroundColor: 'transparent' },
          lazy: false,
        }}
      />
    </NativeTab.Navigator>
  );
}

function WindowsFlyout() {
  const {actions} = useFestival();
  const {chromeHidden} = useWindowsFlyoutUi();
  const [route, setRoute] = React.useState<keyof AppNavParamList>(Routes.Songs);
  const [open, setOpen] = React.useState(false);
  const translateX = React.useRef(new Animated.Value(-FLYOUT_WIDTH)).current;
  const prevRouteRef = React.useRef<keyof AppNavParamList>(Routes.Songs);

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

  return (
    <View style={styles.winRoot}>
      {!chromeHidden ? (
        <View style={styles.winHeader}>
          <HamburgerButton onPress={() => setOpen(true)} />
          <Text style={styles.winHeaderTitle}>{route}</Text>
        </View>
      ) : null}

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
            style={[styles.flyout, { transform: [{ translateX }] }]}
          >
            <View style={styles.flyoutHeader}>
              <Text style={styles.flyoutHeaderTitle}>Menu</Text>
              <Pressable
                onPress={() => setOpen(false)}
                hitSlop={10}
                style={styles.flyoutClose}
                accessibilityRole="button"
                accessibilityLabel="Close navigation menu"
              >
                <Text style={styles.flyoutCloseText}>×</Text>
              </Pressable>
            </View>

            {(Object.keys(SCREEN_COMPONENTS) as Array<keyof AppNavParamList>).map((r) => {
              const active = r === route;
              return (
                <Pressable
                  key={r}
                  onPress={() => {
                    setRoute(r);
                    setOpen(false);
                  }}
                  style={({ pressed }) => [
                    styles.flyoutItem,
                    active && styles.flyoutItemActive,
                    pressed && styles.flyoutItemPressed,
                  ]}
                  accessibilityRole="button"
                  accessibilityLabel={`Go to ${r}`}
                >
                  <Text style={[styles.flyoutItemText, active && styles.flyoutItemTextActive]}>
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

function AppNavigatorInner() {
  console.log('[AppNavigator] Rendering AppNavigator, Platform:', Platform.OS);
  const {chromeHidden} = useWindowsFlyoutUi();
  const {
    state: {isReady, progressPct, settings},
    actions,
  } = useFestival();

  const showBootOverlay = !isReady;

  const bootTitle = settings.hasEverSyncedSongs ? 'Updating Songs' : 'Running First Sync';
  const bootBody = settings.hasEverSyncedSongs
    ? 'Checking for updates to the song catalog and refreshing cached artwork.'
    : 'Downloading the song catalog and caching artwork. This may take a minute on first launch.';

  // NOTE: Keep boot overlay simple (no opacity animations).
  // Under the RN new-arch/Fabric, Animated opacity + callback can be flaky and can
  // leave a semi-transparent overlay mounted that makes the whole UI look “dim”.
  const navRef = useNavigationContainerRef<AppNavParamList>();
  const prevRouteNameRef = React.useRef<string | undefined>(undefined);

  const navTheme = React.useMemo(
    () => ({
      ...DarkTheme,
      colors: {
        ...DarkTheme.colors,
        primary: '#7C3AED',
        // Keep transparent so the global AnimatedBackground can show through.
        background: 'transparent',
        card: 'transparent',
        text: '#FFFFFF',
        border: '#1E2A3A',
        notification: '#7C3AED',
      },
    }),
    [],
  );

  return (
    <View style={{flex: 1, backgroundColor: '#1A0830'}}>
      {!(Platform.OS === 'windows' && chromeHidden) ? (
        <AnimatedBackground animate dimOpacity={0.7} />
      ) : null}
      <View style={{flex: 1}} pointerEvents={showBootOverlay ? 'none' : 'auto'}>
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
            <WindowsFlyout />
          ) : Platform.OS === 'ios' && parseInt(String(Platform.Version), 10) >= 26 ? (
            <IOSNativeTabs />
          ) : (
            <MobileTabs />
          )}
        </NavigationContainer>
      </View>

      {showBootOverlay ? (
        <View pointerEvents="auto" style={styles.bootOverlay}>
          <View style={styles.bootInner}>
            <ActivityIndicator size="large" color="#FFFFFF" />
            <Text style={styles.bootTitle}>{bootTitle}</Text>
            <Text style={styles.bootBody}>{bootBody}</Text>

            <View style={styles.bootProgressOuter}>
              <View
                style={[
                  styles.bootProgressInner,
                  {width: `${Math.max(0, Math.min(100, progressPct))}%`},
                ]}
              />
            </View>
          </View>
        </View>
      ) : null}
    </View>
  );
}

export function AppNavigator() {
  return (
    <WindowsFlyoutUiProvider>
      <AppNavigatorInner />
    </WindowsFlyoutUiProvider>
  );
}

const SCREEN_COMPONENTS: Record<keyof AppNavParamList, React.ComponentType> = {
  [Routes.Songs]: WindowsSongsHost,
  [Routes.Suggestions]: WindowsSuggestionsHost,
  [Routes.Statistics]: WindowsStatisticsHost,
  [Routes.Settings]: SettingsScreen,
};

const FLYOUT_WIDTH = 280;

const styles = StyleSheet.create({
  header: {
    backgroundColor: '#1A0830',
  },
  headerTitle: {
    color: '#FFFFFF',
    fontWeight: '700',
  },
  tabBar: {
    backgroundColor: 'transparent',
    borderTopColor: 'transparent',
    borderTopWidth: 0,
    elevation: 0,
  },
  tabBarFrostedBackground: {
    flex: 1,
    borderRadius: 0,
    borderTopLeftRadius: 18,
    borderTopRightRadius: 18,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: '#263244',
  },
  winRoot: {
    flex: 1,
    backgroundColor: '#1A0830',
  },
  winHeader: {
    height: 48,
    flexDirection: 'row',
    alignItems: 'center',
    backgroundColor: '#1A0830',
    borderBottomColor: '#1E2A3A',
    borderBottomWidth: StyleSheet.hairlineWidth,
  },
  winHeaderTitle: {
    color: '#FFFFFF',
    fontWeight: '700',
    fontSize: 16,
    marginLeft: 8,
  },
  winContent: {
    flex: 1,
  },
  scrim: {
    ...StyleSheet.absoluteFillObject,
    backgroundColor: 'rgba(0,0,0,0.35)',
    zIndex: 5,
  },
  flyout: {
    position: 'absolute',
    top: 0,
    bottom: 0,
    left: 0,
    width: FLYOUT_WIDTH,
    backgroundColor: '#1A0830',
    borderRightColor: '#1E2A3A',
    borderRightWidth: StyleSheet.hairlineWidth,
    paddingTop: 8,
    zIndex: 10,
  },
  flyoutHeader: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    paddingHorizontal: 12,
    paddingVertical: 8,
    borderBottomColor: '#1E2A3A',
    borderBottomWidth: StyleSheet.hairlineWidth,
  },
  flyoutHeaderTitle: {
    color: '#FFFFFF',
    fontWeight: '700',
    fontSize: 16,
  },
  flyoutClose: {
    paddingHorizontal: 10,
    paddingVertical: 6,
    borderRadius: 8,
  },
  flyoutCloseText: {
    color: '#FFFFFF',
    fontSize: 20,
    lineHeight: 20,
  },
  flyoutItem: {
    paddingHorizontal: 14,
    paddingVertical: 12,
  },
  flyoutItemActive: {
    backgroundColor: '#162133',
  },
  flyoutItemPressed: {
    backgroundColor: '#101826',
  },
  flyoutItemText: {
    color: '#D7DEE8',
    fontSize: 14,
  },
  flyoutItemTextActive: {
    color: '#FFFFFF',
    fontWeight: '700',
  },
  hamburger: {
    marginLeft: 8,
    paddingHorizontal: 10,
    paddingVertical: 6,
    borderRadius: 8,
  },
  hamburgerPressed: {
    backgroundColor: '#162133',
  },
  hamburgerText: {
    color: '#FFFFFF',
    fontSize: 22,
    lineHeight: 22,
  },
  bootOverlay: {
    ...StyleSheet.absoluteFillObject,
    backgroundColor: '#0B0B0D',
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
    color: '#FFFFFF',
    textAlign: 'center',
  },
  bootBody: {
    marginTop: 10,
    fontSize: 14,
    lineHeight: 20,
    color: 'rgba(255,255,255,0.78)',
    textAlign: 'center',
  },
  bootProgressOuter: {
    marginTop: 18,
    width: '100%',
    height: 10,
    borderRadius: 6,
    backgroundColor: 'rgba(255,255,255,0.18)',
    overflow: 'hidden',
  },
  bootProgressInner: {
    height: '100%',
    borderRadius: 6,
    backgroundColor: '#FFFFFF',
  },
});
