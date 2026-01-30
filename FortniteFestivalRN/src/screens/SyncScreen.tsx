import React, {useCallback, useEffect, useMemo, useState} from 'react';
import {Linking, Platform, Pressable, ScrollView, StyleSheet, Text, TextInput, View} from 'react-native';

import { Screen } from '../ui/Screen';
import {useFestival} from '../app/festival/FestivalContext';
import type {Settings} from '../core/settings';
import {usePageInstrumentation} from '../app/instrumentation/usePageInstrumentation';

export function SyncScreen() {
  usePageInstrumentation('Sync');

  const {state, actions} = useFestival();
  const {ensureInitializedAsync, startFetchAsync, clearLog, setExchangeCode, setSettings} = actions;

  const generateExchangeCodeUrl =
    'https://www.epicgames.com/id/api/redirect?clientId=ec684b8c687f479fadea3cb2ad83f5c6&responseType=code';

  const [dopText, setDopText] = useState(String(state.settings.degreeOfParallelism));

  useEffect(() => {
    void ensureInitializedAsync();
  }, [ensureInitializedAsync]);

  useEffect(() => {
    setDopText(String(state.settings.degreeOfParallelism));
  }, [state.settings.degreeOfParallelism]);

  const scoresCount = useMemo(() => Object.keys(state.scoresIndex).length, [state.scoresIndex]);

  const updateSettings = useCallback((partial: Partial<Settings>) => {
    setSettings({...state.settings, ...partial});
  }, [setSettings, state.settings]);

  return (
    <Screen>
      <ScrollView contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled">
        <Text style={styles.title}>Sync</Text>
        <Text style={styles.subtitle}>Platform: {Platform.OS}</Text>

        <View style={styles.card}>
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
        </View>

        <View style={styles.card}>
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
        </View>

        <View style={styles.card}>
          <Text style={styles.cardTitle}>Options</Text>
          <View style={styles.row}>
            <Text style={styles.body}>Concurrency</Text>
            <TextInput
              style={[styles.input, styles.inputSmall]}
              value={dopText}
              keyboardType={Platform.OS === 'ios' ? 'number-pad' : 'numeric'}
              onChangeText={t => {
                setDopText(t);
                const v = Number.parseInt(t, 10);
                if (Number.isFinite(v) && v > 0) updateSettings({degreeOfParallelism: v});
              }}
            />
          </View>
          <Text style={styles.smallMuted}>
            Note: until song selection is implemented, score fetch runs across all synced songs.
          </Text>
        </View>

        <View style={styles.card}>
          <Text style={styles.cardTitle}>Log</Text>
          <Text style={styles.log}>{state.logJoined || '(empty)'}</Text>
        </View>
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
    borderWidth: 1,
    borderColor: '#263244',
    backgroundColor: '#121826',
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
  inputSmall: {
    width: 90,
    textAlign: 'center',
    paddingVertical: 8,
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
