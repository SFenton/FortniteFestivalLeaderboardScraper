import React, {useEffect, useMemo, useRef, useState} from 'react';
import {Dimensions, Image, Platform, Pressable, ScrollView, StyleSheet, Text, useWindowDimensions, View} from 'react-native';
import type {NativeStackScreenProps} from '@react-navigation/native-stack';
import {useSafeAreaInsets} from 'react-native-safe-area-context';
import Ionicons from 'react-native-vector-icons/Ionicons';
import MaskedView from '@react-native-masked-view/masked-view';
import LinearGradient from 'react-native-linear-gradient';

import {Screen} from '../ui/Screen';
import {FrostedSurface} from '../ui/FrostedSurface';
import {PageHeader} from '../ui/PageHeader';
import {usePageInstrumentation} from '../app/instrumentation/usePageInstrumentation';
import {useFestival} from '../app/festival/FestivalContext';
import type {InstrumentKey} from '../core/instruments';
import {normalizeInstrumentOrder} from '../app/songs/songFiltering';
import {buildSongInfoInstrumentRows} from '../app/songInfo/songInfo';
import {getInstrumentIconSource} from '../ui/instruments/instrumentVisuals';
import {InstrumentCard, MetricPill, DifficultyBars, StarsVisual} from '../ui/instruments/InstrumentCard';

const CARD_BG = 'rgba(18,24,38,0.78)';

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
  const useCompactLayout = effectiveWidth < 720;

  const [wideMeasuredWidth, setWideMeasuredWidth] = useState<number | null>(null);
  const [_dimensionChangeCount, setDimensionChangeCount] = useState(0);
  const [_dimensionEventWindowWidth, setDimensionEventWindowWidth] = useState<number | null>(null);

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

  const wideTable = useMemo(() => {
    // Wide mode has no horizontal scrolling.
    // We progressively hide columns as the window shrinks, until we hit compact layout.
    const fallback = Math.min(Math.max(effectiveWidth - 32, 0), 1352);
    const containerWidth = wideMeasuredWidth && wideMeasuredWidth > 0 ? wideMeasuredWidth : fallback;

    const instrumentColWidth = 160;
    const diffColWidth = containerWidth >= 980 ? 140 : 120;

    // Stars is expensive width-wise; shrink it a bit on smaller wide windows.
    const starsColWidth = containerWidth >= 1200 ? 260 : containerWidth >= 980 ? 220 : 190;

    const metricsOrder: Array<'score' | 'percent' | 'season' | 'percentile' | 'rank' | 'entries'> = [
      'score',
      'percent',
      'season',
      'percentile',
      'rank',
      'entries',
    ];

    // Minimum widths for each metric. These are chosen to match the MAUI feel while
    // still allowing us to fit without horizontal scroll.
    const metricMinWidth: Record<(typeof metricsOrder)[number], number> = {
      score: 105,
      percent: 100,
      season: 80,
      percentile: 115,
      rank: 95,
      entries: 95,
    };

    const gap = 14;

    const baseCols = 3; // instrument + diff + stars
    const baseWidth = instrumentColWidth + diffColWidth + starsColWidth;

    let visible = [...metricsOrder];
    const calcTotal = (visibleMetrics: typeof visible): number => {
      const metricWidth = visibleMetrics.reduce((sum, k) => sum + metricMinWidth[k], 0);
      const cols = baseCols + visibleMetrics.length;
      const gaps = Math.max(0, cols - 1) * gap;
      // table has paddingHorizontal 10 on both header/row; approximate by subtracting 20.
      return baseWidth + metricWidth + gaps + 20;
    };

    // Remove right-most columns first until we fit.
    while (visible.length > 0 && calcTotal(visible) > containerWidth) visible.pop();

    const show = {
      score: visible.includes('score'),
      percent: visible.includes('percent'),
      season: visible.includes('season'),
      percentile: visible.includes('percentile'),
      rank: visible.includes('rank'),
      entries: visible.includes('entries'),
    };

    // With fewer metrics, we can give each a bit more room (still capped).
    const remaining = Math.max(0, containerWidth - (baseWidth + Math.max(0, (baseCols - 1) * gap) + 20));
    const metricCount = visible.length;
    const metricWidth = metricCount > 0 ? Math.min(140, Math.max(80, Math.floor(remaining / metricCount) - gap)) : 0;

    return {
      containerWidth,
      instrumentColWidth,
      diffColWidth,
      starsColWidth,
      show,
      metricWidth,
    };
  }, [effectiveWidth, wideMeasuredWidth]);

  const debugRef = useRef({
    lastCompact: useCompactLayout,
    lastMeasured: -1,
    lastShowKey: '',
    lastWidth: -1,
    lastMetricWidth: -1,
  });

  useEffect(() => {
    if (!__DEV__) return;
    // eslint-disable-next-line no-console
    console.warn('[SONG_UI] mounted', {platform: Platform.OS, songId, dev: __DEV__});
  }, [songId]);

  useEffect(() => {
    if (!__DEV__) return;
    const sub = Dimensions.addEventListener('change', ({window}) => {
      setDimensionChangeCount(c => c + 1);
      setDimensionEventWindowWidth(window.width);
    });
    return () => {
      // RN 0.65+ subscription object
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      (sub as any)?.remove?.();
    };
  }, []);

  useEffect(() => {
    if (!__DEV__) return;

    const showKey = `score:${Number(wideTable.show.score)} percent:${Number(wideTable.show.percent)} season:${Number(
      wideTable.show.season,
    )} pct:${Number(wideTable.show.percentile)} rank:${Number(wideTable.show.rank)} entries:${Number(
      wideTable.show.entries,
    )}`;

    const measured = wideMeasuredWidth ?? -1;
    const shouldLog =
      debugRef.current.lastCompact !== useCompactLayout ||
      Math.abs(debugRef.current.lastMeasured - measured) >= 1 ||
      debugRef.current.lastShowKey !== showKey ||
      Math.abs(debugRef.current.lastWidth - width) >= 1 ||
      Math.abs(debugRef.current.lastMetricWidth - wideTable.metricWidth) >= 1;

    if (!shouldLog) return;

    debugRef.current = {
      lastCompact: useCompactLayout,
      lastMeasured: measured,
      lastShowKey: showKey,
      lastWidth: width,
      lastMetricWidth: wideTable.metricWidth,
    };

    // eslint-disable-next-line no-console
    console.warn('[SONG_UI] resize/layout', {
      platform: Platform.OS,
      windowWidth: width,
      rootMeasuredWidth,
      effectiveWidth,
      useCompactLayout,
      wideMeasuredWidth,
      containerWidthUsed: wideTable.containerWidth,
      widths: {
        instrument: wideTable.instrumentColWidth,
        diff: wideTable.diffColWidth,
        stars: wideTable.starsColWidth,
        metric: wideTable.metricWidth,
      },
      show: wideTable.show,
    });
  }, [effectiveWidth, rootMeasuredWidth, useCompactLayout, wideMeasuredWidth, wideTable, width]);

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
            ]}
            showsVerticalScrollIndicator={false}
            showsHorizontalScrollIndicator={false}
          >
          <FrostedSurface
            style={[styles.headerCard, useCompactLayout ? styles.headerCardCompact : styles.headerCardWide]}
            tint="dark"
            intensity={22}
            fallbackColor={CARD_BG}
          >
            <View style={[styles.header, useCompactLayout ? styles.headerCompact : styles.headerWide]}>
              <View style={styles.albumWrapOuter}>
                <View style={styles.albumWrap}>
                  {imageUri ? (
                    <Image source={{uri: imageUri}} style={styles.albumImage} resizeMode="contain" />
                  ) : (
                    <View style={styles.albumPlaceholder} />
                  )}
                </View>
              </View>

              <View style={[styles.headerTextWrap, !useCompactLayout && {width: 320}]}>
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
              fallbackColor={CARD_BG}
            >
              <Text style={styles.noticeTitle}>{notice.title}</Text>
              <Text style={styles.noticeBody}>{notice.body}</Text>
            </FrostedSurface>
          ) : null}

          {useCompactLayout ? (
            instrumentRows.map(r => (
              <InstrumentCard key={r.key} data={r} />
            ))
          ) : (
            <FrostedSurface
              style={[styles.scoreGridSurface, styles.scoreGridSurfaceWide]}
              tint="dark"
              intensity={22}
              fallbackColor={CARD_BG}
              onLayout={e => {
                const next = e.nativeEvent.layout.width;
                setWideMeasuredWidth(cur => {
                  if (cur == null) return next;
                  return Math.abs(cur - next) >= 1 ? next : cur;
                });
              }}
            >
              {!useCompactLayout ? (
                <View style={[styles.tableWrap, {width: wideTable.containerWidth}]}>
                  <View style={styles.tableHeaderRow}>
                    <Text style={[styles.tableHeaderText, {width: wideTable.instrumentColWidth}]}>Instrument</Text>
                    <Text style={[styles.tableHeaderText, {width: wideTable.diffColWidth, textAlign: 'center'}]}>Diff</Text>
                    <Text style={[styles.tableHeaderText, {width: wideTable.starsColWidth, textAlign: 'center'}]}>Stars</Text>
                    {wideTable.show.score ? (
                      <Text style={[styles.tableHeaderText, {width: wideTable.metricWidth, textAlign: 'center'}]}>Score</Text>
                    ) : null}
                    {wideTable.show.percent ? (
                      <Text style={[styles.tableHeaderText, {width: wideTable.metricWidth, textAlign: 'center'}]}>Percent</Text>
                    ) : null}
                    {wideTable.show.season ? (
                      <Text style={[styles.tableHeaderText, {width: wideTable.metricWidth, textAlign: 'center'}]}>Season</Text>
                    ) : null}
                    {wideTable.show.percentile ? (
                      <Text style={[styles.tableHeaderText, {width: wideTable.metricWidth, textAlign: 'center'}]}>Percentile</Text>
                    ) : null}
                    {wideTable.show.rank ? (
                      <Text style={[styles.tableHeaderText, {width: wideTable.metricWidth, textAlign: 'center'}]}>Rank</Text>
                    ) : null}
                    {wideTable.show.entries ? (
                      <Text style={[styles.tableHeaderText, {width: wideTable.metricWidth, textAlign: 'center'}]}>Entries</Text>
                    ) : null}
                  </View>

                  {instrumentRows.map(r => (
                    <View key={r.key} style={styles.tableRow}>
                      <View style={[styles.tableInstrumentCol, {width: wideTable.instrumentColWidth}]}>
                        <View style={styles.instIconCircle}>
                          <Image source={getInstrumentIconSource(r.key)} style={styles.instIcon} resizeMode="contain" />
                        </View>
                        <Text style={styles.instNameWide} numberOfLines={1}>
                          {r.name}
                        </Text>
                      </View>

                      <View style={{width: wideTable.diffColWidth, alignItems: 'center', justifyContent: 'center', paddingRight: 10}}>
                        <DifficultyBars rawDifficulty={r.rawDifficulty} />
                      </View>

                      <View style={{width: wideTable.starsColWidth, alignItems: 'center', justifyContent: 'center'}}>
                        <StarsVisual hasScore={r.hasScore} starsCount={r.starsCount} isFullCombo={r.isFullCombo} />
                      </View>

                      {wideTable.show.score ? (
                        <View style={{width: wideTable.metricWidth}}>
                          <MetricPill value={r.scoreDisplay} />
                        </View>
                      ) : null}
                      {wideTable.show.percent ? (
                        <View style={{width: wideTable.metricWidth}}>
                          <MetricPill value={r.percentDisplay} highlight={r.isFullCombo} highlightKind="gold" />
                        </View>
                      ) : null}
                      {wideTable.show.season ? (
                        <View style={{width: wideTable.metricWidth}}>
                          <MetricPill value={r.seasonDisplay} />
                        </View>
                      ) : null}
                      {wideTable.show.percentile ? (
                        <View style={{width: wideTable.metricWidth}}>
                          <MetricPill value={r.percentileDisplay} highlight={r.isTop5Percentile} highlightKind="gold" />
                        </View>
                      ) : null}
                      {wideTable.show.rank ? (
                        <View style={{width: wideTable.metricWidth}}>
                          <MetricPill value={r.rankDisplay} />
                        </View>
                      ) : null}
                      {wideTable.show.entries ? (
                        <View style={{width: wideTable.metricWidth}}>
                          <MetricPill value={r.totalEntriesDisplay} />
                        </View>
                      ) : null}
                    </View>
                  ))}
                </View>
              ) : null}
            </FrostedSurface>
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



const FADE_HEIGHT = 32;

const styles = StyleSheet.create({
  screen: {
    backgroundColor: 'transparent',
  },
  root: {
    flex: 1,
  },
  content: {
    flex: 1,
  },
  bgBase: {
    ...StyleSheet.absoluteFillObject,
    backgroundColor: '#1A0830',
  },
  bgImage: {
    ...StyleSheet.absoluteFillObject,
    opacity: 0.9,
  },
  bgDim: {
    ...StyleSheet.absoluteFillObject,
    backgroundColor: 'rgba(0,0,0,0.7)',
  },
  stickyHeader: {
    zIndex: 10,
    paddingHorizontal: 20,
    paddingTop: 16,
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
    backgroundColor: '#000000',
  },
  fadeGradient: {
    height: FADE_HEIGHT,
  },
  scroll: {
    flex: 1,
  },
  backLabel: {
    color: '#FFFFFF',
    fontWeight: '700',
    fontSize: Platform.OS === 'ios' ? 24 : 22,
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
    paddingVertical: 12,
    paddingHorizontal: 14,
  },
  noticeTitle: {
    color: '#FFFFFF',
    fontWeight: '800',
    fontSize: 14,
  },
  noticeBody: {
    marginTop: 4,
    color: '#F2F6FF',
    fontSize: 13,
    lineHeight: 18,
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
    maxWidth: 900,
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
    backgroundColor: '#7A2B95',
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
    backgroundColor: '#2B3B55',
  },
  headerTextWrap: {
    alignItems: 'center',
    justifyContent: 'center',
    gap: 8,
  },
  songTitle: {
    color: '#FFFFFF',
    fontSize: 28,
    fontWeight: '800',
    textAlign: 'center',
  },
  songSubTitle: {
    color: '#F2F6FF',
    fontSize: 18,
    fontWeight: '800',
    textAlign: 'center',
  },
  scoreGridSurface: {
    alignSelf: 'center',
    borderRadius: 26,
    padding: 12,
    gap: 10,
  },
  scoreGridSurfaceWide: {
    width: '100%',
    maxWidth: 1352,
  },
  tableWrap: {
    alignSelf: 'center',
  },
  tableHeaderRow: {
    flexDirection: 'row',
    alignItems: 'center',
    paddingHorizontal: 10,
    paddingVertical: 6,
    gap: 14,
  },
  tableHeaderText: {
    color: '#FFFFFF',
    fontWeight: '800',
  },
  tableRow: {
    flexDirection: 'row',
    alignItems: 'center',
    paddingHorizontal: 10,
    paddingVertical: 12,
    gap: 14,
  },
  tableInstrumentCol: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 12,
  },
  instIconCircle: {
    width: 48,
    height: 48,
    borderRadius: 24,
    backgroundColor: '#4B0F63',
    alignItems: 'center',
    justifyContent: 'center',
  },
  instIcon: {
    width: 40,
    height: 40,
  },
  instNameWide: {
    color: '#FFFFFF',
    fontWeight: '800',
    flex: 1,
  },

  layoutDebugRow: {
    gap: 8,
  },
  layoutDebugToggle: {
    alignSelf: 'flex-start',
    paddingHorizontal: 10,
    paddingVertical: 6,
    borderRadius: 8,
    borderWidth: 1,
    borderColor: '#2B3B55',
    backgroundColor: '#0B1220',
  },
  layoutDebugTogglePressed: {
    opacity: 0.85,
  },
  layoutDebugToggleText: {
    color: '#FFFFFF',
    fontWeight: '700',
    fontSize: 12,
  },
  layoutDebugBox: {
    borderRadius: 10,
    borderWidth: 1,
    borderColor: '#2B3B55',
    backgroundColor: '#0B1220',
    paddingHorizontal: 10,
    paddingVertical: 8,
    gap: 2,
  },
  layoutDebugText: {
    color: '#9AA6B2',
    fontSize: 12,
    fontWeight: '700',
  },
});
