import React from 'react';
import {Image, StyleSheet, Text, View} from 'react-native';
import {FrostedSurface} from '../FrostedSurface';
import {getInstrumentIconSource} from './instrumentVisuals';
import {DifficultyBars} from './DifficultyBars';
import {Colors, Radius, Font, Gap, MaxWidth, Size} from '../theme';
import type {InstrumentKey} from '@festival/core';

const STAR_WHITE_ICON = require('../../assets/icons/star_white.png');
const STAR_GOLD_ICON = require('../../assets/icons/star_gold.png');

const CARD_BG = 'rgba(18,24,38,0.78)';

// ── Public types ────────────────────────────────────────────────────

export interface InstrumentCardData {
  key: InstrumentKey;
  name: string;
  hasScore: boolean;
  isFullCombo: boolean;
  starsCount: number;
  rawDifficulty: number;
  gameDifficultyDisplay?: string;
  scoreDisplay: string;
  percentDisplay: string;
  seasonDisplay: string;
  percentileDisplay: string;
  rankOutOfDisplay: string;
  isTop5Percentile: boolean;
}
const DIFF_FULL_LABELS: Record<string, string> = {
  E: 'Easy',
  M: 'Medium',
  H: 'Hard',
  X: 'Expert',
};

// ── Sub-components ──────────────────────────────────────────────────

export function MetricPill(props: {value: string; highlight?: boolean; highlightKind?: 'gold'}) {
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

export function MetricCell(props: {label: string; value: string; highlight?: boolean; highlightKind?: 'gold'}) {
  return (
    <View style={styles.metricCell}>
      <Text style={styles.metricLabel}>{props.label}</Text>
      <MetricPill value={props.value} highlight={props.highlight} highlightKind={props.highlightKind} />
    </View>
  );
}

export {DifficultyBars} from './DifficultyBars';
export type {DifficultyBarsProps} from './DifficultyBars';

export function StarsVisual(props: {
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
          key={idx}
          style={[styles.starCircle, {width: size, height: size, borderColor: outline}]}
        >
          <Image source={source} style={{width: inner, height: inner}} resizeMode="contain" />
        </View>
      ))}
    </View>
  );
}

const getDifficultyFullName = (display?: string): string => {
  if (!display) return 'N/A';
  return DIFF_FULL_LABELS[display] ?? 'N/A';
};

// ── Main card ───────────────────────────────────────────────────────

export function InstrumentCard(props: {data: InstrumentCardData}) {
  const r = props.data;
  return (
    <FrostedSurface style={styles.card} tint="dark" intensity={22} fallbackColor={CARD_BG}>
      <View style={styles.body}>
        <View style={styles.top}>
          <View style={styles.iconCircle}>
            <Image source={getInstrumentIconSource(r.key)} style={styles.icon} resizeMode="contain" />
          </View>
          <Text style={styles.instName} numberOfLines={1}>
            {r.name}
          </Text>
        </View>

        <View style={styles.center}>
          <DifficultyBars rawDifficulty={r.rawDifficulty} compact />
          <StarsVisual hasScore={r.hasScore} starsCount={r.starsCount} isFullCombo={r.isFullCombo} compact />
        </View>

        <View style={styles.metrics}>
          <View style={styles.metricRow2}>
            <MetricCell label="Score" value={r.scoreDisplay} />
            <MetricCell label="Percent Hit" value={r.percentDisplay} highlight={r.isFullCombo} highlightKind="gold" />
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
          <View style={styles.metricRow1}>
            <MetricCell label="Difficulty" value={getDifficultyFullName(r.gameDifficultyDisplay)} />
          </View>
        </View>
      </View>
    </FrostedSurface>
  );
}

// ── Styles ──────────────────────────────────────────────────────────

const styles = StyleSheet.create({
  card: {
    width: '100%',
    maxWidth: MaxWidth.card,
    borderRadius: 22,
    padding: Gap.xl,
  },
  body: {
    paddingHorizontal: Gap.lg,
    paddingVertical: Gap.xl,
    gap: Gap.lg,
  },
  top: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    gap: Gap.xl,
  },
  iconCircle: {
    width: 48,
    height: 48,
    borderRadius: 24,
    backgroundColor: '#4B0F63',
    alignItems: 'center',
    justifyContent: 'center',
  },
  icon: {
    width: Size.iconLg,
    height: Size.iconLg,
  },
  instName: {
    color: Colors.textPrimary,
    fontWeight: '800',
    fontSize: Font.lg,
  },
  center: {
    alignItems: 'center',
    gap: Gap.md,
  },
  starRow: {
    flexDirection: 'row',
    gap: Gap.sm,
    alignItems: 'center',
    justifyContent: 'center',
  },
  starCircle: {
    borderRadius: Radius.full,
    borderWidth: 2,
    backgroundColor: 'transparent',
    alignItems: 'center',
    justifyContent: 'center',
  },
  metrics: {
    gap: Gap.xl,
  },
  metricRow2: {
    flexDirection: 'row',
    gap: Gap.xl,
  },
  metricRow1: {
    flexDirection: 'row',
  },
  metricCell: {
    flex: 1,
    gap: Gap.xs,
  },
  metricLabel: {
    color: Colors.textPrimary,
    fontWeight: '800',
    fontSize: Font.sm,
    textAlign: 'center',
  },
  pill: {
    backgroundColor: '#1D3A71',
    borderRadius: Radius.sm,
    paddingHorizontal: Gap.md,
    paddingVertical: Gap.sm,
    borderWidth: 2,
    borderColor: 'transparent',
    minHeight: 30,
    alignItems: 'center',
    justifyContent: 'center',
  },
  pillGold: {
    backgroundColor: '#332915',
    borderColor: Colors.gold,
  },
  pillText: {
    color: Colors.textPrimary,
    fontWeight: '800',
  },
  pillTextGold: {
    color: Colors.gold,
  },
});
