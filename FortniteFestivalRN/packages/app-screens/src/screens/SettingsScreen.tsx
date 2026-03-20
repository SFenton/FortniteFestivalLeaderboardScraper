import React, {useCallback, useEffect, useMemo, useState} from 'react';
import {Alert, Linking, Platform, Pressable, ScrollView, StyleSheet, Text, View} from 'react-native';

import {isLiquidGlassSupported} from '@callstack/liquid-glass';
import DraggableFlatList, {type RenderItemParams} from 'react-native-draggable-flatlist';
import Ionicons from 'react-native-vector-icons/Ionicons';

import {Screen, FrostedSurface, FestivalTextInput, PageHeader, HamburgerButton, getInstrumentIconSource, WIN_SCROLLBAR_INSET, FadeScrollView, Colors, buttonStyles, ToggleRow, ChoiceButton, modalStyles, Layout, Gap, Font, LineHeight, Radius, MaxWidth, Opacity} from '@festival/ui';
import {usePageInstrumentation, useFestival, useAuth} from '@festival/contexts';
import {useTabBarLayout} from '../navigation/useOptionalBottomTabBarHeight';
import {useWindowsFlyoutUi} from '../navigation/windowsFlyoutUi';
import {reorderPIOForVisibilityChange, showSettingKeyForInstrument, normalizeSongRowVisualOrder, InstrumentKeys, APP_VERSION, CORE_VERSION, THEME_VERSION} from '@festival/core';
import type {SongRowVisualItem, SongRowVisualKey} from '@festival/core';

/* ────────────────────────── Settings screen ────────────────────────── */

/* ─────────────────────────── Settings screen ───────────────────────────── */

export function SettingsScreen() {
  usePageInstrumentation('Settings');
  const {openFlyout} = useWindowsFlyoutUi();
  const hamburger = Platform.OS === 'windows' ? <HamburgerButton onPress={openFlyout} /> : undefined;
  const {height: tabBarHeight, marginBottom: tabBarMargin} = useTabBarLayout();
  const {state, actions} = useFestival();
  const {auth, authActions} = useAuth();
  const [settingsServiceEndpoint, setSettingsServiceEndpoint] = useState('https://festivalscoretracker.com');
  const [serviceVersion, setServiceVersion] = useState<string | null>(null);

  // Fetch service version when connected
  useEffect(() => {
    const endpoint = auth.serviceEndpoint;
    if (auth.status !== 'authenticated' || !endpoint) {
      setServiceVersion(null);
      return;
    }
    let cancelled = false;
    (async () => {
      try {
        const res = await fetch(`${endpoint.replace(/\/+$/, '')}/api/version`);
        if (!res.ok || cancelled) return;
        const data = await res.json();
        if (!cancelled && data?.version) setServiceVersion(data.version);
      } catch {
        // Service unreachable — leave as null
      }
    })();
    return () => { cancelled = true; };
  }, [auth.status, auth.serviceEndpoint]);

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
        <PageHeader title="Settings" left={hamburger} />

        <FadeScrollView>
          <ScrollView
            style={{flex: 1, marginBottom: tabBarMargin}}
            contentContainerStyle={[styles.content, {paddingBottom: tabBarHeight + 16, paddingRight: WIN_SCROLLBAR_INSET}]}
            scrollIndicatorInsets={{bottom: tabBarHeight}}
            showsVerticalScrollIndicator={false}
            showsHorizontalScrollIndicator={false}
            keyboardShouldPersistTaps="handled"
          >

        {/* ───── App Settings ───── */}
        <FrostedSurface style={styles.card} tint="dark" intensity={18}>
          <Text style={styles.sectionTitle}>App Settings</Text>
          <Text style={styles.sectionHint}>General Festival Score Tracker app settings.</Text>
            <ToggleRow
              label="Show Instrument Icons"
              description="Display instrument icons on each song row showing which parts have leaderboard scores or FCs."
              checked={!state.settings.songsHideInstrumentIcons}
              onToggle={() => toggleSetting('songsHideInstrumentIcons')}
              first last
            />

            {/* ── Song Row Visual Order ── */}
            <View style={{marginTop: 12}}>
              <ToggleRow
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
                <FrostedSurface style={[modalStyles.orderList, styles.orderListExtra]} tint="dark" intensity={12}>
                  {visualOrderItems.map((it, idx) => (
                    <View key={it.key} style={[modalStyles.orderRow, idx === 0 && styles.orderRowFirst, idx === visualOrderItems.length - 1 && styles.orderRowLast, idx > 0 && styles.orderRowSeparator]}>
                      <Text style={modalStyles.orderName}>{it.displayName}</Text>
                      <View style={modalStyles.orderBtns}>
                        <Pressable
                          onPress={() => {
                            if (idx <= 0) return;
                            const next = [...visualOrderKeys];
                            const tmp = next[idx - 1];
                            next[idx - 1] = next[idx];
                            next[idx] = tmp;
                            actions.setSettings({...state.settings, songRowVisualOrder: next});
                          }}
                          style={({pressed}) => [modalStyles.orderBtn, pressed && modalStyles.smallBtnPressed]}
                        >
                          <Text style={modalStyles.orderBtnText}>↑</Text>
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
                          style={({pressed}) => [modalStyles.orderBtn, pressed && modalStyles.smallBtnPressed]}
                        >
                          <Text style={modalStyles.orderBtnText}>↓</Text>
                        </Pressable>
                      </View>
                    </View>
                  ))}
                </FrostedSurface>
              ) : (
                <FrostedSurface style={[modalStyles.orderList, styles.orderListExtra]} tint="dark" intensity={12}>
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
                          style={[modalStyles.orderRow, idx === 0 && styles.orderRowFirst, idx === visualOrderItems.length - 1 && styles.orderRowLast, idx > 0 && styles.orderRowSeparator, isActive && modalStyles.orderRowActive]}
                        >
                          <Text style={modalStyles.orderName}>{item.displayName}</Text>
                          <Ionicons name="menu" size={20} color={Colors.textMuted} />
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
            <View style={[modalStyles.orderRow, styles.orderRowFirst, {flexDirection: 'column', alignItems: 'stretch'}]}>
              <Text style={modalStyles.orderName}>Service Endpoint</Text>
              <Text style={styles.descriptorText}>
                Change only for local testing.
              </Text>
              <FrostedSurface style={[styles.exchangeCodeSurface, {marginTop: 8}]} tint="dark" intensity={18}>
                <FestivalTextInput
                  style={styles.exchangeCodeInput}
                  placeholder="https://festivalscoretracker.com"
                  placeholderTextColor={Colors.textPlaceholder}
                  value={settingsServiceEndpoint}
                  onChangeText={setSettingsServiceEndpoint}
                  autoCapitalize="none"
                  autoCorrect={false}
                />
              </FrostedSurface>
            </View>
            <View style={[modalStyles.orderRow, styles.orderRowSeparator, {flexDirection: 'column', alignItems: 'stretch'}]}>
              <Pressable
                onPress={() => authActions.signInWithService(settingsServiceEndpoint)}
                style={({pressed}) => [buttonStyles.buttonPurple, pressed && buttonStyles.buttonPressed]}>
                <Text style={buttonStyles.buttonText}>Sign In with Epic Games</Text>
              </Pressable>
            </View>
        </FrostedSurface>
        ) : auth.status === 'authenticated' ? (
        <FrostedSurface style={styles.card} tint="dark" intensity={18}>
          <Text style={styles.sectionTitle}>Switch to Local Mode</Text>
          <Text style={styles.sectionHint}>
            Fetch scores directly from Epic with an exchange code. No account required.
          </Text>
            <View style={[modalStyles.orderRow, styles.orderRowFirst, {flexDirection: 'column', alignItems: 'stretch'}]}>
              <Pressable
                onPress={() => authActions.promptLocal()}
                style={({pressed}) => [buttonStyles.buttonSecondary, pressed && buttonStyles.buttonPressed]}>
                <Text style={buttonStyles.buttonText}>Use Local</Text>
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
                <ToggleRow
                  label="Enable Liquid Glass"
                  description="Use the iOS 26 liquid glass material on surfaces throughout the app. Disable to potentially improve performance on some devices."
                  checked={state.settings.iosLiquidGlassEnabled}
                  onToggle={() => {
                    actions.setSettings({...state.settings, iosLiquidGlassEnabled: !state.settings.iosLiquidGlassEnabled});
                  }}
                  first
                />

                {false && state.settings.iosLiquidGlassEnabled && (
                  <View style={[modalStyles.orderRow, styles.orderRowSeparator, {flexDirection: 'column', alignItems: 'stretch'}]}>
                    <Text style={modalStyles.orderName}>Liquid Glass Style</Text>
                    <Text style={styles.descriptorText}>Controls the glass effect style used on surfaces.</Text>
                    <View style={[modalStyles.choiceRow, styles.choiceRowExtra]}>
                      <ChoiceButton label="None" selected={state.settings.iosLiquidGlassStyle === 'none'} onPress={() => actions.setSettings({...state.settings, iosLiquidGlassStyle: 'none'})} />
                      <ChoiceButton label="Regular" selected={state.settings.iosLiquidGlassStyle === 'regular'} onPress={() => actions.setSettings({...state.settings, iosLiquidGlassStyle: 'regular'})} />
                      <ChoiceButton label="Clear" selected={state.settings.iosLiquidGlassStyle === 'clear'} onPress={() => actions.setSettings({...state.settings, iosLiquidGlassStyle: 'clear'})} />
                    </View>
                  </View>
                )}
              </>
            )}

            <ToggleRow
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
            <View style={[modalStyles.orderRow, styles.orderRowFirst, {flexDirection: 'column', alignItems: 'stretch'}]}>
              <Text style={modalStyles.orderName}>Exchange Code</Text>
              <Text style={styles.descriptorText}>Paste an exchange code to retrieve your scores from Epic.</Text>
              <FrostedSurface style={[styles.exchangeCodeSurface, {marginTop: 8}]} tint="dark" intensity={18}>
                <FestivalTextInput
                  style={styles.exchangeCodeInput}
                  value={state.exchangeCode}
                  placeholder="Paste exchange code"
                  placeholderTextColor={Colors.textPrimary}
                  autoCapitalize="none"
                  autoCorrect={false}
                  onChangeText={actions.setExchangeCode}
                  returnKeyType="done"
                />
              </FrostedSurface>
              <Pressable
                style={({pressed}) => [buttonStyles.buttonSecondary, {marginTop: 8}, pressed && buttonStyles.buttonPressed]}
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
                <Text style={buttonStyles.buttonText}>Generate Code</Text>
              </Pressable>
              <Pressable
                style={({pressed}) => [buttonStyles.button, {marginTop: 8}, pressed && buttonStyles.buttonPressed, (!state.exchangeCode.trim() || state.isInitializing || state.isFetching) && buttonStyles.buttonDisabled]}
                disabled={!state.exchangeCode.trim() || state.isInitializing || state.isFetching}
                onPress={() => void actions.startFetchAsync()}>
                <Text style={buttonStyles.buttonText}>{state.isFetching ? 'Fetching…' : 'Retrieve Scores'}</Text>
              </Pressable>
            </View>

            {/* ── Clear Image Cache ── */}
            <View style={[modalStyles.orderRow, styles.orderRowSeparator, {flexDirection: 'column', alignItems: 'stretch'}]}>
              <Text style={modalStyles.orderName}>Clear Image Cache & Re-Sync</Text>
              <Text style={styles.descriptorText}>Clears all cached album art and re-downloads images on next sync.</Text>
              <Pressable
                style={({pressed}) => [buttonStyles.buttonPurple, {marginTop: 8}, pressed && buttonStyles.buttonPressed, state.isInitializing && buttonStyles.buttonDisabled]}
                disabled={state.isInitializing}
                onPress={handleClearImageCache}>
                <Text style={buttonStyles.buttonText}>Clear Image Cache & Re-Sync</Text>
              </Pressable>
            </View>

            {/* ── Delete All Scores ── */}
            <View style={[modalStyles.orderRow, styles.orderRowSeparator, {flexDirection: 'column', alignItems: 'stretch'}]}>
              <Text style={modalStyles.orderName}>Delete All Scores</Text>
              <Text style={styles.descriptorText}>Delete your locally saved scores.</Text>
              <Pressable
                style={({pressed}) => [buttonStyles.buttonDestructive, {marginTop: 8}, pressed && buttonStyles.buttonPressed, state.isInitializing && buttonStyles.buttonDisabled]}
                disabled={state.isInitializing}
                onPress={handleDeleteAllScores}>
                <Text style={buttonStyles.buttonText}>Delete All Scores</Text>
              </Pressable>
            </View>
        </FrostedSurface>

        {/* ───── Festival Score Tracker Version ───── */}
        <FrostedSurface style={styles.card} tint="dark" intensity={18}>
          <Text style={styles.sectionTitle}>Festival Score Tracker Version</Text>
          <Text style={styles.sectionHint}>Festival Score Tracker information to help with debugging.</Text>

            <View style={[modalStyles.orderRow, styles.orderRowFirst, {flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center'}]}>
              <Text style={modalStyles.orderName}>App Version</Text>
              <Text style={styles.versionValue}>{APP_VERSION}</Text>
            </View>

            <View style={[modalStyles.orderRow, styles.orderRowSeparator, {flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center'}]}>
              <Text style={modalStyles.orderName}>Service Version</Text>
              <Text style={styles.versionValue}>{serviceVersion ?? (auth.status === 'authenticated' ? 'Loading…' : 'Not connected')}</Text>
            </View>

            <View style={[modalStyles.orderRow, styles.orderRowSeparator, {flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center'}]}>
              <Text style={modalStyles.orderName}>@festival/core Version</Text>
              <Text style={styles.versionValue}>{CORE_VERSION}</Text>
            </View>

            <View style={[modalStyles.orderRow, styles.orderRowSeparator, {flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center'}]}>
              <Text style={modalStyles.orderName}>@festival/theme Version</Text>
              <Text style={styles.versionValue}>{THEME_VERSION}</Text>
            </View>
        </FrostedSurface>

        {/* ───── Clear Everything ───── */}
        <FrostedSurface style={styles.card} tint="dark" intensity={18}>
          <Text style={styles.sectionTitle}>Clear Everything</Text>
          <Text style={styles.sectionHint}>Resets all app settings, deletes locally saved scores, and clears the image cache.</Text>
            <View style={[modalStyles.orderRow, styles.orderRowFirst, {flexDirection: 'column', alignItems: 'stretch'}]}>
              <Pressable
                style={({pressed}) => [buttonStyles.buttonDestructive, pressed && buttonStyles.buttonPressed, (state.isInitializing || state.isFetching) && buttonStyles.buttonDisabled]}
                disabled={state.isInitializing || state.isFetching}
                onPress={handleClearEverything}>
                <Text style={buttonStyles.buttonText}>Clear Everything</Text>
              </Pressable>
            </View>
        </FrostedSurface>


      </ScrollView>
        </FadeScrollView>
      </View>
    </Screen>
  );
}

const styles = StyleSheet.create({
  wrapper: {
    flex: 1,
    paddingHorizontal: Layout.paddingHorizontal,
    paddingTop: Layout.paddingTop,
    paddingBottom: Layout.paddingBottom,
    gap: Gap.xl,
  },

  content: { paddingTop: Layout.fadeHeight, gap: Gap.xl },

  /* ── Card (matches Suggestions / Statistics cards) ── */
  card: { borderRadius: Radius.md, padding: 14, gap: Gap.lg, maxWidth: MaxWidth.narrow, width: '100%', alignSelf: 'center' },
  sectionTitle: { color: Colors.textPrimary, fontSize: Font.lg, fontWeight: '700' },
  innerSectionTitle: { color: Colors.textPrimary, fontSize: Font.md, fontWeight: '700', marginBottom: Gap.sm },
  sectionHint: { color: Colors.textSecondary, fontSize: Font.md, lineHeight: LineHeight.md },

  /* ── Toggle list rows (overrides for Settings-specific empty/minimal styles) ── */
  orderRowFirst: { marginTop: 6 },
  orderRowLast: {},
  orderRowSeparator: {},
  descriptorText: { color: Colors.textMuted, fontSize: Font.sm, lineHeight: LineHeight.sm, marginTop: Gap.xs },
  versionValue: { color: Colors.textSecondary, fontSize: Font.md },

  /* ── Extras added on top of shared modalStyles ── */
  orderListExtra: { marginTop: Gap.md },
  choiceRowExtra: { marginTop: Gap.md },

  /* ── Buttons / inputs (carried over) ── */
  row: { flexDirection: 'row', gap: Gap.lg, alignItems: 'center' },
  input: {
    borderWidth: 1,
    borderColor: Colors.borderPrimary,
    backgroundColor: Colors.backgroundCard,
    borderRadius: Radius.sm,
    paddingHorizontal: Gap.xl,
    paddingVertical: Gap.lg,
    color: Colors.textPrimary,
  },
  exchangeCodeSurface: {
    borderRadius: Radius.sm,
    borderColor: Colors.borderPrimary,
  },
  exchangeCodeInput: {
    paddingHorizontal: Gap.xl,
    paddingVertical: Gap.lg,
    color: Colors.textPrimary,
  },
});
