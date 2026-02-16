import React from 'react';
import {Image, StyleSheet, Text, View} from 'react-native';
import Svg, {Polygon} from 'react-native-svg';
import {FrostedSurface} from '../FrostedSurface';
import {getInstrumentIconSource} from './instrumentVisuals';
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

export function DifficultyBars(props: {rawDifficulty: number; compact?: boolean; barWidth?: number; barHeight?: number; gap?: number}) {
  const raw = Number.isFinite(props.rawDifficulty) ? props.rawDifficulty : 0;
  const display = Math.max(0, Math.min(6, Math.trunc(raw))) + 1;
  const barW = props.barWidth ?? (props.compact ? 20 : 16);
  const barH = props.barHeight ?? (props.compact ? 40 : 34);
  const offset = Math.min(Math.max(Math.round(barW * 0.26), 1), Math.floor(barW * 0.45));
  const gap = props.gap ?? (props.compact ? 2 : 1);

  return (
    <View
      style={[styles.diffRow, {gap}]}
      accessibilityRole="text"
      accessibilityLabel={`Difficulty ${display} of 7`}
    >
      {Array.from({length: 7}).map((_, idx) => {
        const filled = idx + 1 <= display;
        return (
          <View
            key={idx}
            style={[
              {
                width: barW,
                height: barH,
              },
            ]}
          >
            <Svg width={barW} height={barH}>
              <Polygon
                points={`${offset},0 ${barW},0 ${barW - offset},${barH} 0,${barH}`}
                fill={filled ? '#FFFFFF' : '#666666'}
              />
            </Svg>
          </View>
        );
      })}
    </View>
  );
}

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
    maxWidth: 1080,
    borderRadius: 22,
    padding: 12,
  },
  body: {
    paddingHorizontal: 10,
    paddingVertical: 12,
    gap: 10,
  },
  top: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    gap: 12,
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
    width: 40,
    height: 40,
  },
  instName: {
    color: '#FFFFFF',
    fontWeight: '800',
    fontSize: 16,
  },
  center: {
    alignItems: 'center',
    gap: 8,
  },
  diffRow: {
    flexDirection: 'row',
    alignItems: 'center',
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
  metrics: {
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
});
