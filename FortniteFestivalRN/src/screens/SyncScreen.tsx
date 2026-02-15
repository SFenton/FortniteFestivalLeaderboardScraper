import React, {useEffect, useMemo} from 'react';
import {Linking, Platform, Pressable, ScrollView, StyleSheet, Text, TextInput, View} from 'react-native';

import { Screen } from '@festival/ui/Screen';
import {FrostedSurface} from '@festival/ui/FrostedSurface';
import {useFestival} from '@festival/contexts';
import {usePageInstrumentation} from '@festival/contexts';
import {useTabBarLayout} from '../navigation/useOptionalBottomTabBarHeight';

export function SyncScreen() {
  usePageInstrumentation('Sync');

  const {height: tabBarHeight, marginBottom: tabBarMargin} = useTabBarLayout();

  const {state, actions} = useFestival();
  const {ensureInitializedAsync, startFetchAsync, clearLog, setExchangeCode} = actions;

  const generateExchangeCodeUrl =
    'https://www.epicgames.com/id/api/redirect?clientId=ec684b8c687f479fadea3cb2ad83f5c6&responseType=code';

  useEffect(() => {
    void ensureInitializedAsync();
  }, [ensureInitializedAsync]);

  const scoresCount = useMemo(() => {
    return Object.values(state.scoresIndex).filter(ld =>
      ld?.guitar?.initialized === true ||
      ld?.drums?.initialized === true ||
      ld?.bass?.initialized === true ||
      ld?.vocals?.initialized === true ||
      ld?.pro_guitar?.initialized === true ||
      ld?.pro_bass?.initialized === true,
    ).length;
  }, [state.scoresIndex]);

  return (
    <Screen>
      <ScrollView
        style={{flex: 1, marginBottom: tabBarMargin}}
        contentContainerStyle={[styles.content, {paddingBottom: tabBarHeight + 16}]}
        scrollIndicatorInsets={{bottom: tabBarHeight}}
        showsVerticalScrollIndicator={false}
        showsHorizontalScrollIndicator={false}
        keyboardShouldPersistTaps="handled"
      >
        <Text style={styles.title}>Sync</Text>
        <Text style={styles.subtitle}>Platform: {Platform.OS}</Text>

        <FrostedSurface style={styles.card} tint="dark" intensity={18}>
          <Text style={styles.cardTitle}>Status</Text>
          <Text style={styles.body}>Songs: {state.songs.length}</Text>
          <Text style={styles.body}>Cached scores: {scoresCount}</Text>
          <Text style={styles.body}>Progress: {state.progressLabel}</Text>
          <Text style={styles.smallMuted}>{state.metrics}</Text>

          <View style={styles.progressOuter}>
            <View style={[styles.progressInner, {width: `${Math.max(0, Math.min(100, state.progressPct))}%`}]} />
          </View>

          <View style={styles.row}>
            <Pressable
              style={[styles.button, (state.isInitializing || state.isFetching) && styles.buttonDisabled]}
              disabled={state.isInitializing || state.isFetching}
              onPress={() => void ensureInitializedAsync({force: true})}>
              <Text style={styles.buttonText}>{state.isInitializing ? 'Initializing…' : 'Re-sync Songs'}</Text>
            </Pressable>

            <Pressable
              style={[styles.buttonSecondary, (state.isInitializing || state.isFetching) && styles.buttonDisabled]}
              disabled={state.isInitializing || state.isFetching}
              onPress={() => void clearLog()}>
              <Text style={styles.buttonText}>Clear Log</Text>
            </Pressable>
          </View>
        </FrostedSurface>

        <FrostedSurface style={styles.card} tint="dark" intensity={18}>
          <Text style={styles.cardTitle}>Exchange Code</Text>
          <TextInput
            style={styles.input}
            value={state.exchangeCode}
            placeholder="Paste exchange code"
            placeholderTextColor="#607089"
            autoCapitalize="none"
            autoCorrect={false}
            onChangeText={setExchangeCode}
          />

          <View style={styles.row}>
            <Pressable
              style={[styles.button, (!state.exchangeCode.trim() || state.isInitializing || state.isFetching) && styles.buttonDisabled]}
              disabled={!state.exchangeCode.trim() || state.isInitializing || state.isFetching}
              onPress={() => void startFetchAsync()}>
              <Text style={styles.buttonText}>{state.isFetching ? 'Fetching…' : 'Retrieve Scores'}</Text>
            </Pressable>

            <Pressable
              style={styles.buttonSecondary}
              onPress={() => void Linking.openURL(generateExchangeCodeUrl)}>
              <Text style={styles.buttonText}>Generate Code</Text>
            </Pressable>
          </View>
        </FrostedSurface>

        <FrostedSurface style={styles.card} tint="dark" intensity={18}>
          <Text style={styles.cardTitle}>Options</Text>
          <Text style={styles.smallMuted}>
            Concurrency is now configured in Settings. Until song selection is implemented, score fetch runs across all synced songs.
          </Text>
        </FrostedSurface>

        <FrostedSurface style={styles.card} tint="dark" intensity={18}>
          <Text style={styles.cardTitle}>Log</Text>
          <Text style={styles.log}>{state.logJoined || '(empty)'}</Text>
        </FrostedSurface>
      </ScrollView>
    </Screen>
  );
}

const styles = StyleSheet.create({
  content: {
    paddingHorizontal: 20,
    paddingVertical: 16,
    gap: 12,
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
  smallMuted: {
    color: '#92A0B2',
    fontSize: 12,
    lineHeight: 16,
  },
  card: {
    borderRadius: 12,
    padding: 12,
    gap: 8,
  },
  cardTitle: {
    color: '#FFFFFF',
    fontSize: 16,
    fontWeight: '600',
  },
  row: {
    flexDirection: 'row',
    gap: 10,
    alignItems: 'center',
  },
  button: {
    backgroundColor: '#4C7DFF',
    paddingVertical: 10,
    paddingHorizontal: 12,
    borderRadius: 10,
    alignItems: 'center',
    justifyContent: 'center',
    flexGrow: 1,
  },
  buttonSecondary: {
    backgroundColor: '#223047',
    paddingVertical: 10,
    paddingHorizontal: 12,
    borderRadius: 10,
    alignItems: 'center',
    justifyContent: 'center',
  },
  buttonDisabled: {
    opacity: 0.5,
  },
  buttonText: {
    color: '#FFFFFF',
    fontWeight: '600',
  },
  input: {
    borderWidth: 1,
    borderColor: '#2B3B55',
    backgroundColor: '#0B1220',
    borderRadius: 10,
    paddingHorizontal: 12,
    paddingVertical: 10,
    color: '#FFFFFF',
  },
  progressOuter: {
    height: 10,
    borderRadius: 999,
    backgroundColor: '#0B1220',
    overflow: 'hidden',
    borderWidth: 1,
    borderColor: '#2B3B55',
  },
  progressInner: {
    height: '100%',
    backgroundColor: '#4C7DFF',
  },
  log: {
    color: '#D7DEE8',
    fontFamily: Platform.OS === 'ios' ? 'Menlo' : 'Consolas',
    fontSize: 12,
    lineHeight: 16,
  },
});
