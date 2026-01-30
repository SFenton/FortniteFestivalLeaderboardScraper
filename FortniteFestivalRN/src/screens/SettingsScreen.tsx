import React from 'react';
import { Platform, Pressable, StyleSheet, Text, View } from 'react-native';

import { Screen } from '../ui/Screen';
import { getPersistenceKind } from '../platform/festivalPersistence';
import {usePageInstrumentation} from '../app/instrumentation/usePageInstrumentation';
import {useFestival} from '../app/festival/FestivalContext';

export function SettingsScreen() {
  usePageInstrumentation('Settings');
  const {actions} = useFestival();

  const persistence = Platform.OS === 'windows' ? getPersistenceKind() : 'mobile';

  const handleClearImageCache = async () => {
    await actions.clearImageCache();
    // Optionally trigger a re-sync to download images again
    await actions.ensureInitializedAsync();
  };

  return (
    <Screen>
      <View style={styles.content}>
        <Text style={styles.title}>Settings</Text>
        <Text style={styles.subtitle}>Persistence: {persistence}</Text>
        <Text style={styles.body}>
          Scaffold screen for settings + instrument configuration.
        </Text>
        
        <View style={styles.section}>
          <Text style={styles.sectionTitle}>Image Cache</Text>
          <Pressable
            style={({pressed}) => [styles.button, pressed && styles.buttonPressed]}
            onPress={handleClearImageCache}>
            <Text style={styles.buttonText}>Clear Image Cache & Re-sync</Text>
          </Pressable>
        </View>
      </View>
    </Screen>
  );
}

const styles = StyleSheet.create({
  content: {
    flex: 1,
    paddingHorizontal: 20,
    paddingVertical: 16,
    gap: 10,
  },
  title: {
    color: '#FFFFFF',
    fontSize: 22,
    fontWeight: '700',
  },
  subtitle: {
    color: '#B8C0CC',
    fontSize: 14,
  },
  body: {
    color: '#D7DEE8',
    fontSize: 14,
    lineHeight: 20,
  },
  section: {
    marginTop: 24,
    gap: 12,
  },
  sectionTitle: {
    color: '#FFFFFF',
    fontSize: 16,
    fontWeight: '600',
  },
  button: {
    backgroundColor: '#7C3AED',
    paddingVertical: 12,
    paddingHorizontal: 16,
    borderRadius: 8,
    alignItems: 'center',
  },
  buttonPressed: {
    opacity: 0.7,
  },
  buttonText: {
    color: '#FFFFFF',
    fontSize: 14,
    fontWeight: '600',
  },
});
