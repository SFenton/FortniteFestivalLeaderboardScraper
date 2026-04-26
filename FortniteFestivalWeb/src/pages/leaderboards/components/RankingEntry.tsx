/* eslint-disable react/forbid-dom-props -- useStyles pattern */
/**
 * A single row in a rankings leaderboard.
 * Displays rank, player name, rating value, and songs played.
 */
import { memo, useMemo } from 'react';
import { Colors, Font, FontVariant, Gap, Layout, MetadataSize, TextAlign, Weight, transition, TRANSITION_MS, truncate } from '@festival/theme';
import PercentilePill from '../../../components/songs/metadata/PercentilePill';

const TEN_DIGIT_SCORE_MIN_WIDTH = Math.ceil('1,000,000,000'.length * Layout.rankCharWidth) + Layout.rankColumnPadding;

export interface RankingEntryProps {
  rank: number;
  displayName: string;
  /** Formatted rating value string (e.g. "42.3%", "1,250,000"). */
  ratingLabel: string;
  /** Songs played count (e.g. "142 / 200"). */
  songsLabel?: string;
  /** Percentile display string (e.g. "Top 0.05%"). When present, renders a PercentilePill instead of songsLabel. */
  percentileDisplay?: string;
  /** Formatted rating value for a value pill (e.g. "0.012"). Shown to the left of the percentile pill. */
  valueDisplay?: string;
  /** Background color for the value pill (e.g. from rankColor()). */
  valueColor?: string;
  /** Raw average percentile value display for percentile-based metrics (e.g. "Top 0.56%"). */
  percentileValueDisplay?: string;
  /** Label shown before the Bayesian rank value pill. */
  bayesianRankLabel?: string;
  /** Raw Bayesian-calculated metric value pill display (e.g. "0.0409"). */
  bayesianRankDisplay?: string;
  /** Background color for the Bayesian value pill. */
  bayesianRankColor?: string;
  /** Shared min width for percentile value pills. */
  percentileValueMinWidth?: number;
  /** Shared min width for Bayesian value pills. */
  bayesianRankMinWidth?: number;
  /** On compact percentile rows, place songs + percentile + Bayesian value on a second row. */
  twoRowPercentileMetadata?: boolean;
  /** When set, renders ratingLabel as a PercentilePill with the given tier instead of plain text. */
  ratingPillTier?: 'top1' | 'top5' | 'default';
  /** When true, songs label uses primary (white) text at standard font size. */
  songsLabelPrimary?: boolean;
  /** When true, renders the first side of an `X / Y` songs label in gold. */
  songsLabelGoldPrefix?: boolean;
  isPlayer?: boolean;
  /** Pixel width for the rank column. Computed from the longest rank in the list. */
  rankWidth?: number;
  /** Reserve space for a formatted 10-digit score (e.g. 1,000,000,000). */
  reserveTenDigitScoreWidth?: boolean;
}

export const RankingEntry = memo(function RankingEntry({
  rank,
  displayName,
  ratingLabel,
  songsLabel,
  percentileDisplay,
  valueDisplay,
  valueColor,
  percentileValueDisplay,
  bayesianRankLabel = 'Bayesian-Calculated Rank:',
  bayesianRankDisplay,
  bayesianRankColor,
  percentileValueMinWidth,
  bayesianRankMinWidth,
  twoRowPercentileMetadata,
  ratingPillTier,
  songsLabelPrimary,
  songsLabelGoldPrefix,
  isPlayer,
  rankWidth,
  reserveTenDigitScoreWidth,
}: RankingEntryProps) {
  const s = useStyles(isPlayer, rankWidth, reserveTenDigitScoreWidth);
  const hasPercentileMetricValue = !!percentileValueDisplay;

  if (hasPercentileMetricValue && twoRowPercentileMetadata) {
    return (
      <div style={s.twoRowLayout}>
        <div style={s.twoRowPrimary}>
          <span style={s.colRank}>#{rank.toLocaleString()}</span>
          <span style={s.colName}>{displayName}</span>
        </div>
        <div style={s.twoRowMetadata}>
          {renderPercentileMetadata({
            songsLabel,
            songsLabelPrimary,
            songsLabelGoldPrefix,
            percentileValueDisplay,
            percentileValueMinWidth,
            bayesianRankLabel,
            bayesianRankDisplay,
            bayesianRankColor,
            bayesianRankMinWidth,
            isPlayer,
            s,
          })}
        </div>
      </div>
    );
  }

  return (
    <>
      <span style={s.colRank}>#{rank.toLocaleString()}</span>
      <span style={s.colName}>{displayName}</span>
      {!hasPercentileMetricValue && valueDisplay && <PercentilePill display={valueDisplay} color={valueColor} minWidth={MetadataSize.valuePillMinWidth} bold={isPlayer} />}
      {!hasPercentileMetricValue && !valueDisplay && (percentileDisplay ? <PercentilePill display={percentileDisplay} bold={isPlayer} /> : songsLabel && renderSongsLabel(songsLabel, songsLabelPrimary || songsLabelGoldPrefix, !!songsLabelGoldPrefix, s))}
      {hasPercentileMetricValue && renderPercentileMetadata({
        songsLabel,
        songsLabelPrimary,
        songsLabelGoldPrefix,
        percentileValueDisplay,
        percentileValueMinWidth,
        bayesianRankLabel,
        bayesianRankDisplay,
        bayesianRankColor,
        bayesianRankMinWidth,
        isPlayer,
        s,
      })}
      {ratingPillTier ? <PercentilePill display={ratingLabel} tier={ratingPillTier} bold={isPlayer} /> : ratingLabel && <span style={s.colRating}>{ratingLabel}</span>}
    </>
  );
});

function renderPercentileMetadata({
  songsLabel,
  songsLabelPrimary,
  songsLabelGoldPrefix,
  percentileValueDisplay,
  percentileValueMinWidth,
  bayesianRankLabel,
  bayesianRankDisplay,
  bayesianRankColor,
  bayesianRankMinWidth,
  isPlayer,
  s,
}: {
  songsLabel?: string;
  songsLabelPrimary?: boolean;
  songsLabelGoldPrefix?: boolean;
  percentileValueDisplay?: string;
  percentileValueMinWidth?: number;
  bayesianRankLabel: string;
  bayesianRankDisplay?: string;
  bayesianRankColor?: string;
  bayesianRankMinWidth?: number;
  isPlayer?: boolean;
  s: ReturnType<typeof useStyles>;
}) {
  return (
    <>
      {songsLabel && renderSongsLabel(songsLabel, songsLabelPrimary || songsLabelGoldPrefix, !!songsLabelGoldPrefix, s)}
      <PercentilePill display={percentileValueDisplay} minWidth={percentileValueMinWidth} bold={isPlayer} />
      {bayesianRankDisplay && <span style={s.bayesianRankLabel}>{bayesianRankLabel}</span>}
      {bayesianRankDisplay && <PercentilePill display={bayesianRankDisplay} color={bayesianRankColor} minWidth={bayesianRankMinWidth ?? MetadataSize.valuePillMinWidth} bold={isPlayer} />}
    </>
  );
}

function renderSongsLabel(
  songsLabel: string,
  primary: boolean | undefined,
  goldPrefix: boolean,
  s: ReturnType<typeof useStyles>,
) {
  const style = primary ? s.colSongsPrimary : s.colSongs;
  if (!goldPrefix) return <span style={style}>{songsLabel}</span>;

  const match = songsLabel.match(/^(.+?)(\s*\/\s*.*)$/);
  if (!match) return <span style={style}>{songsLabel}</span>;

  return (
    <span style={style}>
      <span style={s.colSongsGoldPrefix}>{match[1]}</span>
      {match[2]}
    </span>
  );
}

function useStyles(isPlayer?: boolean, rankWidth?: number, reserveTenDigitScoreWidth?: boolean) {
  return useMemo(() => ({
    colRank: {
      width: rankWidth ?? Layout.rankColumnWidth,
      flexShrink: 0,
      color: Colors.textPrimary,
      fontSize: Font.md,
      fontVariantNumeric: FontVariant.tabularNums,
      transition: transition('width', TRANSITION_MS),
      ...(isPlayer ? { fontWeight: Weight.bold } : undefined),
    },
    colName: {
      ...truncate,
      flex: 1,
      minWidth: 0,
      ...(isPlayer ? { fontWeight: Weight.bold } : undefined),
    },
    colSongs: {
      flexShrink: 0,
      fontSize: Font.sm,
      color: Colors.textSecondary,
      fontVariantNumeric: FontVariant.tabularNums,
      textAlign: TextAlign.right,
      ...(isPlayer ? { fontWeight: Weight.bold } : undefined),
    },
    colSongsPrimary: {
      flexShrink: 0,
      fontSize: Font.md,
      color: Colors.textPrimary,
      fontVariantNumeric: FontVariant.tabularNums,
      textAlign: TextAlign.right,
      ...(isPlayer ? { fontWeight: Weight.bold } : undefined),
    },
    colSongsGoldPrefix: {
      color: Colors.gold,
    },
    bayesianRankLabel: {
      flexShrink: 0,
      fontSize: Font.sm,
      color: Colors.textSecondary,
      ...(isPlayer ? { fontWeight: Weight.bold } : { fontWeight: Weight.semibold }),
    },
    twoRowLayout: {
      display: 'flex',
      flexDirection: 'column',
      justifyContent: 'center',
      gap: Gap.xl,
      width: '100%',
      minWidth: 0,
    } as React.CSSProperties,
    twoRowPrimary: {
      display: 'flex',
      alignItems: 'center',
      gap: Gap.xl,
      width: '100%',
      minWidth: 0,
    } as React.CSSProperties,
    twoRowMetadata: {
      display: 'flex',
      alignItems: 'center',
      justifyContent: 'flex-end',
      gap: Gap.md,
      width: '100%',
      minWidth: 0,
    } as React.CSSProperties,
    colRating: {
      flexShrink: 0,
      fontWeight: Weight.semibold,
      fontSize: Font.md,
      color: Colors.accentBlueBright,
      fontVariantNumeric: FontVariant.tabularNums,
      textAlign: TextAlign.right,
      minWidth: reserveTenDigitScoreWidth ? TEN_DIGIT_SCORE_MIN_WIDTH : '5ch',
      ...(isPlayer ? { fontWeight: Weight.bold } : undefined),
    },
  }), [isPlayer, rankWidth, reserveTenDigitScoreWidth]);
}
