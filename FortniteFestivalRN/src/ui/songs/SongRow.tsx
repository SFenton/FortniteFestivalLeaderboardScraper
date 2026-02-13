import React from 'react';
import {Image, Platform, Pressable, StyleSheet, Text, useWindowDimensions, View} from 'react-native';
import {FrostedSurface} from '../FrostedSurface';
import {getInstrumentIconSource} from '../instruments/instrumentVisuals';
import type {InstrumentKey} from '../../core/instruments';
import type {MetadataSortKey} from '../../core/songListConfig';

const STAR_WHITE_ICON = require('../../assets/icons/star_white.png');
const STAR_GOLD_ICON = require('../../assets/icons/star_gold.png');

// ── Public types ────────────────────────────────────────────────────

/** Colour pair for an instrument chip (circle behind the icon). */
export type InstrumentChipVisual = {
  instrumentKey: InstrumentKey;
  fill: string;
  stroke: string;
};

/** Detailed metadata for a single-instrument filter view. */
export type InstrumentDetailData = {
  scoreDisplay: string;
  starsCount: number;
  hasScore: boolean;
  isFullCombo: boolean;
  seasonDisplay: string;
  percentHitDisplay?: string;
  percentileDisplay?: string;
  isTop5Percentile?: boolean;
  /** Game difficulty the score was played on: E, M, H, X, or empty if unknown. */
  gameDifficultyDisplay?: string;
};

/** Everything the row needs to render – no domain models required. */
export type SongRowDisplayData = {
  title: string;
  artist: string;
  year?: number;
  imageUri?: string;
  /** When omitted the instrument chips are hidden. */
  instruments?: InstrumentChipVisual[];
  /** Present only when filtering to a single instrument. */
  instrumentDetail?: InstrumentDetailData;
  /** Display priority order for instrument metadata. Position 0 = top row, 1-3 = bottom row, 4 = hidden. */
  metadataDisplayOrder?: MetadataSortKey[];
};

// ── Helpers ─────────────────────────────────────────────────────────

function MiniStars(props: {starsCount: number; isFullCombo: boolean; hasScore: boolean}) {
  if (!props.hasScore) return null;
  const raw = Number.isFinite(props.starsCount) ? props.starsCount : 0;
  const allGold = raw >= 6;
  const displayCount = allGold ? 5 : Math.max(1, raw);
  const source = allGold ? STAR_GOLD_ICON : STAR_WHITE_ICON;
  const outline = props.isFullCombo ? '#FFD700' : 'transparent';
  return (
    <View style={styles.miniStarRow}>
      {Array.from({length: displayCount}).map((_, idx) => (
        <View key={idx} style={[styles.miniStarCircle, {borderColor: outline}]}>
          <Image source={source} style={styles.miniStarIcon} resizeMode="contain" />
        </View>
      ))}
    </View>
  );
}

function SeasonPill(props: {seasonDisplay: string}) {
  if (!props.seasonDisplay) return null;
  return (
    <View style={styles.seasonPill}>
      <Text style={styles.seasonPillText} numberOfLines={1}>{props.seasonDisplay}</Text>
    </View>
  );
}

const DIFF_COLORS: Record<string, {bg: string; border: string; text: string}> = {
  E: {bg: '#1B3A2F', border: '#34D399', text: '#34D399'},
  M: {bg: '#3A351B', border: '#FBBF24', text: '#FBBF24'},
  H: {bg: '#3A1B1B', border: '#F87171', text: '#F87171'},
  X: {bg: '#2D1B3A', border: '#C084FC', text: '#C084FC'},
};

function DifficultyPill(props: {display?: string}) {
  if (!props.display) return null;
  const colors = DIFF_COLORS[props.display] ?? {bg: '#1D3A71', border: 'transparent', text: '#FFFFFF'};
  return (
    <View style={[styles.diffPill, {backgroundColor: colors.bg, borderColor: colors.border}]}>
      <Text style={[styles.diffPillText, {color: colors.text}]} numberOfLines={1}>{props.display}</Text>
    </View>
  );
}

function PercentPill(props: {percentHitDisplay?: string; isFullCombo?: boolean}) {
  if (!props.percentHitDisplay) return null;
  const gold = Boolean(props.isFullCombo);
  return (
    <View style={[styles.percentPill, gold && styles.percentPillGold]}>
      <Text style={[styles.percentPillText, gold && styles.percentPillTextGold]} numberOfLines={1}>{props.percentHitDisplay}</Text>
    </View>
  );
}

function PercentilePill(props: {percentileDisplay?: string; isTop5?: boolean}) {
  if (!props.percentileDisplay) return null;
  const gold = Boolean(props.isTop5);
  return (
    <View style={[styles.percentilePill, gold && styles.percentilePillGold]}>
      <Text style={[styles.percentilePillText, gold && styles.percentilePillTextGold]} numberOfLines={1}>{props.percentileDisplay}</Text>
    </View>
  );
}

const DEFAULT_METADATA_ORDER: MetadataSortKey[] = ['title', 'artist', 'year', 'score', 'percentage', 'percentile', 'isfc', 'stars', 'seasonachieved'];

function FCBadge() {
  return (
    <View style={styles.fcBadge}>
      <Text style={styles.fcBadgeText}>FC</Text>
    </View>
  );
}

function renderMetadataElement(key: MetadataSortKey, detail: InstrumentDetailData, allKeys: MetadataSortKey[]): React.ReactElement | null {
  const is100FC = detail.hasScore && detail.isFullCombo && detail.percentHitDisplay === '100%';
  switch (key) {
    case 'score':
      return detail.hasScore && detail.scoreDisplay
        ? <Text style={styles.scoreStarsScoreText}>{detail.scoreDisplay}</Text>
        : null;
    case 'percentage':
      if (is100FC) {
        // If isfc also exists in the visible keys, the earlier one renders FC and the later one is skipped.
        const pctIdx = allKeys.indexOf('percentage');
        const fcIdx = allKeys.indexOf('isfc');
        if (fcIdx !== -1 && fcIdx < pctIdx) return null; // isfc already rendered FC
        return <FCBadge />; // percentage comes first, render as FC
      }
      return <PercentPill percentHitDisplay={detail.percentHitDisplay} isFullCombo={detail.isFullCombo} />;
    case 'isfc':
      if (is100FC) {
        const pctIdx = allKeys.indexOf('percentage');
        const fcIdx = allKeys.indexOf('isfc');
        if (pctIdx !== -1 && pctIdx < fcIdx) return null; // percentage already rendered FC
        return <FCBadge />; // isfc comes first, render as FC
      }
      return detail.hasScore && detail.isFullCombo ? <FCBadge /> : null;
    case 'stars':
      return (
        <View style={styles.scoreStarsInner}>
          <MiniStars starsCount={detail.starsCount} isFullCombo={detail.isFullCombo} hasScore={detail.hasScore} />
        </View>
      );
    case 'seasonachieved':
      return <SeasonPill seasonDisplay={detail.seasonDisplay} />;
    case 'percentile': {
      const pctDisplay = detail.percentileDisplay;
      return <PercentilePill percentileDisplay={pctDisplay} isTop5={detail.isTop5Percentile} />;
    }
    case 'title':
    case 'artist':
    case 'year':
      return null; // rendered in the main row header
    default:
      return null;
  }
}

const FIXED_WIDTH_KEYS: ReadonlySet<MetadataSortKey> = new Set<MetadataSortKey>([]);

function MetadataBottomRow(props: {keys: MetadataSortKey[]; detail: InstrumentDetailData; allKeys: MetadataSortKey[]; isPhone?: boolean; isWideLayout?: boolean}) {
  if (!props.detail.hasScore) return null;
  // First pass: collect visible entries so we can count them before building nodes.
  const visibleEntries: {key: MetadataSortKey; el: React.ReactElement}[] = [];
  for (const key of props.keys) {
    const el = renderMetadataElement(key, props.detail, props.allKeys);
    if (el) {
      visibleEntries.push({key, el});
    }
  }
  if (visibleEntries.length === 0) return null;

  // On phones with exactly 4 elements, use a 2×2 grid so every element can breathe.
  if (props.isPhone && visibleEntries.length === 4) {
    return (
      <View style={styles.metadataGrid}>
        <View style={styles.metadataGridRow}>
          <View key={visibleEntries[0].key} style={styles.metadataGridCell}>{visibleEntries[0].el}</View>
          <View key={visibleEntries[1].key} style={styles.metadataGridCell}>{visibleEntries[1].el}</View>
        </View>
        <View style={styles.metadataGridRow}>
          <View key={visibleEntries[2].key} style={styles.metadataGridCell}>{visibleEntries[2].el}</View>
          <View key={visibleEntries[3].key} style={styles.metadataGridCell}>{visibleEntries[3].el}</View>
        </View>
      </View>
    );
  }

  const elements: React.ReactNode[] = visibleEntries.map(({key, el}) => {
    const useFixed = FIXED_WIDTH_KEYS.has(key);
    return <View key={key} style={useFixed ? styles.metadataCellFixed : styles.metadataCell}>{el}</View>;
  });
  // When exactly 2 items: use spacers only on wide layouts (landscape tablet / open foldable).
  if (elements.length === 2) {
    if (props.isWideLayout) {
      return (
        <View style={styles.scoreStarsRow}>
          <View key="spacer-l" style={styles.metadataCell} />
          {elements}
          <View key="spacer-r" style={styles.metadataCell} />
        </View>
      );
    }
    if (props.isPhone) {
      return (
        <View style={styles.scoreStarsRow}>
          <View key="spacer-l" style={{flex: 0.5}} />
          <View key="slot-l" style={styles.metadataCellLeft}>{visibleEntries[0].el}</View>
          <View key="slot-r" style={styles.metadataCellRight}>{visibleEntries[1].el}</View>
          <View key="spacer-r" style={{flex: 0.5}} />
        </View>
      );
    }
    return (
      <View style={styles.scoreStarsRow}>
        {elements}
      </View>
    );
  }
  return (
    <View style={styles.scoreStarsRow}>
      {elements}
    </View>
  );
}

// ── Component ───────────────────────────────────────────────────────

export const SongRow = React.memo(function SongRow(props: {
  data: SongRowDisplayData;
  /** Use stacked (narrow) layout. Default: false. */
  compact?: boolean;
  /**
   * When true and compact is also true, instrument icons are placed at the
   * end of the art/text row instead of on a separate line below.  Use this
   * on tablet / open-foldable devices where there is enough horizontal room.
   */
  inlineInstruments?: boolean;
  /** Called when the row is tapped. Omit to make the row non-interactive. */
  onPress?: () => void;
}) {
  const {data, compact, inlineInstruments, onPress} = props;
  const {title, artist, year, imageUri, instruments, instrumentDetail, metadataDisplayOrder} = data;
  const hasArt = !!imageUri;
  const isSingleInstrument = instruments?.length === 1 && !!instrumentDetail;
  const metaOrder = metadataDisplayOrder ?? DEFAULT_METADATA_ORDER;
  // Keys that are only meaningful for sorting (already rendered in the row header).
  const instrumentMetaOrder = metaOrder.filter(k => k !== 'title' && k !== 'artist');

  // Detect phone-class device (not tablet/foldable, not Windows).
  const {width: winWidth, height: winHeight} = useWindowDimensions();
  const isPhone = Platform.OS !== 'windows' && Math.min(winWidth, winHeight) < 600;
  // Wide layout: landscape tablet or open foldable (min dimension >= 600 and landscape).
  const isWideLayout = Platform.OS !== 'windows' && Math.min(winWidth, winHeight) >= 600 && winWidth > winHeight;

  const inner = (pressed: boolean) => (
    <FrostedSurface style={[styles.rowSurface, pressed && styles.rowSurfacePressed]} tint="dark" intensity={12}>
      {compact && !inlineInstruments ? (
        <View style={styles.rowInnerCompact}>
          {isSingleInstrument && instruments && instrumentDetail ? (
            <>
              <View style={styles.compactTopRow}>
                {hasArt && (
                  <View style={styles.thumbWrap}>
                    <Image source={{uri: imageUri}} style={styles.thumb} resizeMode="cover" />
                  </View>
                )}
                <View style={[styles.rowText, !hasArt && styles.rowTextCentered]}>
                  <Text numberOfLines={1} style={styles.songTitle}>{title}</Text>
                  <Text numberOfLines={1} style={styles.songMeta}>
                    {artist}{artist && year ? ' • ' : ''}{year ?? ''}
                  </Text>
                </View>
                <View style={styles.detailStrip}>
                  {renderMetadataElement(instrumentMetaOrder[0], instrumentDetail, instrumentMetaOrder)}
                  <DifficultyPill display={instrumentDetail.gameDifficultyDisplay} />
                  <View
                    style={[styles.instrumentChipCompact, {backgroundColor: instruments[0].fill, borderColor: instruments[0].stroke}]}
                  >
                    <Image source={getInstrumentIconSource(instruments[0].instrumentKey)} style={styles.instrumentIconCompact} resizeMode="contain" />
                  </View>
                </View>
              </View>
              <MetadataBottomRow keys={instrumentMetaOrder.slice(1)} detail={instrumentDetail} allKeys={instrumentMetaOrder} isPhone={isPhone} isWideLayout={isWideLayout} />
            </>
          ) : (
            <>
              <View style={[styles.compactTopRow, !hasArt && styles.compactTopRowNoArt]}>
                {hasArt && (
                  <View style={styles.thumbWrap}>
                    <Image source={{uri: imageUri}} style={styles.thumb} resizeMode="cover" />
                  </View>
                )}
                <View style={[styles.rowText, !hasArt && styles.rowTextCentered]}>
                  <Text numberOfLines={1} style={styles.songTitle}>{title}</Text>
                  <Text numberOfLines={1} style={styles.songMeta}>
                    {artist}{artist && year ? ' • ' : ''}{year ?? ''}
                  </Text>
                </View>
              </View>
              {instruments && instruments.length > 0 && (
                <View style={styles.instrumentRowCompact}>
                  {instruments.map(s => (
                    <View
                      key={s.instrumentKey}
                      style={[styles.instrumentChipCompact, {backgroundColor: s.fill, borderColor: s.stroke}]}
                    >
                      <Image source={getInstrumentIconSource(s.instrumentKey)} style={styles.instrumentIconCompact} resizeMode="contain" />
                    </View>
                  ))}
                </View>
              )}
            </>
          )}
        </View>
      ) : compact && inlineInstruments ? (
        /* Tablet / open-foldable compact: art + text + icons in one row */
        isSingleInstrument && instruments && instrumentDetail ? (
          <View style={styles.rowInnerCompact}>
            <View style={styles.rowInnerRow}>
              <View style={styles.left}>
                {hasArt && (
                  <View style={styles.thumbWrap}>
                    <Image source={{uri: imageUri}} style={styles.thumb} resizeMode="cover" />
                  </View>
                )}
                <View style={[styles.rowText, !hasArt && styles.rowTextCentered]}>
                  <Text numberOfLines={1} style={styles.songTitle}>{title}</Text>
                  <Text numberOfLines={1} style={styles.songMeta}>
                    {artist}{artist && year ? ' • ' : ''}{year ?? ''}
                  </Text>
                </View>
              </View>
              <View style={styles.detailStrip}>
                {renderMetadataElement(instrumentMetaOrder[0], instrumentDetail, instrumentMetaOrder)}
                <DifficultyPill display={instrumentDetail.gameDifficultyDisplay} />
                <View
                  style={[styles.instrumentChipCompact, {backgroundColor: instruments[0].fill, borderColor: instruments[0].stroke}]}
                >
                  <Image source={getInstrumentIconSource(instruments[0].instrumentKey)} style={styles.instrumentIconCompact} resizeMode="contain" />
                </View>
              </View>
            </View>
            <MetadataBottomRow keys={instrumentMetaOrder.slice(1)} detail={instrumentDetail} allKeys={instrumentMetaOrder} isPhone={isPhone} isWideLayout={isWideLayout} />
          </View>
        ) : (
          <View style={styles.rowInner}>
            <View style={styles.left}>
              {hasArt && (
                <View style={styles.thumbWrap}>
                  <Image source={{uri: imageUri}} style={styles.thumb} resizeMode="cover" />
                </View>
              )}
              <View style={[styles.rowText, !hasArt && styles.rowTextCentered]}>
                <Text numberOfLines={1} style={styles.songTitle}>{title}</Text>
                <Text numberOfLines={1} style={styles.songMeta}>
                  {artist}{artist && year ? ' • ' : ''}{year ?? ''}
                </Text>
              </View>
            </View>
            {instruments && instruments.length > 0 && (
              <View style={styles.instrumentRow}>
                {instruments.map(s => (
                  <View
                    key={s.instrumentKey}
                    style={[styles.instrumentChipCompact, {backgroundColor: s.fill, borderColor: s.stroke}]}
                  >
                    <Image source={getInstrumentIconSource(s.instrumentKey)} style={styles.instrumentIconCompact} resizeMode="contain" />
                  </View>
                ))}
              </View>
            )}
          </View>
        )
      ) : (
        isSingleInstrument && instruments && instrumentDetail ? (
          <View style={styles.rowInnerCompact}>
            <View style={styles.rowInnerRow}>
              <View style={styles.left}>
                {hasArt && (
                  <View style={styles.thumbWrap}>
                    <Image source={{uri: imageUri}} style={styles.thumb} resizeMode="cover" />
                  </View>
                )}
                <View style={[styles.rowText, !hasArt && styles.rowTextCentered]}>
                  <Text numberOfLines={1} style={styles.songTitle}>{title}</Text>
                  <Text numberOfLines={1} style={styles.songMeta}>
                    {artist}{artist && year ? ' • ' : ''}{year ?? ''}
                  </Text>
                </View>
              </View>
              <View style={styles.detailStrip}>
                {renderMetadataElement(instrumentMetaOrder[0], instrumentDetail, instrumentMetaOrder)}
                <DifficultyPill display={instrumentDetail.gameDifficultyDisplay} />
                <View
                  style={[styles.instrumentChip, {backgroundColor: instruments[0].fill, borderColor: instruments[0].stroke}]}
                >
                  <Image source={getInstrumentIconSource(instruments[0].instrumentKey)} style={styles.instrumentIcon} resizeMode="contain" />
                </View>
              </View>
            </View>
            <MetadataBottomRow keys={instrumentMetaOrder.slice(1)} detail={instrumentDetail} allKeys={instrumentMetaOrder} isPhone={isPhone} isWideLayout={isWideLayout} />
          </View>
        ) : (
          <View style={styles.rowInner}>
            <View style={styles.left}>
              {hasArt && (
                <View style={styles.thumbWrap}>
                  <Image source={{uri: imageUri}} style={styles.thumb} resizeMode="cover" />
                </View>
              )}
              <View style={[styles.rowText, !hasArt && styles.rowTextCentered]}>
                <Text numberOfLines={1} style={styles.songTitle}>{title}</Text>
                <Text numberOfLines={1} style={styles.songMeta}>
                  {artist}{artist && year ? ' • ' : ''}{year ?? ''}
                </Text>
              </View>
            </View>
            {instruments && instruments.length > 0 && (
              <View style={styles.instrumentRow}>
                {instruments.map(s => (
                  <View
                    key={s.instrumentKey}
                    style={[styles.instrumentChip, {backgroundColor: s.fill, borderColor: s.stroke}]}
                  >
                    <Image source={getInstrumentIconSource(s.instrumentKey)} style={styles.instrumentIcon} resizeMode="contain" />
                  </View>
                ))}
              </View>
            )}
          </View>
        )
      )}
    </FrostedSurface>
  );

  if (onPress) {
    return (
      <Pressable onPress={onPress} style={styles.rowPressable} accessibilityRole="button" accessibilityLabel={`Open ${title}`}>
        {({pressed}) => inner(pressed)}
      </Pressable>
    );
  }

  // Non-interactive – just render the surface.
  return <View style={styles.rowPressable}>{inner(false)}</View>;
});

// ── Styles ──────────────────────────────────────────────────────────

const styles = StyleSheet.create({
  rowPressable: {
    marginBottom: 8,
    maxWidth: 600,
    width: '100%',
    alignSelf: 'center',
  },
  rowSurface: {
    borderRadius: 12,
  },
  rowSurfacePressed: {
    opacity: 0.92,
  },
  rowInner: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    paddingHorizontal: 12,
    paddingVertical: 10,
  },
  rowInnerCompact: {
    paddingHorizontal: 12,
    paddingVertical: 10,
    gap: 10,
  },
  compactTopRow: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 10,
    minWidth: 0,
  },
  compactTopRowNoArt: {
    justifyContent: 'center',
  },
  left: {
    flex: 1,
    flexDirection: 'row',
    alignItems: 'center',
    gap: 10,
    minWidth: 0,
  },
  rowText: {
    flex: 1,
    minWidth: 0,
  },
  rowTextCentered: {
    flex: undefined,
    alignItems: 'center',
  },
  instrumentRow: {
    flexDirection: 'row',
    gap: 6,
    alignItems: 'center',
    flexShrink: 0,
    marginLeft: 10,
  },
  instrumentRowCompact: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: 8,
    alignItems: 'center',
    justifyContent: 'center',
    alignSelf: 'center',
  },
  instrumentChip: {
    width: 40,
    height: 40,
    borderRadius: 20,
    borderWidth: 2,
    alignItems: 'center',
    justifyContent: 'center',
  },
  instrumentChipCompact: {
    width: 34,
    height: 34,
    borderRadius: 17,
    borderWidth: 2,
    alignItems: 'center',
    justifyContent: 'center',
  },
  instrumentIcon: {
    width: 32,
    height: 32,
  },
  instrumentIconCompact: {
    width: 24,
    height: 24,
  },
  thumbWrap: {
    width: 44,
    height: 44,
    borderRadius: 10,
    overflow: 'hidden',
    backgroundColor: '#0B1220',
    borderWidth: 1,
    borderColor: '#263244',
  },
  thumb: {
    width: '100%',
    height: '100%',
  },
  thumbPlaceholder: {
    flex: 1,
    backgroundColor: '#0B1220',
  },
  songTitle: {
    color: '#FFFFFF',
    fontSize: 14,
    fontWeight: '700',
  },
  songMeta: {
    color: '#B8C0CC',
    fontSize: 12,
    marginTop: 2,
  },

  // ── Instrument detail strip (single-instrument filter) ──

  detailStrip: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 8,
    flexShrink: 0,
    marginLeft: 10,
  },
  detailStripCentered: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    gap: 8,
  },
  rowInnerRow: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
  },
  miniStarRow: {
    flexDirection: 'row',
    gap: 3,
    alignItems: 'center',
  },
  miniStarCircle: {
    width: 24,
    height: 24,
    borderRadius: 12,
    borderWidth: 1.5,
    backgroundColor: 'transparent',
    alignItems: 'center',
    justifyContent: 'center',
    overflow: 'hidden',
  },
  miniStarIcon: {
    width: 20,
    height: 20,
  },
  diffPill: {
    borderRadius: 8,
    paddingHorizontal: 6,
    borderWidth: 2,
    borderColor: 'transparent',
    alignItems: 'center',
    justifyContent: 'center',
  },
  diffPillText: {
    color: '#FFFFFF',
    fontSize: 14,
    fontWeight: '800',
  },
  seasonPill: {
    backgroundColor: '#1D3A71',
    borderRadius: 8,
    paddingHorizontal: 10,
    borderWidth: 2,
    borderColor: 'transparent',
    minWidth: 80,
    alignItems: 'center',
    justifyContent: 'center',
  },
  seasonPillText: {
    color: '#FFFFFF',
    fontSize: 14,
    fontWeight: '800',
  },
  percentPill: {
    backgroundColor: '#1D3A71',
    borderRadius: 8,
    paddingHorizontal: 10,
    borderWidth: 2,
    borderColor: 'transparent',
    minWidth: 80,
    alignItems: 'center',
    justifyContent: 'center',
  },
  percentPillText: {
    color: '#FFFFFF',
    fontSize: 14,
    fontWeight: '800',
  },
  percentPillGold: {
    backgroundColor: '#332915',
    borderColor: '#FFD700',
    borderWidth: 2,
  },
  percentPillTextGold: {
    color: '#FFD700',
  },

  // ── Percentile pill ──

  percentilePill: {
    backgroundColor: '#1D3A71',
    borderRadius: 8,
    paddingHorizontal: 8,
    borderWidth: 2,
    borderColor: 'transparent',
    minWidth: 80,
    alignItems: 'center',
    justifyContent: 'center',
  },
  percentilePillGold: {
    backgroundColor: '#332915',
    borderColor: '#FFD700',
  },
  percentilePillText: {
    color: '#FFFFFF',
    fontSize: 14,
    fontWeight: '800',
  },
  percentilePillTextGold: {
    color: '#FFD700',
  },

  // ── FC badge ──

  fcBadge: {
    backgroundColor: '#332915',
    borderColor: '#FFD700',
    borderWidth: 2,
    borderRadius: 8,
    paddingHorizontal: 8,
    minWidth: 80,
    alignItems: 'center',
    justifyContent: 'center',
  },
  fcBadgeText: {
    color: '#FFD700',
    fontSize: 14,
    fontWeight: '800',
  },

  // ── Score + stars row below the main content ──

  scoreStarsRow: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    gap: 4,
    paddingBottom: 2,
  },
  metadataGrid: {
    gap: 8,
    paddingBottom: 2,
    marginTop: 2,
  },
  metadataGridRow: {
    flexDirection: 'row',
    justifyContent: 'center',
    gap: 4,
  },
  metadataGridCell: {
    flex: 1,
    alignItems: 'center',
    justifyContent: 'center',
  },
  metadataCell: {
    flex: 1,
    alignItems: 'center',
    justifyContent: 'center',
  },
  metadataCellFixed: {
    alignItems: 'center',
    justifyContent: 'center',
  },
  metadataCellLeft: {
    flex: 1,
    alignItems: 'flex-start',
    justifyContent: 'center',
  },
  metadataCellRight: {
    flex: 1,
    alignItems: 'flex-end',
    justifyContent: 'center',
  },
  scoreStarsInner: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
  },
  scoreStarsScoreText: {
    color: '#D7DEE8',
    fontSize: 16,
    fontWeight: '700',
  },
  scoreStarsSep: {
    color: '#556677',
    fontSize: 16,
    fontWeight: '400',
  },
});
