/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { memo, useMemo, type CSSProperties } from 'react';
import { useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import type { BandConfiguration, BandRankingEntry, BandRankingMetric, BandType, ServerInstrumentKey } from '@festival/core/api/serverTypes';
import { staggerDelay } from '@festival/ui-utils';
import { Border, Colors, Font, Weight, Gap, Radius, Layout, Display, Align, Overflow, Cursor, CssProp, FAST_FADE_MS, STAGGER_INTERVAL, FADE_DURATION, border, frostedCard, flexColumn, flexRow, transition } from '@festival/theme';
import { Routes } from '../../../routes';
import { bandTypeLabel } from '../../../utils/bandTypes';
import { parseApiError } from '../../../utils/apiError';
import BandRankingPlayerCard from './BandRankingPlayerCard';
import { computeRankWidth } from '../helpers/rankingHelpers';
import { getBandRankForMetric } from '../helpers/bandRankingHelpers';

type BandRankingCardProps = {
  bandType: BandType;
  metric: BandRankingMetric;
  entries: BandRankingEntry[];
  selectedPlayerEntry?: BandRankingEntry | null;
  selectedBandEntry?: BandRankingEntry | null;
  selectedAccountId?: string;
  activeFilterComboId?: string;
  activeFilterTeamKey?: string;
  activeFilterInstruments?: readonly ServerInstrumentKey[];
  activeFilterConfigurations?: readonly BandConfiguration[];
  totalTeams: number;
  error?: string | null;
  shouldStagger?: boolean;
  staggerOffset?: number;
};

export default memo(function BandRankingCard({
  bandType,
  metric,
  entries,
  selectedPlayerEntry,
  selectedBandEntry,
  selectedAccountId,
  activeFilterComboId,
  activeFilterTeamKey,
  activeFilterInstruments,
  activeFilterConfigurations,
  totalTeams,
  error,
  shouldStagger,
  staggerOffset = 0,
}: BandRankingCardProps) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const styles = useStyles();
  const selectedEntry = selectedBandEntry ?? selectedPlayerEntry ?? null;
  const selectedInTop = !!selectedEntry && entries.some(entry => isSameBandRankingEntry(entry, selectedEntry));
  const showSelectedRow = !!selectedEntry && !selectedInTop;
  const rankWidth = useMemo(() => computeRankWidth([
    ...entries.map(entry => getBandRankForMetric(entry, metric)),
    ...(showSelectedRow && selectedEntry ? [getBandRankForMetric(selectedEntry, metric)] : []),
  ]), [entries, metric, selectedEntry, showSelectedRow]);
  const hasRows = entries.length > 0 || showSelectedRow;
  const totalStaggerItems = entries.length + (showSelectedRow ? 3 : 2) + staggerOffset;
  const headerDelay = shouldStagger ? staggerDelay(staggerOffset, STAGGER_INTERVAL, totalStaggerItems) : undefined;
  const headerStaggerStyle: CSSProperties | undefined = headerDelay != null
    ? { opacity: 0, animation: `fadeInUp ${FADE_DURATION}ms ease-out ${headerDelay}ms forwards` }
    : undefined;
  const selectedDelay = shouldStagger && showSelectedRow ? staggerDelay(entries.length + 1 + staggerOffset, STAGGER_INTERVAL, totalStaggerItems) : undefined;
  const selectedStaggerStyle: CSSProperties | undefined = selectedDelay != null
    ? { opacity: 0, animation: `fadeInUp ${FADE_DURATION}ms ease-out ${selectedDelay}ms forwards` }
    : undefined;
  const buttonDelay = shouldStagger ? staggerDelay(entries.length + (showSelectedRow ? 2 : 1) + staggerOffset, STAGGER_INTERVAL, totalStaggerItems) : undefined;
  const buttonStaggerStyle: CSSProperties | undefined = buttonDelay != null
    ? { opacity: 0, animation: `fadeInUp ${FADE_DURATION}ms ease-out ${buttonDelay}ms forwards` }
    : undefined;
  const viewAllLabel = totalTeams > 0
    ? t('rankings.viewAllBandRankingsWithCount', { count: totalTeams, formattedCount: totalTeams.toLocaleString() })
    : t('rankings.viewAllBandRankings');
  const bandLabel = bandTypeLabel(bandType, t);

  return (
    <div style={styles.cardWrapper} data-testid={`band-ranking-card-${bandType}`}>
      <div
        style={{ ...styles.cardLabel, ...headerStaggerStyle }}
        onAnimationEnd={(event) => {
          const element = event.currentTarget;
          element.style.opacity = '';
          element.style.animation = '';
        }}
      >
        <span style={styles.cardTitle}>{bandLabel}</span>
      </div>
      <div style={styles.cardBody}>
        {error && <span style={styles.cardError}>{parseApiError(error).title}</span>}
        {!error && !hasRows && <span style={styles.cardMuted}>{t('rankings.noBandRankings')}</span>}
        {!error && entries.map((entry, index) => {
          const rowDelay = shouldStagger ? staggerDelay(index + 1 + staggerOffset, STAGGER_INTERVAL, totalStaggerItems) : undefined;
          const rowStaggerStyle: CSSProperties | undefined = rowDelay != null
            ? { opacity: 0, animation: `fadeInUp ${FADE_DURATION}ms ease-out ${rowDelay}ms forwards` }
            : undefined;
          return (
            <BandRankingPlayerCard
              key={entry.bandId || entry.teamKey}
              entry={entry}
              bandType={bandType}
              metric={metric}
              totalTeams={totalTeams}
              sourceAccountId={entryHasAccount(entry, selectedAccountId) ? selectedAccountId : undefined}
              activeFilterComboId={activeFilterComboId}
              activeFilterTeamKey={activeFilterTeamKey}
              activeFilterInstruments={activeFilterInstruments}
              activeFilterConfigurations={activeFilterConfigurations}
              rankWidth={rankWidth}
              testId={`band-ranking-entry-${bandType}-${index}`}
              style={rowStaggerStyle}
              onAnimationEnd={(event) => {
                const element = event.currentTarget;
                element.style.opacity = '';
                element.style.animation = '';
              }}
            />
          );
        })}
        {!error && showSelectedRow && selectedEntry && (
          <BandRankingPlayerCard
            key={selectedEntry.bandId || selectedEntry.teamKey}
            entry={selectedEntry}
            bandType={bandType}
            metric={metric}
            totalTeams={totalTeams}
            sourceAccountId={selectedBandEntry ? undefined : selectedAccountId}
            activeFilterComboId={activeFilterComboId}
            activeFilterTeamKey={activeFilterTeamKey}
            activeFilterInstruments={activeFilterInstruments}
            activeFilterConfigurations={activeFilterConfigurations}
            rankWidth={rankWidth}
            testId={`band-ranking-selected-entry-${bandType}`}
            style={{ ...styles.selectedCard, ...selectedStaggerStyle }}
            onAnimationEnd={(event) => {
              const element = event.currentTarget;
              element.style.opacity = '';
              element.style.animation = '';
            }}
          />
        )}
        {!error && hasRows && (
          <div
            style={{ ...styles.viewAllButton, ...buttonStaggerStyle }}
            onClick={() => navigate(Routes.bandRankings(bandType, metric))}
            onAnimationEnd={(event) => {
              const element = event.currentTarget;
              element.style.opacity = '';
              element.style.animation = '';
            }}
          >
            {viewAllLabel}
          </div>
        )}
      </div>
    </div>
  );
});

function isSameBandRankingEntry(a: BandRankingEntry, b: BandRankingEntry): boolean {
  return (!!a.bandId && a.bandId === b.bandId) || a.teamKey === b.teamKey;
}

function entryHasAccount(entry: BandRankingEntry, accountId: string | undefined): boolean {
  return !!accountId && entry.teamMembers.some(member => member.accountId === accountId);
}

function useStyles() {
  return useMemo(() => ({
    cardWrapper: { ...flexColumn } as CSSProperties,
    cardLabel: {
      ...flexRow,
      gap: Gap.md,
      paddingBottom: Gap.xs,
    } as CSSProperties,
    cardTitle: {
      color: Colors.textPrimary,
      fontSize: Font.xl,
      fontWeight: Weight.bold,
    } as CSSProperties,
    cardBody: {
      ...flexColumn,
      gap: Gap.sm,
      flex: 1,
      overflow: Overflow.hidden,
    } as CSSProperties,
    cardMuted: {
      fontSize: Font.sm,
      color: Colors.textMuted,
    } as CSSProperties,
    cardError: {
      fontSize: Font.sm,
      color: Colors.statusRed,
    } as CSSProperties,
    viewAllButton: {
      ...frostedCard,
      height: Layout.entryRowHeight,
      borderRadius: Radius.md,
      display: Display.flex,
      alignItems: Align.center,
      justifyContent: 'center',
      fontSize: Font.md,
      fontWeight: Weight.semibold,
      color: Colors.textPrimary,
      cursor: Cursor.pointer,
      transition: transition(CssProp.backgroundColor, FAST_FADE_MS),
    } as CSSProperties,
    selectedCard: {
      backgroundColor: Colors.purpleHighlight,
      border: border(Border.thin, Colors.purpleHighlightBorder),
    } as CSSProperties,
  }), []);
}