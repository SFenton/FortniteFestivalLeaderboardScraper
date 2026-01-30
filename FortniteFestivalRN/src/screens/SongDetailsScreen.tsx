import React, {useEffect, useMemo, useRef, useState} from 'react';
import {Dimensions, Image, Platform, Pressable, ScrollView, StyleSheet, Text, useWindowDimensions, View} from 'react-native';
import type {NativeStackScreenProps} from '@react-navigation/native-stack';

import {Screen} from '../ui/Screen';
import {FrostedSurface} from '../ui/FrostedSurface';
import {usePageInstrumentation} from '../app/instrumentation/usePageInstrumentation';
import {useFestival} from '../app/festival/FestivalContext';
import type {InstrumentKey} from '../core/instruments';
import {defaultPrimaryInstrumentOrder} from '../app/songs/songFiltering';
import {buildSongInfoInstrumentRows} from '../app/songInfo/songInfo';
import {getInstrumentIconSource} from '../ui/instruments/instrumentVisuals';

const STAR_WHITE_ICON = require('../assets/icons/star_white.png');
const STAR_GOLD_ICON = require('../assets/icons/star_gold.png');

export type SongsStackParamList = {
  SongsList: undefined;
  SongDetails: {songId: string};
};

export type SongDetailsScreenProps = NativeStackScreenProps<SongsStackParamList, 'SongDetails'>;

export function SongDetailsView(props: {songId: string; showBack?: boolean; onBack?: () => void}) {
  const songId = props.songId;
  usePageInstrumentation(`Song:${songId}`);

  const {width} = useWindowDimensions();
  const [rootMeasuredWidth, setRootMeasuredWidth] = useState<number | null>(null);
  const effectiveWidth = rootMeasuredWidth && rootMeasuredWidth > 0 ? rootMeasuredWidth : width;
  const useCompactLayout = effectiveWidth < 720;

  const [showLayoutDebug, setShowLayoutDebug] = useState(false);

  const [wideMeasuredWidth, setWideMeasuredWidth] = useState<number | null>(null);
  const [dimensionChangeCount, setDimensionChangeCount] = useState(0);
  const [dimensionEventWindowWidth, setDimensionEventWindowWidth] = useState<number | null>(null);

  const {
    state: {songs, scoresIndex, settings},
    actions: {logUi},
  } = useFestival();

  const song = useMemo(() => songs.find(s => s.track.su === songId), [songId, songs]);

  const title = song?.track.tt ?? song?._title ?? songId;
  const artist = song?.track.an ?? '';
  const year = song?.track.ry;
  const imageUri = song?.imagePath ?? song?.track.au;

  const enabledInstrumentOrder = useMemo(() => {
    const base = defaultPrimaryInstrumentOrder().map(x => x.key);
    const isEnabled = (key: InstrumentKey): boolean => {
      if (!settings) return true;
      switch (key) {
        case 'guitar':
          return settings.queryLead;
        case 'drums':
          return settings.queryDrums;
        case 'vocals':
          return settings.queryVocals;
        case 'bass':
          return settings.queryBass;
        case 'pro_guitar':
          return settings.queryProLead;
        case 'pro_bass':
          return settings.queryProBass;
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
    <Screen style={styles.screen} safeAreaEdges={['top', 'left', 'right', 'bottom']}>
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
        {imageUri ? <Image source={{uri: imageUri}} style={styles.bgImage} resizeMode="cover" /> : null}
        <View style={styles.bgDim} />

        {props.showBack && props.onBack ? (
          <Pressable
            onPress={props.onBack}
            hitSlop={12}
            style={styles.backAnchor}
            accessibilityRole="button"
            accessibilityLabel="Go back"
          >
            {({pressed}) => (
              <FrostedSurface
                style={[styles.backCard, pressed && styles.backCardPressed]}
                tint="dark"
                intensity={18}
              >
                <Text style={styles.backArrow}>←</Text>
                <Text style={styles.backLabel}>Back</Text>
              </FrostedSurface>
            )}
          </Pressable>
        ) : null}

        <ScrollView contentContainerStyle={styles.scrollContent}>
          <FrostedSurface
            style={[
              styles.headerCard,
              useCompactLayout ? styles.headerCardCompact : styles.headerCardWide,
            ]}
            tint="dark"
            intensity={18}
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

          <FrostedSurface
            style={[
              styles.scoreGridSurface,
              useCompactLayout ? styles.scoreGridSurfaceCompact : styles.scoreGridSurfaceWide,
            ]}
            tint="dark"
            intensity={18}
            onLayout={
              useCompactLayout
                ? undefined
                : e => {
                    const next = e.nativeEvent.layout.width;
                    // Avoid re-render loops from tiny float diffs.
                    setWideMeasuredWidth(cur => {
                      if (cur == null) return next;
                      return Math.abs(cur - next) >= 1 ? next : cur;
                    });
                  }
            }
          >
            {__DEV__ && Platform.OS === 'windows' ? (
              <View style={styles.layoutDebugRow}>
                <Pressable
                  onPress={() => setShowLayoutDebug(v => !v)}
                  style={({pressed}) => [styles.layoutDebugToggle, pressed && styles.layoutDebugTogglePressed]}
                  accessibilityRole="button"
                  accessibilityLabel="Toggle layout debug"
                >
                  <Text style={styles.layoutDebugToggleText}>{showLayoutDebug ? 'Hide layout debug' : 'Show layout debug'}</Text>
                </Pressable>

                {showLayoutDebug ? (
                  <View style={styles.layoutDebugBox}>
                    <Text style={styles.layoutDebugText}>windowWidth: {Math.round(width)}</Text>
                    <Text style={styles.layoutDebugText}>
                      rootMeasuredWidth: {rootMeasuredWidth == null ? 'null' : Math.round(rootMeasuredWidth)}
                    </Text>
                    <Text style={styles.layoutDebugText}>effectiveWidth: {Math.round(effectiveWidth)}</Text>
                    <Text style={styles.layoutDebugText}>compact: {String(useCompactLayout)}</Text>
                    <Text style={styles.layoutDebugText}>
                      Dimensions(change): {dimensionChangeCount} lastWidth:{' '}
                      {dimensionEventWindowWidth == null ? 'null' : Math.round(dimensionEventWindowWidth)}
                    </Text>
                    <Text style={styles.layoutDebugText}>
                      measured: {wideMeasuredWidth == null ? 'null' : Math.round(wideMeasuredWidth)}
                    </Text>
                    <Text style={styles.layoutDebugText}>containerUsed: {Math.round(wideTable.containerWidth)}</Text>
                    <Text style={styles.layoutDebugText}>
                      show: score {String(wideTable.show.score)} | percent {String(wideTable.show.percent)} | season{' '}
                      {String(wideTable.show.season)} | pct {String(wideTable.show.percentile)} | rank {String(wideTable.show.rank)} | entries{' '}
                      {String(wideTable.show.entries)}
                    </Text>
                    <Text style={styles.layoutDebugText}>metricWidth: {Math.round(wideTable.metricWidth)}</Text>
                  </View>
                ) : null}
              </View>
            ) : null}

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
            ) : (
              <View style={styles.compactRows}>
                {instrumentRows.map(r => (
                  <View key={r.key} style={styles.compactRowCard}>
                    <View style={styles.compactTop}>
                      <View style={styles.instIconCircle}>
                        <Image source={getInstrumentIconSource(r.key)} style={styles.instIcon} resizeMode="contain" />
                      </View>
                      <Text style={styles.instNameCompact} numberOfLines={1}>
                        {r.name}
                      </Text>
                    </View>

                    <View style={styles.compactCenter}>
                      <DifficultyBars rawDifficulty={r.rawDifficulty} compact />
                      <StarsVisual hasScore={r.hasScore} starsCount={r.starsCount} isFullCombo={r.isFullCombo} compact />
                    </View>

                    <View style={styles.metricsBlock}>
                      <View style={styles.metricRow2}>
                        <MetricCell label="Score" value={r.scoreDisplay} />
                        <MetricCell
                          label="Percent Hit"
                          value={r.percentDisplay}
                          highlight={r.isFullCombo}
                          highlightKind="gold"
                        />
                      </View>
                      <View style={styles.metricRow2}>
                        <MetricCell label="Season" value={r.seasonDisplay} />
                        <MetricCell
                          label="Percentile"
                          value={r.percentileDisplay}
                          highlight={r.isTop5Percentile}
                          highlightKind="gold"
                        />
                      </View>
                      <View style={styles.metricRow1}>
                        <MetricCell label="Rank" value={r.rankOutOfDisplay} />
                      </View>
                    </View>
                  </View>
                ))}
              </View>
            )}

            <Pressable
              onPress={() => logUi(`[SONG] details open ${songId}`)}
              style={({pressed}) => [styles.debugButton, pressed && styles.debugButtonPressed]}
              accessibilityRole="button"
              accessibilityLabel="Log song details"
            >
              <Text style={styles.debugButtonText}>Log</Text>
            </Pressable>

            {Platform.OS === 'android' ? <Text style={styles.hint}>Android hardware back is supported.</Text> : null}
          </FrostedSurface>
        </ScrollView>
      </View>
    </Screen>
  );
}

export function SongDetailsScreen(props: SongDetailsScreenProps) {
  return <SongDetailsView songId={props.route.params.songId} />;
}

function MetricPill(props: {value: string; highlight?: boolean; highlightKind?: 'gold'}) {
  const highlight = Boolean(props.highlight);
  const kind = props.highlightKind ?? 'gold';
  const isGold = highlight && kind === 'gold';
  return (
    <View style={[styles.pill, isGold && styles.pillGold]}>
      <Text style={[styles.pillText, isGold && styles.pillTextGold]} numberOfLines={1}>
        {props.value}
      </Text>
    </View>
  );
}

function MetricCell(props: {label: string; value: string; highlight?: boolean; highlightKind?: 'gold'}) {
  return (
    <View style={styles.metricCell}>
      <Text style={styles.metricLabel}>{props.label}</Text>
      <MetricPill value={props.value} highlight={props.highlight} highlightKind={props.highlightKind} />
    </View>
  );
}

function DifficultyBars(props: {rawDifficulty: number; compact?: boolean}) {
  // Match MAUI: raw 0-6 => display 1-7 filled bars.
  const raw = Number.isFinite(props.rawDifficulty) ? props.rawDifficulty : 0;
  const display = Math.max(0, Math.min(6, Math.trunc(raw))) + 1;
  const barW = props.compact ? 20 : 16;
  const barH = props.compact ? 40 : 34;

  return (
    <View style={[styles.diffRow, {gap: props.compact ? 2 : 1}]}
      accessibilityRole="text"
      accessibilityLabel={`Difficulty ${display} of 7`}
    >
      {Array.from({length: 7}).map((_, idx) => {
        const filled = idx + 1 <= display;
        return (
          <View
            // eslint-disable-next-line react/no-array-index-key
            key={idx}
            style={[
              styles.diffBar,
              {
                width: barW,
                height: barH,
                backgroundColor: filled ? '#FFFFFF' : '#666666',
              },
            ]}
          />
        );
      })}
    </View>
  );
}

function StarsVisual(props: {
  hasScore: boolean;
  starsCount: number;
  isFullCombo: boolean;
  compact?: boolean;
}) {
  if (!props.hasScore) {
    return <MetricPill value="N/A" />;
  }

  const raw = Number.isFinite(props.starsCount) ? props.starsCount : 0;
  const allGold = raw >= 6;
  const displayCount = allGold ? 5 : Math.max(1, raw);
  const size = props.compact ? 40 : 48;
  const inner = props.compact ? 32 : 40;
  const outline = props.isFullCombo ? '#FFD700' : 'transparent';
  const source = allGold ? STAR_GOLD_ICON : STAR_WHITE_ICON;

  return (
    <View style={styles.starRow}>
      {Array.from({length: displayCount}).map((_, idx) => (
        <View
          // eslint-disable-next-line react/no-array-index-key
          key={idx}
          style={[styles.starCircle, {width: size, height: size, borderColor: outline}]}
        >
          <Image source={source} style={[styles.starIcon, {width: inner, height: inner}]} resizeMode="contain" />
        </View>
      ))}
    </View>
  );
}

const styles = StyleSheet.create({
  screen: {
    backgroundColor: '#000000',
  },
  root: {
    flex: 1,
  },
  bgImage: {
    ...StyleSheet.absoluteFillObject,
    opacity: 1,
  },
  bgDim: {
    ...StyleSheet.absoluteFillObject,
    backgroundColor: 'rgba(0,0,0,0.7)',
  },
  backAnchor: {
    position: 'absolute',
    left: 16,
    top: 16,
    zIndex: 10,
  },
  backCard: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 6,
    paddingHorizontal: 16,
    paddingVertical: 10,
    borderRadius: 24,
  },
  backCardPressed: {
    opacity: 0.92,
  },
  backArrow: {
    color: '#FFFFFF',
    fontWeight: '800',
    fontSize: 14,
  },
  backLabel: {
    color: '#FFFFFF',
    fontWeight: '800',
    fontSize: 14,
  },
  scrollContent: {
    paddingHorizontal: 16,
    paddingTop: 48,
    paddingBottom: 20,
    gap: 14,
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
    color: '#FFFFFF',
    fontSize: 18,
    fontWeight: '800',
    textAlign: 'center',
    opacity: 0.95,
  },
  scoreGridSurface: {
    alignSelf: 'center',
    borderRadius: 26,
    padding: 12,
    gap: 10,
  },
  scoreGridSurfaceCompact: {
    width: '100%',
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
  compactRows: {
    gap: 10,
  },
  compactRowCard: {
    paddingHorizontal: 10,
    paddingVertical: 12,
    gap: 10,
  },
  compactTop: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    gap: 12,
  },
  instNameCompact: {
    color: '#FFFFFF',
    fontWeight: '800',
    fontSize: 16,
  },
  compactCenter: {
    alignItems: 'center',
    gap: 8,
  },
  metricsBlock: {
    gap: 12,
  },
  metricRow2: {
    flexDirection: 'row',
    gap: 12,
  },
  metricRow1: {
    flexDirection: 'row',
  },
  metricCell: {
    flex: 1,
    gap: 2,
  },
  metricLabel: {
    color: '#FFFFFF',
    fontWeight: '800',
    fontSize: 12,
    textAlign: 'center',
  },
  pill: {
    backgroundColor: '#1D3A71',
    borderRadius: 10,
    paddingHorizontal: 8,
    paddingVertical: 4,
    borderWidth: 2,
    borderColor: 'transparent',
    minHeight: 30,
    alignItems: 'center',
    justifyContent: 'center',
  },
  pillGold: {
    backgroundColor: '#332915',
    borderColor: '#FFD700',
  },
  pillText: {
    color: '#FFFFFF',
    fontWeight: '800',
  },
  pillTextGold: {
    color: '#FFD700',
  },
  diffRow: {
    flexDirection: 'row',
    alignItems: 'center',
  },
  diffBar: {
    transform: [{skewX: '-10deg'}],
  },
  starRow: {
    flexDirection: 'row',
    gap: 4,
    alignItems: 'center',
    justifyContent: 'center',
  },
  starCircle: {
    borderRadius: 999,
    borderWidth: 2,
    backgroundColor: 'transparent',
    alignItems: 'center',
    justifyContent: 'center',
  },
  starText: {
    textAlign: 'center',
    textAlignVertical: 'center',
    fontSize: 22,
    lineHeight: 32,
  },
  starIcon: {
    // Intentionally empty; size is set inline.
  },
  debugButton: {
    alignSelf: 'flex-start',
    paddingHorizontal: 12,
    paddingVertical: 8,
    borderRadius: 10,
    borderWidth: 1,
    borderColor: '#2B3B55',
    backgroundColor: '#0B1220',
  },
  debugButtonPressed: {
    opacity: 0.85,
  },
  debugButtonText: {
    color: '#FFFFFF',
    fontWeight: '700',
  },
  hint: {
    color: '#9AA6B2',
    fontSize: 12,
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
