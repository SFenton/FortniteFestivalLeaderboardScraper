import React, {useMemo} from 'react';
import {Image, Pressable, StyleSheet, Text, View} from 'react-native';
import {MarqueeText} from '../MarqueeText';
import {getInstrumentIconSource, getInstrumentStatusVisual} from '../instruments/instrumentVisuals';
import {buildSongDisplayRow, type InstrumentShowSettings} from '../../app/songs/songFiltering';
import {formatIntegerWithCommas} from '../../app/format/formatters';
import type {LeaderboardData, Song} from '../../core/models';
import type {SuggestionSongItem} from '../../core/suggestions/types';

const STAR_WHITE_ICON = require('../../assets/icons/star_white.png');
const STAR_GOLD_ICON = require('../../assets/icons/star_gold.png');

function formatRight(item: SuggestionSongItem): string {
  const parts: string[] = [];

  if (typeof item.percent === 'number' && Number.isFinite(item.percent) && item.percent > 0) {
    parts.push(`${item.percent.toFixed(2)}%`);
  }

  if (typeof item.stars === 'number' && Number.isFinite(item.stars) && item.stars > 0) {
    const displayStars = item.stars >= 6 ? 6 : item.stars;
    parts.push(`${displayStars}★`);
  }

  if (item.fullCombo) {
    parts.push('FC');
  }

  return parts.join(' • ');
}

export const SuggestionSongRow = React.memo(function SuggestionSongRow(props: {
  categoryKey: string;
  item: SuggestionSongItem;
  song?: Song;
  leaderboardData?: LeaderboardData;
  settings: InstrumentShowSettings;
  useCompactLayout: boolean;
  /** When true, hide the album art thumbnail. */
  hideArt?: boolean;
  onOpenSong: (songId: string, title: string) => void;
}) {
  const {item, song, leaderboardData, settings} = props;

  const year = song?.track?.ry;

  const imageUri = song?.imagePath ?? song?.track?.au;

  const isUnfcCategory = props.categoryKey.startsWith('unfc_');
  const isFcTheseNextCategory = props.categoryKey.startsWith('near_fc_any');
  const isNearFcRelaxedCategory = props.categoryKey.startsWith('near_fc_relaxed');
  const isGoldStarPushCategory = props.categoryKey.startsWith('almost_six_star') || props.categoryKey.startsWith('more_stars');
  const isFirstPlaysMixedCategory = props.categoryKey.startsWith('first_plays_mixed');
  const isVarietyPackCategory = props.categoryKey === 'variety_pack';
  const isArtistSamplerCategory = props.categoryKey.startsWith('artist_sampler_');
  const isArtistUnplayedCategory = props.categoryKey.startsWith('artist_unplayed_');
  const isSameNameNearFcCategory = props.categoryKey.startsWith('samename_nearfc_');
  const isSameNameTitleCategory = props.categoryKey.startsWith('samename_') && !isSameNameNearFcCategory;
  const isUnplayedAnyCategory = props.categoryKey === 'unplayed_any' || props.categoryKey.startsWith('unplayed_any_decade_');
  const isStarGainsCategory = props.categoryKey.startsWith('star_gains');
  const isPercentileCategory = props.categoryKey.startsWith('almost_elite') || props.categoryKey.startsWith('pct_push');

  const right = useMemo(() => {
    if (isUnfcCategory) return '';
    return formatRight(item);
  }, [isUnfcCategory, item]);

  const unfcPercent = useMemo(() => {
    if (!isUnfcCategory) return undefined;
    if (typeof item.percent !== 'number' || !Number.isFinite(item.percent)) return undefined;
    const pctInt = Math.max(0, Math.min(99, Math.floor(item.percent)));
    return String(pctInt).padStart(2, '0');
  }, [isUnfcCategory, item.percent]);

  const showUnfcBadge = unfcPercent != null;

  const rightInstrumentKey = (isFcTheseNextCategory || isNearFcRelaxedCategory || isGoldStarPushCategory || isFirstPlaysMixedCategory || isStarGainsCategory || isPercentileCategory) ? item.instrumentKey : undefined;
  const rightInstrumentKeyFinal = (rightInstrumentKey || (isSameNameNearFcCategory ? item.instrumentKey : undefined));
  const showRightInstrumentIcon = !!rightInstrumentKeyFinal;

  const percentilePill = useMemo(() => {
    if (!isPercentileCategory) return undefined;
    const display = item.percentileDisplay;
    if (!display) return undefined;
    // Highlight gold when top 5% or better
    const isTop5 = display === 'Top 1%' || display === 'Top 2%' || display === 'Top 3%' || display === 'Top 4%' || display === 'Top 5%';
    return {display, isTop5};
  }, [isPercentileCategory, item.percentileDisplay]);

  const starGainsStarCount = useMemo(() => {
    if (!isStarGainsCategory) return 0;

    const instr = rightInstrumentKeyFinal;
    const tr = instr && leaderboardData ? (leaderboardData as any)[instr] : undefined;
    const nFromTracker = tr?.numStars;
    if (typeof nFromTracker === 'number' && Number.isFinite(nFromTracker) && nFromTracker > 0) {
      return Math.max(0, Math.min(6, Math.floor(nFromTracker)));
    }

    if (typeof item.stars !== 'number' || !Number.isFinite(item.stars) || item.stars <= 0) return 0;
    return Math.max(0, Math.min(6, Math.floor(item.stars)));
  }, [isStarGainsCategory, item.stars, leaderboardData, rightInstrumentKeyFinal]);

  const starGainsStarsVisual = useMemo(() => {
    if (!isStarGainsCategory || starGainsStarCount <= 0) return null;
    const allGold = starGainsStarCount >= 6;
    const displayCount = allGold ? 5 : Math.max(1, starGainsStarCount);
    const source = allGold ? STAR_GOLD_ICON : STAR_WHITE_ICON;
    const instr = rightInstrumentKeyFinal;
    const tr = instr && leaderboardData ? (leaderboardData as any)[instr] : undefined;
    const scoreValue = tr?.initialized ? tr?.maxScore : undefined;
    const scoreDisplay = typeof scoreValue === 'number' && Number.isFinite(scoreValue) ? formatIntegerWithCommas(scoreValue) : '';
    return {displayCount, source, scoreDisplay};
  }, [isStarGainsCategory, leaderboardData, rightInstrumentKeyFinal, starGainsStarCount]);

  const row = useMemo(() => {
    if (!song) return null;
    return buildSongDisplayRow({song, leaderboardData, settings});
  }, [leaderboardData, settings, song]);

  const hideRightSideCompletely = isVarietyPackCategory || isArtistSamplerCategory || isArtistUnplayedCategory || isSameNameTitleCategory || isUnplayedAnyCategory;

  return (
    <Pressable
      onPress={() => props.onOpenSong(item.songId, item.title)}
      style={styles.songRowPressable}
      accessibilityRole="button"
      accessibilityLabel={`Open ${item.title}`}
    >
      {({pressed}) => (
        <View style={[styles.songRowPressable, pressed && styles.songRowInnerPressed]}>
          <View style={styles.songRowInner}>
            <View style={styles.songLeft}>
              {!props.hideArt && (
                <View style={styles.thumbWrap}>
                  {imageUri ? (
                    <Image source={{uri: imageUri}} style={styles.thumb} resizeMode="cover" />
                  ) : (
                    <View style={styles.thumbPlaceholder} />
                  )}
                </View>
              )}

              <View style={styles.songRowText}>
                <MarqueeText text={item.title} textStyle={styles.songTitle} />
                <MarqueeText text={`${item.artist}${item.artist && year ? ' • ' : ''}${year ?? ''}`} textStyle={styles.songMeta} />
              </View>
            </View>

            <View style={styles.songRight}>
              {!hideRightSideCompletely && !showUnfcBadge && !showRightInstrumentIcon && !props.useCompactLayout && row ? (
                <View style={styles.instrumentRow}>
                  {row.instrumentStatuses.map(s => {
                    const {fill, stroke} = getInstrumentStatusVisual({hasScore: s.hasScore, isFullCombo: s.isFullCombo});
                    const opacity = s.isEnabled ? 1 : 0.35;
                    return (
                      <View key={s.instrumentKey} style={[styles.instrumentChip, {backgroundColor: fill, borderColor: stroke, opacity}]}>
                        <Image source={getInstrumentIconSource(s.instrumentKey)} style={styles.instrumentIcon} resizeMode="contain" />
                      </View>
                    );
                  })}
                </View>
              ) : null}
              {hideRightSideCompletely ? null : isPercentileCategory && percentilePill ? (
                <View style={styles.songRightPercentile}>
                  <View style={[styles.percentilePill, percentilePill.isTop5 && styles.percentilePillGold]}>
                    <Text style={[styles.percentilePillText, percentilePill.isTop5 && styles.percentilePillTextGold]} numberOfLines={1}>
                      {percentilePill.display}
                    </Text>
                  </View>
                  {showRightInstrumentIcon ? (
                    <Image source={getInstrumentIconSource(rightInstrumentKeyFinal)} style={styles.fcTheseInstrumentIcon} resizeMode="contain" />
                  ) : null}
                </View>
              ) : showRightInstrumentIcon ? (
                <View style={styles.songRightSingle}>
                  <Image source={getInstrumentIconSource(rightInstrumentKeyFinal)} style={styles.fcTheseInstrumentIcon} resizeMode="contain" />
                </View>
              ) : showUnfcBadge ? (
                <View style={styles.songRightSingle}>
                  <Text style={styles.unfcPctText}>{unfcPercent}%</Text>
                </View>
              ) : right ? (
                <View style={styles.songRightSingle}>
                  <Text style={styles.songRightText}>{right}</Text>
                </View>
              ) : null}
            </View>
          </View>
          {starGainsStarsVisual ? (
            <View style={styles.starGainsStarsRow}>
              <View style={styles.starGainsStarsInner}>
                {Array.from({length: starGainsStarsVisual.displayCount}).map((_, i) => (
                  <Image key={i} source={starGainsStarsVisual.source} style={styles.starGainsStarIcon} resizeMode="contain" />
                ))}
                {starGainsStarsVisual.scoreDisplay ? (
                  <Text style={styles.starGainsScoreText}>• {starGainsStarsVisual.scoreDisplay}</Text>
                ) : null}
              </View>
            </View>
          ) : null}
        </View>
      )}
    </Pressable>
  );
}, (prev, next) => (
  prev.categoryKey === next.categoryKey &&
  prev.item === next.item &&
  prev.song === next.song &&
  prev.leaderboardData === next.leaderboardData &&
  prev.settings === next.settings &&
  prev.useCompactLayout === next.useCompactLayout &&
  prev.hideArt === next.hideArt &&
  prev.onOpenSong === next.onOpenSong
));

const styles = StyleSheet.create({
  songRowPressable: {},
  songRowInner: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: 10,
    paddingVertical: 10,
    paddingHorizontal: 10,
  },
  songRowInnerPressed: {
    opacity: 0.85,
  },
  songLeft: {
    flexGrow: 1,
    flexShrink: 1,
    minWidth: 0,
    flexDirection: 'row',
    alignItems: 'center',
    gap: 10,
  },
  thumbWrap: {
    width: 44,
    height: 44,
    borderRadius: 10,
    overflow: 'hidden',
    backgroundColor: '#0F172A',
  },
  thumb: {
    width: '100%',
    height: '100%',
  },
  thumbPlaceholder: {
    width: '100%',
    height: '100%',
    backgroundColor: '#111827',
  },
  songRowText: {
    flexGrow: 1,
    flexShrink: 1,
    minWidth: 0,
    gap: 2,
  },
  songRight: {
    flexShrink: 0,
    alignItems: 'flex-end',
    justifyContent: 'center',
    gap: 4,
  },
  songRightSingle: {
    height: 24,
    justifyContent: 'center',
    alignItems: 'center',
  },
  unfcPctText: {
    color: '#FFFFFF',
    fontSize: 16,
    fontWeight: '800',
    letterSpacing: 0.2,
    minWidth: 56,
    includeFontPadding: false,
    lineHeight: 18,
    textAlign: 'center',
    textAlignVertical: 'center',
  },
  fcTheseInstrumentIcon: {
    width: 24,
    height: 24,
    opacity: 0.92,
    alignSelf: 'center',
  },
  starGainsStarsRow: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    gap: 4,
    paddingBottom: 6,
    paddingHorizontal: 10,
  },
  starGainsStarsInner: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
  },
  starGainsStarIcon: {
    width: 14,
    height: 14,
    opacity: 0.95,
  },
  starGainsScoreText: {
    marginLeft: 8,
    color: '#D7DEE8',
    fontSize: 12,
    fontWeight: '700',
  },
  songRightPercentile: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 6,
  },
  percentilePill: {
    backgroundColor: '#1D3A71',
    borderRadius: 10,
    paddingHorizontal: 8,
    paddingVertical: 3,
    borderWidth: 2,
    borderColor: 'transparent',
    alignItems: 'center',
    justifyContent: 'center',
  },
  percentilePillGold: {
    backgroundColor: '#332915',
    borderColor: '#FFD700',
  },
  percentilePillText: {
    color: '#FFFFFF',
    fontWeight: '800',
    fontSize: 12,
  },
  percentilePillTextGold: {
    color: '#FFD700',
  },
  songTitle: {
    color: '#FFFFFF',
    fontWeight: '700',
    fontSize: 14,
  },
  songMeta: {
    color: '#9AA6B2',
    fontSize: 12,
  },
  songRightText: {
    color: '#D7DEE8',
    fontSize: 12,
    fontWeight: '700',
  },
  instrumentRow: {
    flexDirection: 'row',
    gap: 6,
    alignItems: 'center',
    justifyContent: 'flex-end',
  },
  instrumentChip: {
    width: 28,
    height: 28,
    borderRadius: 14,
    borderWidth: 2,
    alignItems: 'center',
    justifyContent: 'center',
  },
  instrumentIcon: {
    width: 18,
    height: 18,
  },
});
