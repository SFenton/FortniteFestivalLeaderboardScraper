import React, {useCallback, useMemo} from 'react';
import {Linking, Platform, Pressable, ScrollView, StyleSheet, Switch, Text, TextInput, View} from 'react-native';
import MaskedView from '@react-native-masked-view/masked-view';
import LinearGradient from 'react-native-linear-gradient';

import { Screen } from '../ui/Screen';
import {usePageInstrumentation} from '../app/instrumentation/usePageInstrumentation';
import {useFestival} from '../app/festival/FestivalContext';
import {IntSlider} from '../ui/IntSlider';
import {FrostedSurface} from '../ui/FrostedSurface';
import {useTabBarLayout} from '../navigation/useOptionalBottomTabBarHeight';
import {PageHeader} from '../ui/PageHeader';

export function SettingsScreen() {
  usePageInstrumentation('Settings');
  const {height: tabBarHeight, marginBottom: tabBarMargin} = useTabBarLayout();
  const {state, actions} = useFestival();

  const generateExchangeCodeUrl =
    'https://www.epicgames.com/id/api/redirect?clientId=ec684b8c687f479fadea3cb2ad83f5c6&responseType=code';

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

  const dop = useMemo(() => {
    const raw = Math.round(state.settings.degreeOfParallelism);
    return Math.max(1, Math.min(32, raw));
  }, [state.settings.degreeOfParallelism]);

  const setDop = useCallback((next: number) => {
    actions.setSettings({...state.settings, degreeOfParallelism: next});
  }, [actions, state.settings]);

  const toggleHideSongIcons = useCallback(() => {
    const next = !state.settings.songsHideInstrumentIcons;
    actions.setSettings({
      ...state.settings,
      songsHideInstrumentIcons: next,
    });
  }, [actions, state.settings]);

  const handleClearImageCache = async () => {
    await actions.clearImageCache();
    // Optionally trigger a re-sync to download images again
    await actions.ensureInitializedAsync();
  };

  return (
    <Screen>
      <View style={styles.wrapper}>
        <PageHeader title="Settings" />

        <MaskedView
          style={styles.fadeScrollContainer}
          maskElement={
            <View style={styles.fadeMaskContainer}>
              <LinearGradient
                colors={['transparent', 'black']}
                style={styles.fadeGradient}
              />
              <View style={styles.fadeMaskOpaque} />
              <LinearGradient
                colors={['black', 'transparent']}
                style={styles.fadeGradient}
              />
            </View>
          }
        >
          <ScrollView
            style={{flex: 1, marginBottom: tabBarMargin}}
            contentContainerStyle={[styles.content, {paddingBottom: tabBarHeight + 16}]}
            scrollIndicatorInsets={{bottom: tabBarHeight}}
            showsVerticalScrollIndicator={false}
            showsHorizontalScrollIndicator={false}
            keyboardShouldPersistTaps="handled"
          >

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
              onPress={() => void actions.ensureInitializedAsync({force: true})}>
              <Text style={styles.buttonText}>{state.isInitializing ? 'Initializing…' : 'Re-sync Songs'}</Text>
            </Pressable>

            <Pressable
              style={[styles.buttonSecondary, (state.isInitializing || state.isFetching) && styles.buttonDisabled]}
              disabled={state.isInitializing || state.isFetching}
              onPress={() => void actions.clearLog()}>
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
            onChangeText={actions.setExchangeCode}
          />

          <View style={styles.row}>
            <Pressable
              style={[styles.button, (!state.exchangeCode.trim() || state.isInitializing || state.isFetching) && styles.buttonDisabled]}
              disabled={!state.exchangeCode.trim() || state.isInitializing || state.isFetching}
              onPress={() => void actions.startFetchAsync()}>
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
          <Text style={styles.cardTitle}>Sync</Text>
          <View style={styles.rowBetween}>
            <Text style={styles.body}>Concurrency</Text>
            <Text style={styles.value}>{dop}</Text>
          </View>
          <IntSlider min={1} max={32} value={dop} onChange={setDop} disabled={state.isInitializing || state.isFetching} />
          <Text style={styles.smallMuted}>Range: 1–32. Higher values may be faster, but use more CPU/network.</Text>
        </FrostedSurface>

        <FrostedSurface style={styles.card} tint="dark" intensity={18}>
          <Text style={styles.cardTitle}>Songs List</Text>

          <View style={styles.toggleRow}>
            <Text style={styles.body}>Hide instrument icons</Text>
            <Switch
              value={state.settings.songsHideInstrumentIcons}
              onValueChange={() => toggleHideSongIcons()}
              trackColor={{false: '#263244', true: '#2D82E6'}}
              thumbColor={Platform.OS === 'android' ? '#FFFFFF' : undefined}
              accessibilityLabel="Hide instrument icons"
            />
          </View>
        </FrostedSurface>

        <FrostedSurface style={styles.card} tint="dark" intensity={18}>
          <Text style={styles.cardTitle}>Image Cache</Text>
          <Pressable
            style={({pressed}) => [styles.buttonPurple, pressed && styles.buttonPressed, state.isInitializing && styles.buttonDisabled]}
            disabled={state.isInitializing}
            onPress={handleClearImageCache}>
            <Text style={styles.buttonText}>Clear Image Cache & Re-sync</Text>
          </Pressable>
        </FrostedSurface>

        <FrostedSurface style={styles.card} tint="dark" intensity={18}>
          <Text style={styles.cardTitle}>Log</Text>
          <Text style={styles.log}>{state.logJoined || '(empty)'}</Text>
        </FrostedSurface>
      </ScrollView>
        </MaskedView>
      </View>
    </Screen>
  );
}

const styles = StyleSheet.create({
  wrapper: {
    flex: 1,
    paddingHorizontal: 20,
    paddingTop: 16,
    paddingBottom: 4,
    gap: 12,
  },
  fadeScrollContainer: {
    flex: 1,
  },
  fadeMaskContainer: {
    flex: 1,
  },
  fadeMaskOpaque: {
    flex: 1,
    backgroundColor: '#000000',
  },
  fadeGradient: {
    height: 32,
  },
  content: {
    paddingTop: 32,
    gap: 12,
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
  rowBetween: {
    flexDirection: 'row',
    justifyContent: 'space-between',
    alignItems: 'center',
  },
  toggleRow: {
    flexDirection: 'row',
    justifyContent: 'space-between',
    alignItems: 'center',
    paddingVertical: 10,
  },
  value: {
    color: '#FFFFFF',
    fontSize: 14,
    fontWeight: '700',
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
  buttonPurple: {
    backgroundColor: '#7C3AED',
    paddingVertical: 10,
    paddingHorizontal: 12,
    borderRadius: 10,
    alignItems: 'center',
    justifyContent: 'center',
  },
  buttonPressed: {
    opacity: 0.7,
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
