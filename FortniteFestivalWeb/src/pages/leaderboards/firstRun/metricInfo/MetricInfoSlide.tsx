/* eslint-disable react/forbid-dom-props -- useStyles pattern */
/**
 * Shared metric-info FRE slide component.
 * Renders body paragraphs, optional callout, rank-card comparisons,
 * song example rows, and LaTeX formulas.
 */
import { useMemo, type CSSProperties, type ReactNode } from 'react';
import FadeIn from '../../../../components/page/FadeIn';
import MathTex from '../../../../components/common/Math';
import { RankingEntry } from '../../components/RankingEntry';
import SongInfo from '../../../../components/songs/metadata/SongInfo';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import { useIsMobile } from '../../../../hooks/ui/useIsMobile';
import {
  Colors, Font, Weight, Gap, Radius, LineHeight, Layout, Size, Border,
  Display, Align, TextAlign, CssValue, PointerEvents,
  flexColumn, flexRow, frostedCard, padding, border,
} from '@festival/theme';

/* ── Types ── */

export interface RankCardData {
  label: string;
  entries: { rank: number; displayName: string; ratingLabel?: string; isPlayer?: boolean }[];
  highlight?: string;
}

export interface SongExampleRow {
  albumArt?: string;
  title: string;
  artist: string;
  valueLabel: string;
  valueLines?: string[];
}

export interface StatBlockData {
  value: string;
  label: string;
}

export interface MetricInfoSlideProps {
  paragraphs: (string | ReactNode)[];
  formulas?: string[];
  /** Formula-focused composition for compact one-formula explanation slides. */
  layout?: 'standard' | 'formula';
  /** 1-2 labeled mini-leaderboards shown side-by-side. */
  cards?: RankCardData[];
  /** Blue left-border tip box for key takeaways. */
  callout?: string;
  /** Song rows with album art + right-aligned value label. */
  songRows?: SongExampleRow[];
  /** Summary line shown below song rows (e.g. "Your average: 4.5%"). */
  songSummary?: string;
  /** Centered big-number stat display. */
  stat?: StatBlockData;
}

export default function MetricInfoSlide({ paragraphs, formulas, layout = 'standard', cards, callout, songRows, songSummary, stat }: MetricInfoSlideProps) {
  const isMobile = useIsMobile();
  const s = useStyles(isMobile);
  const slideHeight = useSlideHeight();
  const isFormulaLayout = layout === 'formula';
  const useStackedCards = isMobile && (cards?.length ?? 0) > 1;
  const wrapperStyle = isFormulaLayout
    ? { ...s.wrapper, ...s.formulaLayout, minHeight: slideHeight || undefined }
    : s.wrapper;
  const paragraphStyle = isFormulaLayout ? s.formulaPara : s.para;
  const formulaStyle = isFormulaLayout ? { ...s.formula, ...s.formulaCompact } : s.formula;

  /**
   * Progressive height budget for cards.
   * Priority order for hiding (first dropped → last):
   *   1. Non-player card rows (trim to min 2, always keep player row)
   *   2. Callout box
   *   Then continue trimming rows down to minimum 1 (just the player row).
   *   Italic highlight captions are never hidden.
   */
  const { maxCardRows, showCallout } = useMemo(() => {
    if (!cards) return { maxCardRows: 0, showCallout: true };
    const budget = slideHeight || 400;
    const cardCount = cards.length;
    const paraHeight = paragraphs.length * 48;
    const formulaHeight = (formulas?.length ?? 0) * 60;
    const songHeight = songRows ? songRows.length * 60 + (songSummary ? 24 : 0) : 0;
    const gapCount = paragraphs.length + 1 /* cards */ + (formulas?.length ?? 0);
    const gapHeight = gapCount * Gap.lg;
    const fixedHeight = paraHeight + formulaHeight + songHeight + gapHeight;

    const calloutCost = callout ? 56 + Gap.lg : 0; // content + its gap
    const highlightCost = useStackedCards ? 34 : 18; // mobile captions often wrap
    const cardLabelCost = 22;      // Font.sm + marginBottom per card
    const rowUnit = Layout.entryRowHeight + Gap.xs;
    const chrome = useStackedCards
      ? cardCount * (cardLabelCost + highlightCost) + (cardCount - 1) * Gap.md
      : cardLabelCost + highlightCost;
    const rowCost = useStackedCards ? rowUnit * cardCount : rowUnit;

    const tryFit = (withCallout: boolean) => {
      const available = budget - fixedHeight - (withCallout ? calloutCost : 0) - chrome;
      return Math.floor(available / rowCost);
    };

    // Phase 1: everything — trim rows to fit (min 2)
    let rows = tryFit(true);
    if (rows >= 2) return { maxCardRows: rows, showCallout: true };

    // Phase 2: drop callout (min 2 rows)
    rows = tryFit(false);
    if (rows >= 2) return { maxCardRows: rows, showCallout: false };

    // Phase 3: drop callout, allow rows down to 1
    return { maxCardRows: Math.max(1, rows), showCallout: false };
  }, [slideHeight, paragraphs.length, callout, formulas?.length, cards, songRows, songSummary, useStackedCards]);

  let delay = 0;
  return (
    <div style={wrapperStyle}>
      {paragraphs.map((p, i) => (
        <FadeIn key={i} delay={(delay++) * 80} style={paragraphStyle}>
          {p}
        </FadeIn>
      ))}
      {callout && showCallout && (
        <FadeIn delay={(delay++) * 80} style={s.callout}>
          {callout}
        </FadeIn>
      )}
      {stat && (
        <FadeIn delay={(delay++) * 80} style={s.statBlock}>
          <div style={s.statValue}>{stat.value}</div>
          <div style={s.statLabel}>{stat.label}</div>
        </FadeIn>
      )}
      {songRows && (
        <FadeIn delay={(delay++) * 80} style={s.songSection}>
          {songRows.map((row, i) => (
            <div key={i} style={s.songRow}>
              <SongInfo albumArt={row.albumArt} title={row.title} artist={row.artist} minWidth={0} />
              <span style={s.songValue} aria-label={row.valueLabel}>
                {getSongValueLines(row).map((line, lineIndex) => (
                  <span key={lineIndex} style={s.songValueLine} data-testid="metric-song-value-line">{line}</span>
                ))}
              </span>
            </div>
          ))}
          {songSummary && <div style={s.songSummary}>{songSummary}</div>}
        </FadeIn>
      )}
      {cards && (
        <FadeIn delay={(delay++) * 80} style={cards.length > 1 ? (useStackedCards ? s.cardStack : s.cardPair) : s.cardSingle} data-testid="metric-rank-card-pair">
          {cards.map((card, ci) => {
            const trimmed = trimEntries(card.entries, maxCardRows);
            return (
              <div key={ci} style={s.card}>
                <div style={s.cardLabel}>{card.label}</div>
                {trimmed.map(e => (
                  <div key={e.rank} style={e.isPlayer ? s.rowPlayer : s.row}>
                    <RankingEntry rank={e.rank} displayName={e.displayName} ratingLabel={e.ratingLabel ?? ''} isPlayer={e.isPlayer} />
                  </div>
                ))}
                {card.highlight && <div style={s.cardHighlight}>{card.highlight}</div>}
              </div>
            );
          })}
        </FadeIn>
      )}
      {formulas && formulas.map((f, i) => (
        <FadeIn key={`f${i}`} delay={(delay++) * 80} style={formulaStyle}>
          <MathTex tex={f} block />
        </FadeIn>
      ))}
    </div>
  );
}

function getSongValueLines(row: SongExampleRow) {
  if (row.valueLines?.length) return row.valueLines;
  return row.valueLabel.split(/\s+[·→]\s+/u).filter(Boolean);
}

/** Trim entries to fit maxRows, always keeping the player row. */
function trimEntries(
  entries: RankCardData['entries'],
  maxRows: number,
): RankCardData['entries'] {
  if (entries.length <= maxRows) return entries;
  const playerIdx = entries.findIndex(e => e.isPlayer);
  if (playerIdx === -1) return entries.slice(0, maxRows);
  // Keep the player and fill remaining slots from surrounding entries
  const result: typeof entries = [];
  const before = entries.slice(0, playerIdx);
  const after = entries.slice(playerIdx + 1);
  const slots = maxRows - 1; // reserve one for player
  const beforeSlots = Math.min(before.length, Math.ceil(slots / 2));
  const afterSlots = Math.min(after.length, slots - beforeSlots);
  result.push(...before.slice(before.length - beforeSlots));
  result.push(entries[playerIdx]!);
  result.push(...after.slice(0, afterSlots));
  return result;
}

function useStyles(isMobile: boolean) {
  return useMemo(() => ({
    wrapper: {
      ...flexColumn,
      gap: Gap.lg,
      width: CssValue.full,
      maxWidth: CssValue.full,
      minWidth: 0,
      overflowX: 'hidden' as const,
      pointerEvents: PointerEvents.none,
    } as CSSProperties,
    formulaLayout: {
      justifyContent: 'center' as const,
      gap: Gap.xl,
      textAlign: TextAlign.center,
    } as CSSProperties,
    para: {
      fontSize: Font.md,
      color: Colors.textSecondary,
      lineHeight: LineHeight.relaxed,
    } as CSSProperties,
    formulaPara: {
      fontSize: Font.md,
      color: Colors.textSecondary,
      lineHeight: LineHeight.relaxed,
      textAlign: TextAlign.center,
    } as CSSProperties,
    callout: {
      fontSize: Font.sm,
      color: Colors.accentBlueBright,
      lineHeight: LineHeight.relaxed,
      whiteSpace: 'pre-line' as const,
      padding: padding(Gap.md, Gap.lg),
      borderLeft: border(Border.thick, Colors.accentBlue),
      backgroundColor: 'rgba(59,130,246,0.06)',
      borderRadius: `0 ${Radius.sm}px ${Radius.sm}px 0`,
    } as CSSProperties,

    /* ── Stat block ── */
    statBlock: {
      ...frostedCard,
      ...flexColumn,
      alignItems: Align.center,
      padding: padding(Gap.xl, Gap.lg),
      borderRadius: Radius.md,
      gap: Gap.xs,
    } as CSSProperties,
    statValue: {
      fontSize: Font['2xl'],
      fontWeight: Weight.bold,
      color: Colors.accentBlueBright,
    } as CSSProperties,
    statLabel: {
      fontSize: Font.sm,
      color: Colors.textSecondary,
    } as CSSProperties,
    formula: {
      display: Display.flex,
      justifyContent: 'center' as const,
      padding: `${Gap.md}px 0`,
      color: Colors.textPrimary,
      fontSize: Font.lg,
      fontWeight: Weight.normal,
      width: CssValue.full,
      maxWidth: CssValue.full,
      minWidth: 0,
      overflowX: 'hidden' as const,
      overflowY: 'visible' as const,
      pointerEvents: 'auto' as const,
    } as CSSProperties,
    formulaCompact: {
      fontSize: Font.md,
      padding: `${Gap.sm}px 0`,
    } as CSSProperties,

    /* ── Song example rows ── */
    songSection: {
      ...flexColumn,
      gap: Gap.sm,
      width: CssValue.full,
    } as CSSProperties,
    songRow: {
      ...frostedCard,
      ...flexRow,
      gap: Gap.lg,
      padding: padding(Gap.sm, Gap.lg),
      borderRadius: Radius.sm,
      fontSize: Font.sm,
      minHeight: Size.thumb + Gap.sm * 2,
      minWidth: 0,
      overflow: 'hidden' as const,
    } as CSSProperties,
    songValue: {
      ...flexColumn,
      flexShrink: 0,
      minWidth: isMobile ? 94 : 118,
      maxWidth: isMobile ? 118 : 160,
      gap: 2,
      fontWeight: Weight.semibold,
      fontSize: Font.sm,
      color: Colors.accentBlueBright,
      textAlign: TextAlign.right,
      marginLeft: 'auto',
      alignItems: 'flex-end' as const,
    } as CSSProperties,
    songValueLine: {
      display: 'block',
      whiteSpace: 'nowrap' as const,
      lineHeight: 1.2,
    } as CSSProperties,
    songSummary: {
      fontSize: Font.sm,
      fontWeight: Weight.bold,
      color: Colors.textPrimary,
      textAlign: TextAlign.center,
      paddingTop: Gap.xs,
    } as CSSProperties,

    /* ── Rank cards ── */
    cardPair: {
      ...flexRow,
      gap: Gap.md,
      width: CssValue.full,
    } as CSSProperties,
    cardStack: {
      ...flexColumn,
      gap: Gap.md,
      width: CssValue.full,
    } as CSSProperties,
    cardSingle: {
      width: CssValue.full,
    } as CSSProperties,
    card: {
      ...flexColumn,
      flex: 1,
      minWidth: 0,
      gap: Gap.xs,
    } as CSSProperties,
    cardLabel: {
      fontSize: Font.sm,
      fontWeight: Weight.bold,
      color: Colors.textPrimary,
      marginBottom: Gap.xs,
    } as CSSProperties,
    row: {
      ...frostedCard,
      display: Display.flex,
      alignItems: Align.center,
      gap: Gap.lg,
      padding: padding(0, Gap.lg),
      height: Layout.entryRowHeight,
      borderRadius: Radius.sm,
      fontSize: Font.sm,
    } as CSSProperties,
    rowPlayer: {
      ...frostedCard,
      display: Display.flex,
      alignItems: Align.center,
      gap: Gap.lg,
      padding: padding(0, Gap.lg),
      height: Layout.entryRowHeight,
      borderRadius: Radius.sm,
      fontSize: Font.sm,
      border: border(Border.thin, Colors.accentBlue),
    } as CSSProperties,
    cardHighlight: {
      fontSize: Font.xs,
      color: Colors.textTertiary,
      fontStyle: 'italic' as const,
      marginTop: Gap.xs,
    } as CSSProperties,
  }), []);
}
