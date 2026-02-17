import React, {useEffect, useMemo} from 'react';
import {Linking, Platform, Pressable, ScrollView, StyleSheet, Text, View} from 'react-native';

import {Screen, FrostedSurface, FestivalTextInput, WIN_SCROLLBAR_INSET, Colors, Layout, Gap, Font, LineHeight, Radius, Opacity} from '@festival/ui';
import {useFestival, usePageInstrumentation} from '@festival/contexts';
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
        contentContainerStyle={[styles.content, {paddingBottom: tabBarHeight + 16, paddingRight: 20 + WIN_SCROLLBAR_INSET}]}
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
          <FestivalTextInput
            style={styles.input}
            value={state.exchangeCode}
            placeholder="Paste exchange code"
            placeholderTextColor={Colors.textDisabled}
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
    paddingHorizontal: Layout.paddingHorizontal,
    paddingVertical: Layout.paddingTop,
    gap: Gap.xl,
  },
  title: {
    color: Colors.textPrimary,
    fontSize: Font.title,
    fontWeight: '700',
  },
  subtitle: {
    color: Colors.textSubtle,
    fontSize: Font.md,
  },
  body: {
    color: Colors.textSecondary,
    fontSize: Font.md,
    lineHeight: LineHeight.lg,
  },
  smallMuted: {
    color: Colors.textMutedAlt,
    fontSize: Font.sm,
    lineHeight: LineHeight.sm,
  },
  card: {
    borderRadius: Radius.md,
    padding: Gap.xl,
    gap: Gap.md,
  },
  cardTitle: {
    color: Colors.textPrimary,
    fontSize: Font.lg,
    fontWeight: '600',
  },
  row: {
    flexDirection: 'row',
    gap: Gap.lg,
    alignItems: 'center',
  },
  button: {
    backgroundColor: Colors.accentBlueBright,
    paddingVertical: Gap.lg,
    paddingHorizontal: Gap.xl,
    borderRadius: Radius.sm,
    alignItems: 'center',
    justifyContent: 'center',
    flexGrow: 1,
  },
  buttonSecondary: {
    backgroundColor: Colors.surfaceMuted,
    paddingVertical: Gap.lg,
    paddingHorizontal: Gap.xl,
    borderRadius: Radius.sm,
    alignItems: 'center',
    justifyContent: 'center',
  },
  buttonDisabled: {
    opacity: Opacity.disabled,
  },
  buttonText: {
    color: Colors.textPrimary,
    fontWeight: '600',
  },
  input: {
    borderWidth: 1,
    borderColor: Colors.borderPrimary,
    backgroundColor: Colors.backgroundCard,
    borderRadius: Radius.sm,
    paddingHorizontal: Gap.xl,
    paddingVertical: Gap.lg,
    color: Colors.textPrimary,
  },
  progressOuter: {
    height: Gap.lg,
    borderRadius: Radius.full,
    backgroundColor: Colors.backgroundCard,
    overflow: 'hidden',
    borderWidth: 1,
    borderColor: Colors.borderPrimary,
  },
  progressInner: {
    height: '100%',
    backgroundColor: Colors.accentBlueBright,
  },
  log: {
    color: Colors.textSecondary,
    fontFamily: Platform.OS === 'ios' ? 'Menlo' : 'Consolas',
    fontSize: Font.sm,
    lineHeight: LineHeight.sm,
  },
});
