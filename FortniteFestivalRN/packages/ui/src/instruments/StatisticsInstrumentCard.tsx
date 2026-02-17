import React from 'react';
import {Image, StyleSheet, Text, View, type StyleProp, type ViewStyle} from 'react-native';
import {FrostedSurface} from '../FrostedSurface';
import {getInstrumentIconSource} from './instrumentVisuals';
import {Colors, Radius, Font, LineHeight, Gap, Opacity, MaxWidth} from '../theme';
import type {InstrumentKey} from '@festival/core';

// ── Public data type ────────────────────────────────────────────────

export interface StatisticsCardData {
  instrumentKey: InstrumentKey;
  instrumentLabel: string;
  totalSongsInLibrary: number;
  songsPlayed: number;
  completionPercent: number;
  fcCount: number;
  fcPercent: number;
  goldStarCount: number;
  fiveStarCount: number;
  fourStarCount: number;
  averageAccuracy: number;
  bestAccuracy: number;
  averageStars: number;
  bestRank: number;
  bestRankFormatted: string;
  weightedPercentileFormatted: string;
  top1PercentCount: number;
  top5PercentCount: number;
  top10PercentCount: number;
  top25PercentCount: number;
  top50PercentCount: number;
  below50PercentCount: number;
}

// ── Sub-components ──────────────────────────────────────────────────

const StatCell = React.memo(function StatCell(props: {label: string; value: string; compact?: boolean}) {
  return (
    <View style={props.compact ? compactStyles.statCell : styles.statCell}>
      <Text style={props.compact ? compactStyles.statLabel : styles.statLabel}>{props.label}</Text>
      <Text style={props.compact ? compactStyles.statValue : styles.statValue}>{props.value}</Text>
    </View>
  );
});

export const DistSeg = React.memo(function DistSeg(props: {color: string; count: number; total: number; compact?: boolean}) {
  if (props.count <= 0 || props.total <= 0) return null;
  const h = props.compact ? 8 : 12;
  return <View style={[styles.distSeg, {backgroundColor: props.color, flex: props.count, height: h}]} />;
});

export const LegendItem = React.memo(function LegendItem(props: {label: string; color: string; value: number; compact?: boolean}) {
  return (
    <View style={styles.legendItem}>
      <View style={[props.compact ? compactStyles.legendSwatch : styles.legendSwatch, {backgroundColor: props.color}]} />
      <Text style={props.compact ? compactStyles.legendText : styles.legendText}>
        {props.label}: {props.value}
      </Text>
    </View>
  );
});

// ── Main card ───────────────────────────────────────────────────────

export const StatisticsInstrumentCard = React.memo(function StatisticsInstrumentCard(props: {data: StatisticsCardData; compact?: boolean; style?: StyleProp<ViewStyle>}) {
  const s = props.data;
  const c = Boolean(props.compact);

  const pctTotal =
    s.top1PercentCount +
    s.top5PercentCount +
    s.top10PercentCount +
    s.top25PercentCount +
    s.top50PercentCount +
    s.below50PercentCount;

  return (
    <FrostedSurface style={[c ? compactStyles.card : styles.card, props.style]} tint="dark" intensity={18}>
      <View style={styles.cardHeaderRow}>
        <Image source={getInstrumentIconSource(s.instrumentKey)} style={c ? compactStyles.instrumentIcon : styles.instrumentIcon} />
        <View style={styles.cardHeaderText}>
          <Text style={c ? compactStyles.cardTitle : styles.cardTitle}>{s.instrumentLabel}</Text>
          <Text style={c ? compactStyles.cardSubtitle : styles.cardSubtitle}>
            {s.songsPlayed} of {s.totalSongsInLibrary} songs played ({s.completionPercent.toFixed(1)}%)
          </Text>
        </View>
      </View>

      <View style={c ? compactStyles.statsGrid : styles.statsGrid}>
        <StatCell label="FCs" value={`${s.fcCount} (${s.fcPercent.toFixed(1)}%)`} compact={c} />
        <StatCell label="Gold Stars" value={`${s.goldStarCount}`} compact={c} />
        <StatCell label="5 Stars" value={`${s.fiveStarCount}`} compact={c} />
        <StatCell label="4 Stars" value={`${s.fourStarCount}`} compact={c} />
        <StatCell label="Average Accuracy" value={`${s.averageAccuracy.toFixed(2)}%`} compact={c} />
        <StatCell label="Best Accuracy" value={`${s.bestAccuracy.toFixed(2)}%`} compact={c} />
        <StatCell label="Average Stars" value={`${s.averageStars.toFixed(2)}`} compact={c} />
        <StatCell label="Best Rank" value={s.bestRank > 0 ? s.bestRankFormatted : '—'} compact={c} />
        <StatCell label="Weighted Percentile" value={s.weightedPercentileFormatted !== 'N/A' ? s.weightedPercentileFormatted : '—'} compact={c} />
      </View>

      {s.songsPlayed > 0 && (
        <View style={c ? compactStyles.distWrap : styles.distWrap}>
          <Text style={c ? compactStyles.sectionTitle : styles.sectionTitle}>Percentile Distribution</Text>

          {pctTotal > 0 ? (
            <View style={c ? compactStyles.distBar : styles.distBar}>
              <DistSeg color="#27ae60" count={s.top1PercentCount} total={pctTotal} compact={c} />
              <DistSeg color="#2ecc71" count={s.top5PercentCount} total={pctTotal} compact={c} />
              <DistSeg color="#f1c40f" count={s.top10PercentCount} total={pctTotal} compact={c} />
              <DistSeg color="#e67e22" count={s.top25PercentCount} total={pctTotal} compact={c} />
              <DistSeg color="#e74c3c" count={s.top50PercentCount} total={pctTotal} compact={c} />
              <DistSeg color="#7f8c8d" count={s.below50PercentCount} total={pctTotal} compact={c} />
            </View>
          ) : (
            <Text style={styles.muted}>No percentile data yet.</Text>
          )}

          <View style={styles.legendGrid}>
            <LegendItem label="Top 1%" color="#27ae60" value={s.top1PercentCount} compact={c} />
            <LegendItem label="Top 5%" color="#2ecc71" value={s.top5PercentCount} compact={c} />
            <LegendItem label="Top 10%" color="#f1c40f" value={s.top10PercentCount} compact={c} />
            <LegendItem label="Top 25%" color="#e67e22" value={s.top25PercentCount} compact={c} />
            <LegendItem label="Top 50%" color="#e74c3c" value={s.top50PercentCount} compact={c} />
            <LegendItem label="> 50%" color="#7f8c8d" value={s.below50PercentCount} compact={c} />
          </View>
        </View>
      )}
    </FrostedSurface>
  );
});

// ── Styles ──────────────────────────────────────────────────────────

const styles = StyleSheet.create({
  card: {
    borderRadius: Radius.lg,
    borderWidth: 2,
    borderColor: 'rgba(255,255,255,0.15)',
    padding: 14,
    gap: Gap.lg,
    maxWidth: MaxWidth.card,
    width: '100%',
  },
  cardHeaderRow: {
    flexDirection: 'row',
    gap: Gap.xl,
    alignItems: 'center',
  },
  instrumentIcon: {
    width: 48,
    height: 48,
    resizeMode: 'contain',
  },
  cardHeaderText: {
    flex: 1,
    gap: Gap.xs,
  },
  cardTitle: {
    color: Colors.textPrimary,
    fontSize: Font.xl,
    fontWeight: '800',
  },
  cardSubtitle: {
    color: Colors.textSecondary,
    fontSize: 13,
    opacity: Opacity.pressed,
  },
  statsGrid: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    columnGap: 16,
    rowGap: Gap.lg,
    marginTop: Gap.section,
  },
  statCell: {
    width: '47%',
    gap: Gap.xs,
  },
  statLabel: {
    color: Colors.textSecondary,
    fontSize: Font.sm,
    opacity: Opacity.pressed,
  },
  statValue: {
    color: Colors.textPrimary,
    fontSize: Font.md,
    fontWeight: '700',
  },
  distWrap: {
    gap: Gap.md,
    marginTop: Gap.section,
  },
  sectionTitle: {
    color: Colors.textPrimary,
    fontSize: Font.md,
    fontWeight: '800',
  },
  muted: {
    color: Colors.textSecondary,
    opacity: 0.8,
    fontSize: 13,
  },
  distBar: {
    flexDirection: 'row',
    height: Gap.xl,
    borderRadius: 6,
    overflow: 'hidden',
    backgroundColor: 'rgba(255,255,255,0.08)',
  },
  distSeg: {
    height: Gap.xl,
  },
  legendGrid: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    rowGap: 6,
    columnGap: Gap.xl,
  },
  legendItem: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 6,
  },
  legendSwatch: {
    width: Gap.lg,
    height: Gap.lg,
    borderRadius: 3,
  },
  legendText: {
    color: Colors.textSecondary,
    fontSize: Font.sm,
    opacity: 0.9,
  },
});

const compactStyles = StyleSheet.create({
  card: {
    borderRadius: Radius.md,
    borderWidth: 1.5,
    borderColor: 'rgba(255,255,255,0.15)',
    padding: Gap.lg,
    gap: 6,
  },
  instrumentIcon: {
    width: 32,
    height: 32,
    resizeMode: 'contain',
  },
  cardTitle: {
    color: Colors.textPrimary,
    fontSize: 17,
    fontWeight: '800',
  },
  cardSubtitle: {
    color: Colors.textSecondary,
    fontSize: Font.sm,
    opacity: Opacity.pressed,
  },
  statsGrid: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    columnGap: Gap.lg,
    rowGap: Gap.sm,
    marginTop: Gap.md,
  },
  statCell: {
    width: '47%',
    gap: 1,
  },
  statLabel: {
    color: Colors.textSecondary,
    fontSize: Font.xs,
    opacity: Opacity.pressed,
  },
  statValue: {
    color: Colors.textPrimary,
    fontSize: 13,
    fontWeight: '700',
  },
  distWrap: {
    gap: Gap.sm,
    marginTop: Gap.md,
  },
  sectionTitle: {
    color: Colors.textPrimary,
    fontSize: 13,
    fontWeight: '800',
  },
  distBar: {
    flexDirection: 'row',
    height: Gap.md,
    borderRadius: Gap.sm,
    overflow: 'hidden',
    backgroundColor: 'rgba(255,255,255,0.08)',
  },
  legendSwatch: {
    width: 7,
    height: 7,
    borderRadius: 2,
  },
  legendText: {
    color: Colors.textSecondary,
    fontSize: Font.xs,
    opacity: 0.9,
  },
});
