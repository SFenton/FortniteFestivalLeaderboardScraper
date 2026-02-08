import React, {useCallback} from 'react';
import {Alert, Linking, Platform, Pressable, ScrollView, StyleSheet, Switch, Text, TextInput, View} from 'react-native';
import MaskedView from '@react-native-masked-view/masked-view';
import LinearGradient from 'react-native-linear-gradient';
import {isLiquidGlassSupported} from '@callstack/liquid-glass';

import { Screen } from '../ui/Screen';
import {usePageInstrumentation} from '../app/instrumentation/usePageInstrumentation';
import {useFestival} from '../app/festival/FestivalContext';
import {FrostedSurface} from '../ui/FrostedSurface';
import {useTabBarLayout} from '../navigation/useOptionalBottomTabBarHeight';
import {PageHeader} from '../ui/PageHeader';
import {reorderPIOForVisibilityChange, showSettingKeyForInstrument} from '../core/songListConfig';
import {InstrumentKeys} from '../core/instruments';

/* ────────────────────────── Toggle row (reused) ────────────────────────── */

function ToggleRow(props: {label: string; checked: boolean; onToggle: () => void; first?: boolean; last?: boolean}) {
  return (
    <Pressable
      onPress={props.onToggle}
      style={({pressed}) => [
        styles.orderRow,
        props.first && styles.orderRowFirst,
        props.last && styles.orderRowLast,
        !props.first && styles.orderRowSeparator,
        pressed && styles.rowBtnPressed,
      ]}
      accessibilityRole="switch"
    >
      <Text style={styles.orderName}>{props.label}</Text>
      <Switch
        value={props.checked}
        onValueChange={props.onToggle}
        trackColor={{false: '#263244', true: 'rgba(45,130,230,1)'}}
        thumbColor={props.checked ? '#FFFFFF' : '#8899AA'}
      />
    </Pressable>
  );
}

/* ── Toggle row with descriptor text ── */

function DescriptorToggleRow(props: {
  label: string;
  description: string;
  checked: boolean;
  onToggle: () => void;
  disabled?: boolean;
  first?: boolean;
  last?: boolean;
}) {
  return (
    <Pressable
      onPress={props.disabled ? undefined : props.onToggle}
      disabled={props.disabled}
      style={({pressed}) => [
        styles.orderRow,
        {alignItems: 'flex-start'},
        props.first && styles.orderRowFirst,
        props.last && styles.orderRowLast,
        !props.first && styles.orderRowSeparator,
        pressed && !props.disabled && styles.rowBtnPressed,
        props.disabled && styles.rowDisabled,
      ]}
      accessibilityRole="switch"
    >
      <View style={{flex: 1, marginRight: 12}}>
        <Text style={[styles.orderName, props.disabled && styles.textDisabled]}>{props.label}</Text>
        <Text style={[styles.descriptorText, props.disabled && styles.textDisabled]}>{props.description}</Text>
      </View>
      <View style={props.disabled ? styles.rowDisabled : undefined} pointerEvents={props.disabled ? 'none' : 'auto'}>
        <Switch
          value={props.checked}
          onValueChange={props.onToggle}
          trackColor={{false: '#263244', true: 'rgba(45,130,230,1)'}}
          thumbColor={props.checked ? '#FFFFFF' : '#8899AA'}
        />
      </View>
    </Pressable>
  );
}

/* ── Choice button (mirrors SortModal pattern) ── */

function Choice(props: {label: string; selected: boolean; onPress: () => void}) {
  return (
    <Pressable onPress={props.onPress} style={({pressed}) => [{flex: 1}, pressed && styles.rowBtnPressed]}>
      <FrostedSurface style={[styles.choice, props.selected && styles.choiceSelected]} tint="dark" intensity={12}>
        <Text style={[styles.choiceText, props.selected && styles.choiceTextSelected]}>{props.label}</Text>
      </FrostedSurface>
    </Pressable>
  );
}

/* ─────────────────────────── Settings screen ───────────────────────────── */

export function SettingsScreen() {
  usePageInstrumentation('Settings');
  const {height: tabBarHeight, marginBottom: tabBarMargin} = useTabBarLayout();
  const {state, actions} = useFestival();

  const generateExchangeCodeUrl =
    'https://www.epicgames.com/id/api/redirect?clientId=ec684b8c687f479fadea3cb2ad83f5c6&responseType=code';

  /* ── instrument toggles (same backend values as filter modal) ── */

  const toggleSetting = useCallback(
    (key: 'queryLead' | 'queryBass' | 'queryDrums' | 'queryVocals' | 'queryProLead' | 'queryProBass' | 'showLead' | 'showBass' | 'showDrums' | 'showVocals' | 'showProLead' | 'showProBass' | 'songsHideInstrumentIcons') => {
      const next = {...state.settings, [key]: !state.settings[key]};

      // When toggling a show* setting, also reorder the Primary Instrument Order:
      // hidden instruments move to end; re-enabled instruments return to their default position.
      const instrumentKey = InstrumentKeys.find(k => showSettingKeyForInstrument(k) === key);
      if (instrumentKey) {
        const isNowVisible = !state.settings[key]; // toggling, so new value is the inverse
        next.songsPrimaryInstrumentOrder = reorderPIOForVisibilityChange(
          state.settings.songsPrimaryInstrumentOrder,
          instrumentKey,
          isNowVisible,
          state.settings, // pass current (pre-toggle) show settings
        );
      }

      actions.setSettings(next);
    },
    [actions, state.settings],
  );

  const handleClearImageCache = () => {
    Alert.alert(
      'Clear Image Cache & Re-Sync',
      'This will clear all cached songs and images and require an online connection to re-sync them. Your local scores will not be deleted.\n\nOnly proceed if you are ready to re-sync.',
      [
        {text: 'Cancel', style: 'cancel'},
        {
          text: 'Re-Sync',
          style: 'destructive',
          onPress: async () => {
            await actions.clearImageCache();
            await actions.ensureInitializedAsync({force: true});
          },
        },
      ],
    );
  };

  const handleDeleteAllScores = () => {
    Alert.alert(
      'Warning',
      'This will delete all of your locally saved scores. To retrieve them again, you will have to go through the score retrieval flow above.\n\nOnly use this as a last resort.',
      [
        {text: 'Cancel', style: 'cancel'},
        {
          text: 'OK',
          style: 'destructive',
          onPress: async () => {
            await actions.deleteAllScores();
          },
        },
      ],
    );
  };

  const handleClearEverything = () => {
    Alert.alert(
      'Warning',
      'This will clear all data from the app. You will have to re-sync the song catalog and album images, and manually re-sync your scores.\n\nOnly use this as a last resort.',
      [
        {text: 'Cancel', style: 'cancel'},
        {
          text: 'OK',
          style: 'destructive',
          onPress: async () => {
            await actions.clearEverything();
          },
        },
      ],
    );
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

        {/* ───── App Settings ───── */}
        <FrostedSurface style={styles.card} tint="dark" intensity={18}>
          <Text style={styles.sectionTitle}>App Settings</Text>
          <Text style={styles.sectionHint}>General Festival Score Tracker app settings.</Text>
            <DescriptorToggleRow
              label="Show Instrument Icons"
              description="Display instrument icons on each song row showing which parts have leaderboard scores or FCs."
              checked={!state.settings.songsHideInstrumentIcons}
              onToggle={() => toggleSetting('songsHideInstrumentIcons')}
              first last
            />
        </FrostedSurface>

        {/* ───── Show Instruments ───── */}
        <FrostedSurface style={styles.card} tint="dark" intensity={18}>
          <Text style={styles.sectionTitle}>Show Instruments</Text>
          <Text style={styles.sectionHint}>Choose which instruments to display throughout the app.</Text>
            <ToggleRow label="Lead"     checked={state.settings.showLead}     onToggle={() => toggleSetting('showLead')} first />
            <ToggleRow label="Bass"     checked={state.settings.showBass}     onToggle={() => toggleSetting('showBass')} />
            <ToggleRow label="Drums"    checked={state.settings.showDrums}    onToggle={() => toggleSetting('showDrums')} />
            <ToggleRow label="Vocals"   checked={state.settings.showVocals}   onToggle={() => toggleSetting('showVocals')} />
            <ToggleRow label="Pro Lead" checked={state.settings.showProLead}  onToggle={() => toggleSetting('showProLead')} />
            <ToggleRow label="Pro Bass" checked={state.settings.showProBass}  onToggle={() => toggleSetting('showProBass')} last />
        </FrostedSurface>

        {/* ───── Instrument Query Settings ───── */}
        <FrostedSurface style={styles.card} tint="dark" intensity={18}>
          <Text style={styles.sectionTitle}>Instrument Query Settings</Text>
          <Text style={styles.sectionHint}>Choose which instruments to sync when fetching scores. Removing instruments you don't typically play can improve sync times.</Text>
            <ToggleRow label="Lead"     checked={state.settings.queryLead}     onToggle={() => toggleSetting('queryLead')} first />
            <ToggleRow label="Bass"     checked={state.settings.queryBass}     onToggle={() => toggleSetting('queryBass')} />
            <ToggleRow label="Drums"    checked={state.settings.queryDrums}    onToggle={() => toggleSetting('queryDrums')} />
            <ToggleRow label="Vocals"   checked={state.settings.queryVocals}   onToggle={() => toggleSetting('queryVocals')} />
            <ToggleRow label="Pro Lead" checked={state.settings.queryProLead}  onToggle={() => toggleSetting('queryProLead')} />
            <ToggleRow label="Pro Bass" checked={state.settings.queryProBass}  onToggle={() => toggleSetting('queryProBass')} last />
        </FrostedSurface>

        {/* ───── iOS Settings ───── */}
        {Platform.OS === 'ios' && (
        <FrostedSurface style={styles.card} tint="dark" intensity={18}>
          <Text style={styles.sectionTitle}>iOS Settings</Text>
          <Text style={styles.sectionHint}>Settings specific to iOS.</Text>

            {isLiquidGlassSupported && (
              <>
                <DescriptorToggleRow
                  label="Enable Liquid Glass"
                  description="Use the iOS 26 liquid glass material on surfaces throughout the app. Disable to potentially improve performance on some devices."
                  checked={state.settings.iosLiquidGlassEnabled}
                  onToggle={() => {
                    actions.setSettings({...state.settings, iosLiquidGlassEnabled: !state.settings.iosLiquidGlassEnabled});
                  }}
                  first
                />

                {false && state.settings.iosLiquidGlassEnabled && (
                  <View style={[styles.orderRow, styles.orderRowSeparator, {flexDirection: 'column', alignItems: 'stretch'}]}>
                    <Text style={styles.orderName}>Liquid Glass Style</Text>
                    <Text style={styles.descriptorText}>Controls the glass effect style used on surfaces.</Text>
                    <View style={styles.choiceRow}>
                      <Choice label="None" selected={state.settings.iosLiquidGlassStyle === 'none'} onPress={() => actions.setSettings({...state.settings, iosLiquidGlassStyle: 'none'})} />
                      <Choice label="Regular" selected={state.settings.iosLiquidGlassStyle === 'regular'} onPress={() => actions.setSettings({...state.settings, iosLiquidGlassStyle: 'regular'})} />
                      <Choice label="Clear" selected={state.settings.iosLiquidGlassStyle === 'clear'} onPress={() => actions.setSettings({...state.settings, iosLiquidGlassStyle: 'clear'})} />
                    </View>
                  </View>
                )}
              </>
            )}

            <DescriptorToggleRow
              label="Enable Blur"
              description="Apply a blur effect behind surfaces. Disable to potentially improve performance on some devices."
              checked={state.settings.iosBlurEnabled}
              onToggle={() => {
                actions.setSettings({...state.settings, iosBlurEnabled: !state.settings.iosBlurEnabled});
              }}
              disabled={isLiquidGlassSupported && state.settings.iosLiquidGlassEnabled}
              first={!isLiquidGlassSupported}
              last
            />
        </FrostedSurface>
        )}

        {/* ───── Sync Settings ───── */}
        <FrostedSurface style={styles.card} tint="dark" intensity={18}>
          <Text style={styles.sectionTitle}>Sync Settings</Text>
          <Text style={styles.sectionHint}>Configure how scores are synced.</Text>

            {/* ── Exchange Code ── */}
            <View style={[styles.orderRow, styles.orderRowFirst, {flexDirection: 'column', alignItems: 'stretch'}]}>
              <Text style={styles.orderName}>Exchange Code</Text>
              <Text style={styles.descriptorText}>Paste an exchange code to retrieve your scores from Epic.</Text>
              <FrostedSurface style={[styles.exchangeCodeSurface, {marginTop: 8}]} tint="dark" intensity={18}>
                <TextInput
                  style={styles.exchangeCodeInput}
                  value={state.exchangeCode}
                  placeholder="Paste exchange code"
                  placeholderTextColor="#FFFFFF"
                  autoCapitalize="none"
                  autoCorrect={false}
                  onChangeText={actions.setExchangeCode}
                  returnKeyType="done"
                />
              </FrostedSurface>
              <Pressable
                style={({pressed}) => [styles.buttonSecondary, {marginTop: 8}, pressed && styles.buttonPressed]}
                onPress={() => {
                  Alert.alert(
                    'Warning',
                    'You must already be signed into Epic Games on your default browser to use this. When this link is opened, copy the authorization code and paste it back into this app and this app only.\n\nDo not give this code to anyone else. It enables access to your entire account.\n\nThis code has a short lifespan and must be copied and pasted very quickly. Only proceed if you are aware of all of the above.',
                    [
                      {text: 'Cancel', style: 'cancel'},
                      {text: 'OK', style: 'destructive', onPress: () => void Linking.openURL(generateExchangeCodeUrl)},
                    ],
                  );
                }}>
                <Text style={styles.buttonText}>Generate Code</Text>
              </Pressable>
              <Pressable
                style={({pressed}) => [styles.button, {marginTop: 8}, pressed && styles.buttonPressed, (!state.exchangeCode.trim() || state.isInitializing || state.isFetching) && styles.buttonDisabled]}
                disabled={!state.exchangeCode.trim() || state.isInitializing || state.isFetching}
                onPress={() => void actions.startFetchAsync()}>
                <Text style={styles.buttonText}>{state.isFetching ? 'Fetching…' : 'Retrieve Scores'}</Text>
              </Pressable>
            </View>

            {/* ── Clear Image Cache ── */}
            <View style={[styles.orderRow, styles.orderRowSeparator, {flexDirection: 'column', alignItems: 'stretch'}]}>
              <Text style={styles.orderName}>Clear Image Cache & Re-Sync</Text>
              <Text style={styles.descriptorText}>Clears all cached album art and re-downloads images on next sync.</Text>
              <Pressable
                style={({pressed}) => [styles.buttonPurple, {marginTop: 8}, pressed && styles.buttonPressed, state.isInitializing && styles.buttonDisabled]}
                disabled={state.isInitializing}
                onPress={handleClearImageCache}>
                <Text style={styles.buttonText}>Clear Image Cache & Re-Sync</Text>
              </Pressable>
            </View>

            {/* ── Delete All Scores ── */}
            <View style={[styles.orderRow, styles.orderRowSeparator, {flexDirection: 'column', alignItems: 'stretch'}]}>
              <Text style={styles.orderName}>Delete All Scores</Text>
              <Text style={styles.descriptorText}>Delete your locally saved scores.</Text>
              <Pressable
                style={({pressed}) => [styles.buttonDestructive, {marginTop: 8}, pressed && styles.buttonPressed, state.isInitializing && styles.buttonDisabled]}
                disabled={state.isInitializing}
                onPress={handleDeleteAllScores}>
                <Text style={styles.buttonText}>Delete All Scores</Text>
              </Pressable>
            </View>
        </FrostedSurface>

        {/* ───── Clear Everything ───── */}
        <FrostedSurface style={styles.card} tint="dark" intensity={18}>
          <Text style={styles.sectionTitle}>Clear Everything</Text>
          <Text style={styles.sectionHint}>Resets all app settings, deletes locally saved scores, and clears the image cache.</Text>
            <View style={[styles.orderRow, styles.orderRowFirst, {flexDirection: 'column', alignItems: 'stretch'}]}>
              <Pressable
                style={({pressed}) => [styles.buttonDestructive, pressed && styles.buttonPressed, (state.isInitializing || state.isFetching) && styles.buttonDisabled]}
                disabled={state.isInitializing || state.isFetching}
                onPress={handleClearEverything}>
                <Text style={styles.buttonText}>Clear Everything</Text>
              </Pressable>
            </View>
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
  fadeScrollContainer: { flex: 1 },
  fadeMaskContainer: { flex: 1 },
  fadeMaskOpaque: { flex: 1, backgroundColor: '#000000' },
  fadeGradient: { height: 32 },
  content: { paddingTop: 32, gap: 12 },

  /* ── Card (matches Suggestions / Statistics cards) ── */
  card: { borderRadius: 12, padding: 14, gap: 10 },
  sectionTitle: { color: '#FFFFFF', fontSize: 16, fontWeight: '700' },
  sectionHint: { color: '#D7DEE8', fontSize: 13, lineHeight: 18 },

  /* ── Toggle list rows ── */
  orderRow: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    paddingVertical: 8,
    paddingHorizontal: 12,
  },
  orderRowFirst: { marginTop: 6 },
  orderRowLast: {},
  orderRowSeparator: {},
  orderName: { color: '#FFFFFF', fontSize: 13, fontWeight: '700' },
  rowBtnPressed: { opacity: 0.85 },
  rowDisabled: { opacity: 0.45 },
  descriptorText: { color: '#8899AA', fontSize: 12, lineHeight: 16, marginTop: 2 },
  textDisabled: { color: '#607089' },

  /* ── Choice buttons (matches SortModal) ── */
  choiceRow: { flexDirection: 'row', gap: 8, marginTop: 8 },
  choice: {
    flex: 1,
    paddingHorizontal: 12,
    paddingVertical: 8,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: '#2B3B55',
    alignItems: 'center',
  },
  choiceSelected: {
    borderColor: '#2D82E6',
    backgroundColor: 'rgba(45,130,230,0.18)',
  },
  choiceText: { color: '#D7DEE8', fontSize: 12, fontWeight: '700' },
  choiceTextSelected: { color: '#FFFFFF' },

  /* ── Buttons / inputs (carried over) ── */
  row: { flexDirection: 'row', gap: 10, alignItems: 'center' },
  button: {
    backgroundColor: 'rgba(45,130,230,0.4)',
    paddingVertical: 12,
    paddingHorizontal: 12,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: 'rgba(45,130,230,0.4)',
    alignItems: 'center',
    justifyContent: 'center',
    flexGrow: 1,
  },
  buttonSecondary: {
    backgroundColor: 'rgba(34,48,71,0.6)',
    paddingVertical: 12,
    paddingHorizontal: 12,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: '#2B3B55',
    alignItems: 'center',
    justifyContent: 'center',
  },
  buttonPurple: {
    backgroundColor: 'rgba(124,58,237,0.4)',
    paddingVertical: 12,
    paddingHorizontal: 12,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: 'rgba(124,58,237,0.4)',
    alignItems: 'center',
    justifyContent: 'center',
  },
  buttonDestructive: {
    backgroundColor: 'rgba(198,40,40,0.4)',
    paddingVertical: 12,
    paddingHorizontal: 12,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: 'rgba(198,40,40,0.4)',
    alignItems: 'center',
    justifyContent: 'center',
  },
  buttonPressed: { opacity: 0.85 },
  buttonDisabled: { opacity: 0.5 },
  buttonText: { color: '#FFFFFF', fontWeight: '800' },
  input: {
    borderWidth: 1,
    borderColor: '#2B3B55',
    backgroundColor: '#0B1220',
    borderRadius: 10,
    paddingHorizontal: 12,
    paddingVertical: 10,
    color: '#FFFFFF',
  },
  exchangeCodeSurface: {
    borderRadius: 10,
    borderColor: '#2B3B55',
  },
  exchangeCodeInput: {
    paddingHorizontal: 12,
    paddingVertical: 10,
    color: '#FFFFFF',
  },
});
