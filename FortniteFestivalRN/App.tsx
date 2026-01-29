/**
 * Sample React Native App
 * https://github.com/facebook/react-native
 *
 * @format
 */

import { Platform, StatusBar, StyleSheet, Text, useColorScheme, View } from 'react-native';
import {
  SafeAreaProvider,
  useSafeAreaInsets,
} from 'react-native-safe-area-context';

import { getPersistenceKind } from './src/platform/festivalPersistence';

function App() {
  const isDarkMode = useColorScheme() === 'dark';

  return (
    <SafeAreaProvider>
      <StatusBar barStyle={isDarkMode ? 'light-content' : 'dark-content'} />
      <AppContent />
    </SafeAreaProvider>
  );
}

function AppContent() {
  const safeAreaInsets = useSafeAreaInsets();
  const persistence = Platform.OS === 'windows' ? getPersistenceKind() : 'not-configured';

  return (
    <View style={styles.container}>
      <View style={[styles.header, { paddingTop: safeAreaInsets.top }]}>
        <Text style={styles.title}>FortniteFestivalRN</Text>
        <Text style={styles.subtitle}>
          Android is rendering OK (Fabric={String(true)})
        </Text>
        {Platform.OS === 'windows' ? (
          <Text style={styles.subtitle}>Persistence: {persistence}</Text>
        ) : null}
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    backgroundColor: '#0B0F19',
    justifyContent: 'center',
    paddingHorizontal: 24,
  },
  header: {
    gap: 8,
  },
  title: {
    color: '#FFFFFF',
    fontSize: 28,
    fontWeight: '700',
  },
  subtitle: {
    color: '#B8C0CC',
    fontSize: 14,
  },
});

export default App;
