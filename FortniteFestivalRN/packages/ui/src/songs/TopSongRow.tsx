/**
 * A compact song row for top-songs / statistics cards.
 *
 * Shows: thumbnail • title/artist • percentile pill • stars/FC text.
 *
 * Built on `SongRowShell` and uses the shared `songRowStyles` / `pillStyles`.
 */
import React, {useMemo} from 'react';
import {Text, View} from 'react-native';
import {SongRowShell} from './SongRowShell';
import {songRowStyles} from '../styles/songRowStyles';
import {pillStyles} from '../styles/pillStyles';

// ── Props ───────────────────────────────────────────────────────────

export interface TopSongRowItem {
  songId: string;
  title: string;
  artist: string;
  /** Leaderboard percentile, e.g. 2.5 = "Top 2.50%". */
  percent?: number;
  stars?: number;
  fullCombo?: boolean;
}

export interface TopSongRowProps {
  item: TopSongRowItem;
  imageUri?: string;
  onPress: () => void;
}

// ── Helpers ─────────────────────────────────────────────────────────

/** Format non-percentile right-side text (stars, FC). */
function formatRightText(item: {stars?: number; fullCombo?: boolean}): string {
  const parts: string[] = [];
  if (typeof item.stars === 'number' && Number.isFinite(item.stars) && item.stars > 0) {
    const displayStars = item.stars >= 6 ? 6 : item.stars;
    parts.push(`${displayStars}★`);
  }
  if (item.fullCombo) {
    parts.push('FC');
  }
  return parts.join(' • ');
}

// ── Component ───────────────────────────────────────────────────────

export const TopSongRow = React.memo(function TopSongRow(props: TopSongRowProps) {
  const {item, imageUri, onPress} = props;

  const rightText = useMemo(() => formatRightText(item), [item]);

  const percentilePill = useMemo(() => {
    if (typeof item.percent !== 'number' || !Number.isFinite(item.percent) || item.percent <= 0) return undefined;
    const display = `Top ${item.percent.toFixed(2)}%`;
    const isTop5 = item.percent <= 5;
    return {display, isTop5};
  }, [item.percent]);

  const rightContent = useMemo(() => {
    if (!percentilePill && !rightText) return undefined;
    return (
      <View style={songRowStyles.songRight}>
        {percentilePill ? (
          <View style={[pillStyles.percentilePill, percentilePill.isTop5 && pillStyles.percentilePillGold]}>
            <Text style={[pillStyles.percentilePillText, percentilePill.isTop5 && pillStyles.percentilePillTextGold]} numberOfLines={1}>
              {percentilePill.display}
            </Text>
          </View>
        ) : null}
        {rightText ? (
          <Text numberOfLines={1} style={songRowStyles.songRightText}>
            {rightText}
          </Text>
        ) : null}
      </View>
    );
  }, [percentilePill, rightText]);

  return (
    <SongRowShell
      title={item.title}
      artist={item.artist}
      imageUri={imageUri}
      onPress={onPress}
      rightContent={rightContent}
    />
  );
});
