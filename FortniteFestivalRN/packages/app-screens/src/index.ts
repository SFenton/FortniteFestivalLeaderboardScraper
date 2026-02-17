// @festival/app-screens — Shared screens and navigation used by both local-app
// and server-app.

// ── Screens ─────────────────────────────────────────────────────────
export {SongsScreen} from './screens/SongsScreen';
export {SongDetailsScreen, SongDetailsView} from './screens/SongDetailsScreen';
export type {SongsStackParamList, SongDetailsScreenProps} from './screens/SongDetailsScreen';
export {StatisticsScreen} from './screens/StatisticsScreen';
export {SuggestionsScreen} from './screens/SuggestionsScreen';
export {SettingsScreen} from './screens/SettingsScreen';
export {SyncScreen} from './screens/SyncScreen';
export {WindowsHostScreen} from './screens/WindowsHostScreen';
export type {WindowsHostScreenProps} from './screens/WindowsHostScreen';

// ── Navigation ──────────────────────────────────────────────────────
export {Routes} from './navigation/routes';
export type {RouteName} from './navigation/routes';
export {createSubNavigator} from './navigation/createSubNavigator';
export {useTabBarLayout, useOptionalBottomTabBarHeight} from './navigation/useOptionalBottomTabBarHeight';
export type {TabBarLayout} from './navigation/useOptionalBottomTabBarHeight';
export {WindowsFlyoutUiProvider, useWindowsFlyoutUi, useRegisterOpenFlyout} from './navigation/windowsFlyoutUi';

// ── App Shell ───────────────────────────────────────────────────────
export {AppShell} from './navigation/AppShell';
export type {AppNavParamList, FlyoutConfig} from './navigation/AppShell';
