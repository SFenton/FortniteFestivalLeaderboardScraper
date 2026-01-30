import React from 'react';
import { Animated, Platform, Pressable, StyleSheet, Text, View } from 'react-native';
import {
  getFocusedRouteNameFromRoute,
  NavigationContainer,
  useNavigationContainerRef,
} from '@react-navigation/native';
import { createBottomTabNavigator } from '@react-navigation/bottom-tabs';
import Icon from 'react-native-vector-icons/Ionicons';

import { Routes } from './routes';
import { SettingsScreen } from '../screens/SettingsScreen';
import { WindowsSongsHost } from '../screens/WindowsSongsHost';
import { WindowsSuggestionsHost } from '../screens/WindowsSuggestionsHost';
import { WindowsStatisticsHost } from '../screens/WindowsStatisticsHost';
import { SyncScreen } from '../screens/SyncScreen';
import { useFestival } from '../app/festival/FestivalContext';
import { SongsNavigator } from './SongsNavigator';
import { SuggestionsNavigator } from './SuggestionsNavigator';
import { StatisticsNavigator } from './StatisticsNavigator';
import {useWindowsFlyoutUi, WindowsFlyoutUiProvider} from './windowsFlyoutUi';

export type AppNavParamList = {
  [Routes.Sync]: undefined;
  [Routes.Songs]: undefined;
  [Routes.Suggestions]: undefined;
  [Routes.Statistics]: undefined;
  [Routes.Settings]: undefined;
};

const Tab = createBottomTabNavigator<AppNavParamList>();

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
      screenOptions={{
        headerStyle: styles.header,
        headerTitleStyle: styles.headerTitle,
        tabBarStyle: styles.tabBar,
        tabBarActiveTintColor: '#FFFFFF',
        tabBarInactiveTintColor: '#9AA6B2',
      }}
    >
      <Tab.Screen 
        name={Routes.Sync} 
        component={SyncScreen}
        options={{
          headerShown: false,
          tabBarIcon: ({color, size}) => <Icon name="refresh" size={size} color={color} />,
        }}
      />
      <Tab.Screen
        name={Routes.Songs}
        component={SongsNavigator}
        options={({route}) => {
          const nested = getFocusedRouteNameFromRoute(route);
          const tabBarStyle = nested === 'SongDetails' ? {display: 'none'} : styles.tabBar;

          return {
            headerShown: false,
            tabBarStyle,
            tabBarIcon: ({color, size}) => <Icon name="musical-notes" size={size} color={color} />,
          };
        }}
      />
      <Tab.Screen
        name={Routes.Suggestions}
        component={SuggestionsNavigator}
        options={({route}) => {
          const nested = getFocusedRouteNameFromRoute(route);
          const tabBarStyle = nested === 'SongDetails' ? {display: 'none'} : styles.tabBar;

          return {
            headerShown: false,
            tabBarStyle,
            tabBarIcon: ({color, size}) => <Icon name="sparkles" size={size} color={color} />,
          };
        }}
      />
      <Tab.Screen
        name={Routes.Statistics}
        component={StatisticsNavigator}
        options={({route}) => {
          const nested = getFocusedRouteNameFromRoute(route);
          const tabBarStyle = nested === 'SongDetails' ? {display: 'none'} : styles.tabBar;

          return {
            headerShown: false,
            tabBarStyle,
            tabBarIcon: ({color, size}) => <Icon name="stats-chart" size={size} color={color} />,
          };
        }}
      />
      <Tab.Screen 
        name={Routes.Settings} 
        component={SettingsScreen}
        options={{
          headerShown: false,
          tabBarIcon: ({color, size}) => <Icon name="settings" size={size} color={color} />,
        }}
      />
    </Tab.Navigator>
  );
}

function WindowsFlyout() {
  const {actions} = useFestival();
  const {chromeHidden} = useWindowsFlyoutUi();
  const [route, setRoute] = React.useState<keyof AppNavParamList>(Routes.Sync);
  const [open, setOpen] = React.useState(false);
  const translateX = React.useRef(new Animated.Value(-FLYOUT_WIDTH)).current;
  const prevRouteRef = React.useRef<keyof AppNavParamList>(Routes.Sync);

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

export function AppNavigator() {
  console.log('[AppNavigator] Rendering AppNavigator, Platform:', Platform.OS);
  const {actions} = useFestival();
  const navRef = useNavigationContainerRef<AppNavParamList>();
  const prevRouteNameRef = React.useRef<string | undefined>(undefined);

  return (
    <NavigationContainer
      ref={navRef}
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
        <WindowsFlyoutUiProvider>
          <WindowsFlyout />
        </WindowsFlyoutUiProvider>
      ) : (
        <MobileTabs />
      )}
    </NavigationContainer>
  );
}

const SCREEN_COMPONENTS: Record<keyof AppNavParamList, React.ComponentType> = {
  [Routes.Sync]: SyncScreen,
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
    backgroundColor: '#1A0830',
    borderTopColor: '#1E2A3A',
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
});
