import React from 'react';
import { Platform, StatusBar, StyleSheet, useColorScheme, View } from 'react-native';
import { SafeAreaProvider } from 'react-native-safe-area-context';

import { AppNavigator } from './src/navigation/AppNavigator';
import { FestivalProvider } from './src/app/festival/FestivalContext';
import { IntroScreen } from './src/screens/IntroScreen';

if (Platform.OS !== 'windows') {
  // `react-native-screens`' Windows native project currently targets UWP/WinUI2,
  // which is incompatible with RNW's default WinUI3 app template.
  // It is optional for React Navigation, so we enable it only on non-Windows.
  // eslint-disable-next-line @typescript-eslint/no-var-requires
  require('react-native-screens').enableScreens();
}

const RootView =
  Platform.OS === 'windows'
    ? View
    : // eslint-disable-next-line @typescript-eslint/no-var-requires
      require('react-native-gesture-handler').GestureHandlerRootView;

function App() {
  const isDarkMode = useColorScheme() === 'dark';
  const [showIntro, setShowIntro] = React.useState(true);

  console.log('[App] Rendering App component, Platform:', Platform.OS);

  return (
    <SafeAreaProvider>
      <StatusBar barStyle={isDarkMode ? 'light-content' : 'dark-content'} />
      <RootView style={styles.root}>
        {/* FestivalProvider wraps everything so song/image sync kicks off
            immediately in the background — even while the user is on the
            intro carousel. */}
        <FestivalProvider>
          {showIntro ? (
            <IntroScreen onContinue={() => setShowIntro(false)} />
          ) : (
            <AppNavigator />
          )}
        </FestivalProvider>
      </RootView>
    </SafeAreaProvider>
  );
}

const styles = StyleSheet.create({
  root: {
    flex: 1,
  },
});

export default App;
