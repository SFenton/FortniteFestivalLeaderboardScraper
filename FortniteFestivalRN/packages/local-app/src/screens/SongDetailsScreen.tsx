import React, {useEffect, useMemo, useState} from 'react';
import {Image, Platform, Pressable, ScrollView, StyleSheet, Text, View, useWindowDimensions} from 'react-native';
import type {NativeStackScreenProps} from '@react-navigation/native-stack';
import {useSafeAreaInsets} from 'react-native-safe-area-context';
import Ionicons from 'react-native-vector-icons/Ionicons';
import MaskedView from '@react-native-masked-view/masked-view';
import LinearGradient from 'react-native-linear-gradient';

import {Screen, FrostedSurface, PageHeader, InstrumentCard, useCardGrid, WIN_SCROLLBAR_INSET, gridStyles, Colors, Layout, Gap, Font, LineHeight, Radius, Opacity, MaxWidth} from '@festival/ui';
import {usePageInstrumentation, useFestival} from '@festival/contexts';
import type {InstrumentKey} from '@festival/core';
import {normalizeInstrumentOrder, buildSongInfoInstrumentRows} from '@festival/core';

export type SongsStackParamList = {
  SongsList: undefined;
  SongDetails: {songId: string};
};

export type SongDetailsScreenProps = NativeStackScreenProps<SongsStackParamList, 'SongDetails'>;

export function SongDetailsView(props: {songId: string; showBack?: boolean; onBack?: () => void}) {
  const songId = props.songId;
  usePageInstrumentation(`Song:${songId}`);

  const insets = useSafeAreaInsets();

  const {width} = useWindowDimensions();
  const [rootMeasuredWidth, setRootMeasuredWidth] = useState<number | null>(null);
  const effectiveWidth = rootMeasuredWidth && rootMeasuredWidth > 0 ? rootMeasuredWidth : width;
  const isCardGrid = useCardGrid(effectiveWidth);

  const {
    state: {songs, scoresIndex, settings},
  } = useFestival();

  const song = useMemo(() => songs.find(s => s.track.su === songId), [songId, songs]);

  const title = song?.track.tt ?? song?._title ?? songId;
  const artist = song?.track.an ?? '';
  const year = song?.track.ry;
  const imageUri = song?.imagePath ?? song?.track.au;

  const enabledInstrumentOrder = useMemo(() => {
    const base = normalizeInstrumentOrder(settings?.songsPrimaryInstrumentOrder).map(x => x.key);
    const isEnabled = (key: InstrumentKey): boolean => {
      if (!settings) return true;
      switch (key) {
        case 'guitar':
          return settings.showLead;
        case 'drums':
          return settings.showDrums;
        case 'vocals':
          return settings.showVocals;
        case 'bass':
          return settings.showBass;
        case 'pro_guitar':
          return settings.showProLead;
        case 'pro_bass':
          return settings.showProBass;
        default:
          return true;
      }
    };

    return base.filter(isEnabled);
  }, [settings]);

  const instrumentRows = useMemo(() => {
    if (!song) return [];
    return buildSongInfoInstrumentRows({song, instrumentOrder: enabledInstrumentOrder, scoresIndex});
  }, [enabledInstrumentOrder, scoresIndex, song]);

  const hasAnyCachedScoreForSong = useMemo(() => instrumentRows.some(r => r.hasScore), [instrumentRows]);

  const notice = useMemo(() => {
    if (!song) {
      return {
        title: 'Song data not loaded',
        body: 'If this persists, re-sync songs from Settings.',
      };
    }

    if (enabledInstrumentOrder.length === 0) {
      return {
        title: 'No instruments enabled',
        body: 'Enable at least one instrument in Settings to see rows here.',
      };
    }

    if (!hasAnyCachedScoreForSong) {
      return {
        title: 'No cached scores yet',
        body: 'Paste an exchange code in Settings and tap Retrieve Scores to populate this table.',
      };
    }

    return null;
  }, [enabledInstrumentOrder.length, hasAnyCachedScoreForSong, song]);

  useEffect(() => {
    if (!__DEV__) return;
    // eslint-disable-next-line no-console
    console.warn('[SONG_UI] mounted', {platform: Platform.OS, songId, dev: __DEV__});
  }, [songId]);

  return (
    <Screen style={styles.screen} withSafeArea={false}>
      <View
        style={styles.root}
        onLayout={e => {
          const next = e.nativeEvent.layout.width;
          setRootMeasuredWidth(cur => {
            if (cur == null) return next;
            return Math.abs(cur - next) >= 1 ? next : cur;
          });
        }}
      >

        <View pointerEvents="none" style={styles.bgBase} />
        {imageUri ? (
          <Image
            source={{uri: imageUri}}
            style={styles.bgImage}
            resizeMode="cover"
          />
        ) : null}
        <View pointerEvents="none" style={styles.bgDim} />

        <View style={[styles.content, {paddingTop: insets.top, paddingBottom: insets.bottom, paddingLeft: insets.left, paddingRight: insets.right}]}>

          {props.showBack && props.onBack ? (
            <View style={styles.stickyHeader}>
              <PageHeader
                left={
                  <Pressable
                    onPress={props.onBack}
                    hitSlop={12}
                    style={({pressed}) => [styles.backButton, pressed && styles.backButtonPressed]}
                    accessibilityRole="button"
                    accessibilityLabel="Go back"
                  >
                    <Ionicons
                      name="chevron-back"
                      size={Platform.OS === 'ios' ? 30 : 26}
                      color="#FFFFFF"
                      style={styles.backIcon}
                    />
                    <Text style={styles.backLabel}>Back</Text>
                  </Pressable>
                }
              />
            </View>
          ) : null}

          <MaskedView
            style={styles.scrollContainer}
            maskElement={
              <View style={styles.maskContainer}>
                <LinearGradient
                  colors={['transparent', 'black']}
                  style={styles.fadeGradient}
                />
                <View style={styles.maskOpaque} />
                <LinearGradient
                  colors={['black', 'transparent']}
                  style={styles.fadeGradient}
                />
              </View>
            }
          >
          <ScrollView
            style={styles.scroll}
            contentContainerStyle={[
              styles.scrollContent,
              props.showBack && props.onBack ? styles.scrollContentBelowStickyHeader : null,
              WIN_SCROLLBAR_INSET ? {paddingRight: 16 + WIN_SCROLLBAR_INSET} : null,
            ]}
            showsVerticalScrollIndicator={false}
            showsHorizontalScrollIndicator={false}
          >
          <FrostedSurface
            style={[styles.headerCard, isCardGrid ? styles.headerCardWide : styles.headerCardCompact]}
            tint="dark"
            intensity={22}
            fallbackColor={Colors.surfaceFrosted}
          >
            <View style={[styles.header, isCardGrid ? styles.headerWide : styles.headerCompact]}>
              <View style={styles.albumWrapOuter}>
                <View style={styles.albumWrap}>
                  {imageUri ? (
                    <Image source={{uri: imageUri}} style={styles.albumImage} resizeMode="contain" />
                  ) : (
                    <View style={styles.albumPlaceholder} />
                  )}
                </View>
              </View>

              <View style={[styles.headerTextWrap, isCardGrid && {width: 320}]}>
                <Text style={styles.songTitle} numberOfLines={3}>
                  {title}
                </Text>
                <Text style={styles.songSubTitle} numberOfLines={3}>
                  {artist}
                  {artist && year ? ' · ' : ''}
                  {year ?? ''}
                </Text>
              </View>
            </View>
          </FrostedSurface>

          {notice ? (
            <FrostedSurface
              style={styles.noticeCard}
              tint="dark"
              intensity={22}
              fallbackColor={Colors.surfaceFrosted}
            >
              <Text style={styles.noticeTitle}>{notice.title}</Text>
              <Text style={styles.noticeBody}>{notice.body}</Text>
            </FrostedSurface>
          ) : null}

          {isCardGrid ? (
            <View style={gridStyles.cardGrid}>
              <View style={gridStyles.cardGridColumnLeft}>
                {instrumentRows.filter((_, i) => i % 2 === 0).map(r => (
                  <InstrumentCard key={r.key} data={r} />
                ))}
              </View>
              <View style={gridStyles.cardGridColumnRight}>
                {instrumentRows.filter((_, i) => i % 2 !== 0).map(r => (
                  <InstrumentCard key={r.key} data={r} />
                ))}
              </View>
            </View>
          ) : (
            instrumentRows.map(r => (
              <InstrumentCard key={r.key} data={r} />
            ))
          )}

          </ScrollView>
          </MaskedView>
        </View>
      </View>

    </Screen>
  );
}

export function SongDetailsScreen(props: SongDetailsScreenProps) {
  return (
    <SongDetailsView
      songId={props.route.params.songId}
      showBack
      onBack={() => props.navigation.goBack()}
    />
  );
}



const FADE_HEIGHT = Layout.fadeHeight;

const styles = StyleSheet.create({
  screen: {
    backgroundColor: Colors.transparent,
  },
  root: {
    flex: 1,
  },
  content: {
    flex: 1,
  },
  bgBase: {
    ...StyleSheet.absoluteFillObject,
    backgroundColor: Colors.backgroundApp,
  },
  bgImage: {
    ...StyleSheet.absoluteFillObject,
    opacity: 0.9,
  },
  bgDim: {
    ...StyleSheet.absoluteFillObject,
    backgroundColor: Colors.overlayDark,
  },
  stickyHeader: {
    zIndex: 10,
    paddingHorizontal: Layout.paddingHorizontal,
    paddingTop: Layout.paddingTop,
    paddingBottom: 0,
  },
  backButton: {
    alignSelf: 'flex-start',
    flexDirection: 'row',
    alignItems: 'center',
    gap: 6,
    paddingVertical: 6,
    paddingHorizontal: 0,
  },
  backIcon: {
    // Ionicons chevrons have built-in glyph padding; compensate so the left edge
    // aligns with PageHeader titles on other screens.
    marginLeft: -6,
    marginRight: -2,
  },
  backButtonPressed: {
    opacity: 0.7,
  },
  scrollContainer: {
    flex: 1,
  },
  maskContainer: {
    flex: 1,
  },
  maskOpaque: {
    flex: 1,
    backgroundColor: Colors.backgroundBlack,
  },
  fadeGradient: {
    height: FADE_HEIGHT,
  },
  scroll: {
    flex: 1,
  },
  backLabel: {
    color: Colors.textPrimary,
    fontWeight: '700',
    fontSize: Platform.OS === 'ios' ? 24 : Font.title,
    lineHeight: Platform.OS === 'ios' ? 30 : 28,
    includeFontPadding: false,
  },
  scrollContent: {
    paddingHorizontal: 16,
    paddingTop: 0,
    paddingBottom: 20 + FADE_HEIGHT,
    gap: 14,
  },
  scrollContentBelowStickyHeader: {
    paddingTop: FADE_HEIGHT,
  },
  noticeCard: {
    alignSelf: 'center',
    width: '100%',
    borderRadius: 18,
    paddingVertical: Gap.xl,
    paddingHorizontal: 14,
  },
  noticeTitle: {
    color: Colors.textPrimary,
    fontWeight: '800',
    fontSize: Font.md,
  },
  noticeBody: {
    marginTop: Gap.sm,
    color: Colors.textNearWhite,
    fontSize: Font.md,
    lineHeight: LineHeight.md,
  },
  headerCard: {
    alignSelf: 'center',
    borderRadius: 26,
    padding: 14,
  },
  headerCardCompact: {
    width: '100%',
  },
  headerCardWide: {
    width: '100%',
    maxWidth: MaxWidth.grid,
  },
  header: {
    alignItems: 'center',
    justifyContent: 'center',
    gap: 16,
    alignSelf: 'center',
  },
  headerWide: {
    flexDirection: 'row',
    height: 260,
    gap: 32,
  },
  headerCompact: {
    flexDirection: 'column',
    paddingTop: 16,
  },
  albumWrapOuter: {
    width: 220,
    height: 220,
    alignItems: 'center',
    justifyContent: 'center',
  },
  albumWrap: {
    width: 220,
    height: 220,
    borderRadius: 18,
    overflow: 'hidden',
    backgroundColor: Colors.purpleTabActive,
    alignItems: 'center',
    justifyContent: 'center',
  },
  albumImage: {
    width: '100%',
    height: '100%',
  },
  albumPlaceholder: {
    width: '100%',
    height: '100%',
    backgroundColor: Colors.borderPrimary,
  },
  headerTextWrap: {
    alignItems: 'center',
    justifyContent: 'center',
    gap: Gap.md,
  },
  songTitle: {
    color: Colors.textPrimary,
    fontSize: 28,
    fontWeight: '800',
    textAlign: 'center',
  },
  songSubTitle: {
    color: Colors.textNearWhite,
    fontSize: 18,
    fontWeight: '800',
    textAlign: 'center',
  },

  layoutDebugRow: {
    gap: Gap.md,
  },
  layoutDebugToggle: {
    alignSelf: 'flex-start',
    paddingHorizontal: Gap.lg,
    paddingVertical: 6,
    borderRadius: Radius.xs,
    borderWidth: 1,
    borderColor: Colors.borderPrimary,
    backgroundColor: Colors.backgroundCard,
  },
  layoutDebugTogglePressed: {
    opacity: Opacity.pressed,
  },
  layoutDebugToggleText: {
    color: Colors.textPrimary,
    fontWeight: '700',
    fontSize: Font.sm,
  },
  layoutDebugBox: {
    borderRadius: Radius.sm,
    borderWidth: 1,
    borderColor: Colors.borderPrimary,
    backgroundColor: Colors.backgroundCard,
    paddingHorizontal: Gap.lg,
    paddingVertical: Gap.md,
    gap: Gap.xs,
  },
  layoutDebugText: {
    color: Colors.textTertiary,
    fontSize: Font.sm,
    fontWeight: '700',
  },
});
