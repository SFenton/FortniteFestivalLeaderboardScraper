import React, {useCallback, useMemo, useState} from 'react';
import {Alert, Image, type ImageSourcePropType, Linking, Platform, Pressable, ScrollView, StyleSheet, Switch, Text, TextInput, View} from 'react-native';
import MaskedView from '@react-native-masked-view/masked-view';
import LinearGradient from 'react-native-linear-gradient';
import {isLiquidGlassSupported} from '@callstack/liquid-glass';
import DraggableFlatList, {type RenderItemParams} from 'react-native-draggable-flatlist';
import Ionicons from 'react-native-vector-icons/Ionicons';

import { Screen } from '@festival/ui/Screen';
import {usePageInstrumentation} from '@festival/contexts';
import {useFestival} from '@festival/contexts';
import {FrostedSurface} from '@festival/ui/FrostedSurface';
import {useTabBarLayout} from '../navigation/useOptionalBottomTabBarHeight';
import {PageHeader} from '@festival/ui/PageHeader';
import {reorderPIOForVisibilityChange, showSettingKeyForInstrument, normalizeSongRowVisualOrder} from '@festival/core';
import type {SongRowVisualItem, SongRowVisualKey} from '@festival/core';
import {InstrumentKeys} from '@festival/core';
import {getInstrumentIconSource} from '@festival/ui/instruments/instrumentVisuals';
import {useAuth} from '@festival/contexts';

/* ────────────────────────── Toggle row (reused) ────────────────────────── */

function ToggleRow(props: {label: string; icon?: ImageSourcePropType; checked: boolean; onToggle: () => void; disabled?: boolean; first?: boolean; last?: boolean}) {
  return (
    <Pressable
      onPress={props.disabled ? undefined : props.onToggle}
      disabled={props.disabled}
      style={({pressed}) => [
        styles.orderRow,
        props.first && styles.orderRowFirst,
        props.last && styles.orderRowLast,
        !props.first && styles.orderRowSeparator,
        pressed && !props.disabled && styles.rowBtnPressed,
        props.disabled && styles.rowDisabled,
      ]}
      accessibilityRole="switch"
    >
      <View style={styles.toggleLabelRow}>
        {props.icon && <Image source={props.icon} style={styles.toggleInstrumentIcon} resizeMode="contain" />}
        <Text style={[styles.orderName, props.disabled && styles.textDisabled]}>{props.label}</Text>
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
  const {auth, authActions} = useAuth();
  const [settingsServiceEndpoint, setSettingsServiceEndpoint] = useState('');
  const [settingsEpicUsername, setSettingsEpicUsername] = useState('');

  const generateExchangeCodeUrl =
    'https://www.epicgames.com/id/api/redirect?clientId=ec684b8c687f479fadea3cb2ad83f5c6&responseType=code';

  /* ── instrument toggles (same backend values as filter modal) ── */

  const showActiveCount = [state.settings.showLead, state.settings.showBass, state.settings.showDrums, state.settings.showVocals, state.settings.showProLead, state.settings.showProBass].filter(Boolean).length;
  const queryActiveCount = [state.settings.queryLead, state.settings.queryBass, state.settings.queryDrums, state.settings.queryVocals, state.settings.queryProLead, state.settings.queryProBass].filter(Boolean).length;

  const visualOrderItems = useMemo(() => normalizeSongRowVisualOrder(state.settings.songRowVisualOrder), [state.settings.songRowVisualOrder]);
  const visualOrderKeys = useMemo(() => visualOrderItems.map(i => i.key), [visualOrderItems]);

  const toggleSetting = useCallback(
    (key: 'queryLead' | 'queryBass' | 'queryDrums' | 'queryVocals' | 'queryProLead' | 'queryProBass' | 'showLead' | 'showBass' | 'showDrums' | 'showVocals' | 'showProLead' | 'showProBass' | 'songsHideInstrumentIcons' | 'metadataShowScore' | 'metadataShowPercentage' | 'metadataShowPercentile' | 'metadataShowSeasonAchieved' | 'metadataShowDifficulty' | 'metadataShowIsFC' | 'metadataShowStars') => {
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

            {/* ── Song Row Visual Order ── */}
            <View style={{marginTop: 12}}>
              <DescriptorToggleRow
                label="Enable Independent Song Row Visual Order"
                description="When enabled, the metadata display order on song rows is controlled separately from sort priority below. When disabled, metadata is displayed in sort priority order."
                checked={state.settings.songRowVisualOrderEnabled}
                onToggle={() => actions.setSettings({...state.settings, songRowVisualOrderEnabled: !state.settings.songRowVisualOrderEnabled})}
                first last
              />
            </View>

            {state.settings.songRowVisualOrderEnabled && (
            <View style={{marginTop: 12}}>
              <Text style={styles.innerSectionTitle}>Song Row Visual Order</Text>
              <Text style={styles.sectionHint}>
                When filtering to a single instrument in the song list, extra metadata is displayed. Choose the order it appears in on the bottom row.{'\n\n'}Note: if you sort by a piece of metadata, that metadata will appear in the row above. This does not impact sort order, just the visual order of the data you see.
              </Text>

              {Platform.OS === 'windows' ? (
                <FrostedSurface style={styles.orderList} tint="dark" intensity={12}>
                  {visualOrderItems.map((it, idx) => (
                    <View key={it.key} style={[styles.orderRow, idx === 0 && styles.orderRowFirst, idx === visualOrderItems.length - 1 && styles.orderRowLast, idx > 0 && styles.orderRowSeparator]}>
                      <Text style={styles.orderName}>{it.displayName}</Text>
                      <View style={styles.orderBtns}>
                        <Pressable
                          onPress={() => {
                            if (idx <= 0) return;
                            const next = [...visualOrderKeys];
                            const tmp = next[idx - 1];
                            next[idx - 1] = next[idx];
                            next[idx] = tmp;
                            actions.setSettings({...state.settings, songRowVisualOrder: next});
                          }}
                          style={({pressed}) => [styles.orderBtn, pressed && styles.smallBtnPressed]}
                        >
                          <Text style={styles.orderBtnText}>↑</Text>
                        </Pressable>
                        <Pressable
                          onPress={() => {
                            if (idx >= visualOrderKeys.length - 1) return;
                            const next = [...visualOrderKeys];
                            const tmp = next[idx + 1];
                            next[idx + 1] = next[idx];
                            next[idx] = tmp;
                            actions.setSettings({...state.settings, songRowVisualOrder: next});
                          }}
                          style={({pressed}) => [styles.orderBtn, pressed && styles.smallBtnPressed]}
                        >
                          <Text style={styles.orderBtnText}>↓</Text>
                        </Pressable>
                      </View>
                    </View>
                  ))}
                </FrostedSurface>
              ) : (
                <FrostedSurface style={styles.orderList} tint="dark" intensity={12}>
                  <DraggableFlatList<SongRowVisualItem>
                    data={visualOrderItems}
                    keyExtractor={(item) => item.key}
                    scrollEnabled={false}
                    onDragEnd={({data}) => {
                      actions.setSettings({...state.settings, songRowVisualOrder: data.map(i => i.key)});
                    }}
                    renderItem={({item, drag, isActive, getIndex}: RenderItemParams<SongRowVisualItem>) => {
                      const idx = getIndex() ?? 0;
                      return (
                        <Pressable
                          onLongPress={drag}
                          delayLongPress={100}
                          disabled={isActive}
                          style={[styles.orderRow, idx === 0 && styles.orderRowFirst, idx === visualOrderItems.length - 1 && styles.orderRowLast, idx > 0 && styles.orderRowSeparator, isActive && styles.orderRowActive]}
                        >
                          <Text style={styles.orderName}>{item.displayName}</Text>
                          <Ionicons name="menu" size={20} color="#8899AA" />
                        </Pressable>
                      );
                    }}
                  />
                </FrostedSurface>
              )}
            </View>
            )}
        </FrostedSurface>

        {/* ───── Show Instruments ───── */}
        <FrostedSurface style={styles.card} tint="dark" intensity={18}>
          <Text style={styles.sectionTitle}>Show Instruments</Text>
          <Text style={styles.sectionHint}>Choose which instruments to display throughout the app.</Text>
            <ToggleRow label="Lead"     icon={getInstrumentIconSource('guitar')}     checked={state.settings.showLead}     onToggle={() => toggleSetting('showLead')} disabled={state.settings.showLead && showActiveCount <= 1} first />
            <ToggleRow label="Bass"     icon={getInstrumentIconSource('bass')}       checked={state.settings.showBass}     onToggle={() => toggleSetting('showBass')} disabled={state.settings.showBass && showActiveCount <= 1} />
            <ToggleRow label="Drums"    icon={getInstrumentIconSource('drums')}      checked={state.settings.showDrums}    onToggle={() => toggleSetting('showDrums')} disabled={state.settings.showDrums && showActiveCount <= 1} />
            <ToggleRow label="Vocals"   icon={getInstrumentIconSource('vocals')}     checked={state.settings.showVocals}   onToggle={() => toggleSetting('showVocals')} disabled={state.settings.showVocals && showActiveCount <= 1} />
            <ToggleRow label="Pro Lead" icon={getInstrumentIconSource('pro_guitar')} checked={state.settings.showProLead}  onToggle={() => toggleSetting('showProLead')} disabled={state.settings.showProLead && showActiveCount <= 1} />
            <ToggleRow label="Pro Bass" icon={getInstrumentIconSource('pro_bass')}   checked={state.settings.showProBass}  onToggle={() => toggleSetting('showProBass')} disabled={state.settings.showProBass && showActiveCount <= 1} last />
        </FrostedSurface>

        {/* ───── Show Instrument Metadata ───── */}
        <FrostedSurface style={styles.card} tint="dark" intensity={18}>
          <Text style={styles.sectionTitle}>Show Instrument Metadata</Text>
          <Text style={styles.sectionHint}>When filtering songs down to one instrument in the song list, extra metadata for that song can appear. Choose what you'd like to see in the song row here.</Text>
            <ToggleRow label="Score"           checked={state.settings.metadataShowScore}           onToggle={() => toggleSetting('metadataShowScore')} first />
            <ToggleRow label="Percentage"       checked={state.settings.metadataShowPercentage}      onToggle={() => toggleSetting('metadataShowPercentage')} />
            <ToggleRow label="Percentile"       checked={state.settings.metadataShowPercentile}      onToggle={() => toggleSetting('metadataShowPercentile')} />
            <ToggleRow label="Season Achieved"  checked={state.settings.metadataShowSeasonAchieved}  onToggle={() => toggleSetting('metadataShowSeasonAchieved')} />
            <ToggleRow label="Song Intensity"   checked={state.settings.metadataShowDifficulty}      onToggle={() => toggleSetting('metadataShowDifficulty')} />
            <ToggleRow label="Is FC"            checked={state.settings.metadataShowIsFC}            onToggle={() => toggleSetting('metadataShowIsFC')} />
            <ToggleRow label="Stars"            checked={state.settings.metadataShowStars}           onToggle={() => toggleSetting('metadataShowStars')} last />
        </FrostedSurface>

        {/* ───── Instrument Query Settings ───── */}
        <FrostedSurface style={styles.card} tint="dark" intensity={18}>
          <Text style={styles.sectionTitle}>Instrument Query Settings</Text>
          <Text style={styles.sectionHint}>Choose which instruments to sync when fetching scores. Removing instruments you don't typically play can improve sync times.</Text>
            <ToggleRow label="Lead"     icon={getInstrumentIconSource('guitar')}     checked={state.settings.queryLead}     onToggle={() => toggleSetting('queryLead')} disabled={state.settings.queryLead && queryActiveCount <= 1} first />
            <ToggleRow label="Bass"     icon={getInstrumentIconSource('bass')}       checked={state.settings.queryBass}     onToggle={() => toggleSetting('queryBass')} disabled={state.settings.queryBass && queryActiveCount <= 1} />
            <ToggleRow label="Drums"    icon={getInstrumentIconSource('drums')}      checked={state.settings.queryDrums}    onToggle={() => toggleSetting('queryDrums')} disabled={state.settings.queryDrums && queryActiveCount <= 1} />
            <ToggleRow label="Vocals"   icon={getInstrumentIconSource('vocals')}     checked={state.settings.queryVocals}   onToggle={() => toggleSetting('queryVocals')} disabled={state.settings.queryVocals && queryActiveCount <= 1} />
            <ToggleRow label="Pro Lead" icon={getInstrumentIconSource('pro_guitar')} checked={state.settings.queryProLead}  onToggle={() => toggleSetting('queryProLead')} disabled={state.settings.queryProLead && queryActiveCount <= 1} />
            <ToggleRow label="Pro Bass" icon={getInstrumentIconSource('pro_bass')}   checked={state.settings.queryProBass}  onToggle={() => toggleSetting('queryProBass')} disabled={state.settings.queryProBass && queryActiveCount <= 1} last />
        </FrostedSurface>

        {/* ───── Connect / Switch Mode ───── */}
        {auth.status === 'local' ? (
        <FrostedSurface style={styles.card} tint="dark" intensity={18}>
          <Text style={styles.sectionTitle}>Connect to Festival Score Tracker</Text>
          <Text style={styles.sectionHint}>
            Sync your scores automatically, see friends, rankings, score history, and more.
          </Text>
            <View style={[styles.orderRow, styles.orderRowFirst, {flexDirection: 'column', alignItems: 'stretch'}]}>
              <Text style={styles.orderName}>Service Endpoint</Text>
              <Text style={styles.descriptorText}>
                Enter the endpoint of the Festival Score Tracker service you want to connect to.
              </Text>
              <FrostedSurface style={[styles.exchangeCodeSurface, {marginTop: 8}]} tint="dark" intensity={18}>
                <TextInput
                  style={styles.exchangeCodeInput}
                  placeholder="https://example.com"
                  placeholderTextColor="rgba(255,255,255,0.4)"
                  value={settingsServiceEndpoint}
                  onChangeText={setSettingsServiceEndpoint}
                  autoCapitalize="none"
                  autoCorrect={false}
                />
              </FrostedSurface>
            </View>
            <View style={[styles.orderRow, styles.orderRowSeparator, {flexDirection: 'column', alignItems: 'stretch'}]}>
              <Text style={styles.orderName}>Epic Games Username</Text>
              <Text style={styles.descriptorText}>
                Enter your Epic Games username here.
              </Text>
              <FrostedSurface style={[styles.exchangeCodeSurface, {marginTop: 8}]} tint="dark" intensity={18}>
                <TextInput
                  style={styles.exchangeCodeInput}
                  placeholder="Username"
                  placeholderTextColor="rgba(255,255,255,0.4)"
                  value={settingsEpicUsername}
                  onChangeText={setSettingsEpicUsername}
                  autoCapitalize="none"
                  autoCorrect={false}
                />
              </FrostedSurface>
            </View>
            <View style={[styles.orderRow, styles.orderRowSeparator, {flexDirection: 'column', alignItems: 'stretch'}]}>
              <Pressable
                onPress={() => authActions.signInWithService(settingsServiceEndpoint, settingsEpicUsername)}
                style={({pressed}) => [styles.buttonPurple, pressed && styles.buttonPressed]}>
                <Text style={styles.buttonText}>Sign In</Text>
              </Pressable>
            </View>
        </FrostedSurface>
        ) : auth.status === 'authenticated' ? (
        <FrostedSurface style={styles.card} tint="dark" intensity={18}>
          <Text style={styles.sectionTitle}>Switch to Local Mode</Text>
          <Text style={styles.sectionHint}>
            Fetch scores directly from Epic with an exchange code. No account required.
          </Text>
            <View style={[styles.orderRow, styles.orderRowFirst, {flexDirection: 'column', alignItems: 'stretch'}]}>
              <Pressable
                onPress={() => authActions.promptLocal()}
                style={({pressed}) => [styles.buttonSecondary, pressed && styles.buttonPressed]}>
                <Text style={styles.buttonText}>Use Local</Text>
              </Pressable>
            </View>
        </FrostedSurface>
        ) : null}

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
  card: { borderRadius: 12, padding: 14, gap: 10, maxWidth: 600, width: '100%', alignSelf: 'center' },
  sectionTitle: { color: '#FFFFFF', fontSize: 16, fontWeight: '700' },
  innerSectionTitle: { color: '#FFFFFF', fontSize: 14, fontWeight: '700', marginBottom: 4 },
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
  toggleLabelRow: { flexDirection: 'row', alignItems: 'center', gap: 8 },
  toggleInstrumentIcon: { width: 36, height: 36, marginRight: 8 },
  rowBtnPressed: { opacity: 0.85 },
  rowDisabled: { opacity: 0.45 },
  descriptorText: { color: '#8899AA', fontSize: 12, lineHeight: 16, marginTop: 2 },
  textDisabled: { color: '#607089' },

  /* ── Reorderable list (mirrors SortModal pattern) ── */
  orderList: {
    borderRadius: 12,
    borderWidth: 1,
    borderColor: '#263244',
    overflow: 'hidden',
    marginTop: 8,
  },
  orderRowActive: {
    backgroundColor: '#1A2940',
    borderRadius: 12,
    transform: [{scale: 1.03}],
    shadowColor: '#000',
    shadowOffset: {width: 0, height: 4},
    shadowOpacity: 0.3,
    shadowRadius: 8,
    elevation: 8,
  },
  orderBtns: {
    flexDirection: 'row',
    gap: 8,
  },
  orderBtn: {
    width: 34,
    height: 34,
    borderRadius: 10,
    borderWidth: 1,
    borderColor: '#2B3B55',
    alignItems: 'center',
    justifyContent: 'center',
  },
  orderBtnText: {
    color: '#FFFFFF',
    fontSize: 16,
    fontWeight: '900',
  },
  smallBtnPressed: {
    opacity: 0.85,
  },

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
