/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { IoChevronForward } from 'react-icons/io5';
import { api } from '../../api/client';
import { queryKeys } from '../../api/queryKeys';
import { rankingsCache } from '../../api/pageCache';
import { useTrackedPlayer } from '../../hooks/data/useTrackedPlayer';
import { useScoreFilter } from '../../hooks/data/useScoreFilter';
import { useSettings, visibleInstruments } from '../../contexts/SettingsContext';
import { useIsMobileChrome } from '../../hooks/ui/useIsMobile';
import { comboIdFromInstruments } from '@festival/core/combos';
import type { RankingsPageResponse, ComboPageResponse, AccountRankingDto, ComboRankingEntry } from '@festival/core/api/serverTypes';
import Page from '../Page';
import PageHeader from '../../components/common/PageHeader';
import EmptyState from '../../components/common/EmptyState';
import { parseApiError } from '../../utils/apiError';
import { buildStaggerStyle, clearStaggerStyle } from '../../hooks/ui/useStaggerStyle';
import { RankingEntry } from '../leaderboards/components/RankingEntry';
import { formatRating } from '../leaderboards/helpers/rankingHelpers';
import { Routes } from '../../routes';
import RivalRow from '../rivals/components/RivalRow';
import { usePageTransition } from '../../hooks/ui/usePageTransition';
import { useStagger } from '../../hooks/ui/useStagger';
import {
  Colors, Font, Weight, Gap, Radius, Layout,
  Display, Align, Justify, Cursor, CssValue, CssProp, WhiteSpace,
  frostedCard, flexColumn, flexRow, transition, padding, border, Border,
  FAST_FADE_MS, NAV_TRANSITION_MS,
} from '@festival/theme';
import fx from '../../styles/effects.module.css';
import { competeSlides } from './firstRun';

export default function CompetePage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { player } = useTrackedPlayer();
  const { settings } = useSettings();
  const { leewayParam } = useScoreFilter();
  const isMobile = useIsMobileChrome();

  const accountId = player?.accountId ?? '';
  const instruments = useMemo(() => visibleInstruments(settings), [settings]);
  const previewInstrument = instruments[0] ?? null;
  const isMulti = instruments.length >= 2;
  const comboId = useMemo(
    () => isMulti ? comboIdFromInstruments(instruments) : null,
    [instruments, isMulti],
  );

  type PlayerRankingResult = AccountRankingDto | ({ comboId: string; rankBy: string; totalAccounts: number } & ComboRankingEntry);

  // Leaderboard top 10 — combo rankings (2+ instruments) or per-instrument (1)
  const { data: leaderboardData, isLoading: leaderboardLoading, error: leaderboardError } = useQuery<RankingsPageResponse | ComboPageResponse>({
    queryKey: isMulti
      ? queryKeys.comboRankings(comboId!, 'totalscore', 1, 10)
      : queryKeys.rankings(previewInstrument ?? 'Solo_Guitar', 'totalscore', 1, 10, leewayParam),
    queryFn: () => isMulti
      ? api.getComboRankings(comboId!, 'totalscore', 1, 10)
      : api.getRankings(previewInstrument!, 'totalscore', 1, 10, leewayParam),
    enabled: !!previewInstrument,
  });

  // Player's own ranking for the same metric
  const { data: playerRanking } = useQuery<PlayerRankingResult>({
    queryKey: isMulti
      ? queryKeys.playerComboRanking(accountId, comboId!, 'totalscore')
      : queryKeys.playerRanking(previewInstrument ?? 'Solo_Guitar', accountId, leewayParam),
    queryFn: () => isMulti
      ? api.getPlayerComboRanking(accountId, comboId!, 'totalscore')
      : api.getPlayerRanking(previewInstrument!, accountId, leewayParam),
    enabled: !!accountId && !!previewInstrument,
  });

  // Normalize to common shape for rendering
  const leaderboardEntries = useMemo(() => {
    if (!leaderboardData) return [];
    if ('entries' in leaderboardData && 'comboId' in leaderboardData) {
      // ComboPageResponse
      return leaderboardData.entries.map(e => ({
        accountId: e.accountId,
        displayName: e.displayName,
        rank: e.rank,
        ratingLabel: formatRating(e.totalScore, 'totalscore'),
      }));
    }
    // RankingsPageResponse
    if ('entries' in leaderboardData) {
      return leaderboardData.entries.map(e => ({
        accountId: e.accountId,
        displayName: e.displayName,
        rank: e.totalScoreRank,
        ratingLabel: formatRating(e.totalScore, 'totalscore'),
      }));
    }
    return [];
  }, [leaderboardData]);

  const playerEntry = useMemo(() => {
    if (!playerRanking) return null;
    if ('comboId' in playerRanking) {
      // Combo player ranking
      return {
        accountId: playerRanking.accountId,
        displayName: playerRanking.displayName,
        rank: playerRanking.rank,
        ratingLabel: formatRating(playerRanking.totalScore, 'totalscore'),
      };
    }
    // Per-instrument player ranking
    return {
      accountId: playerRanking.accountId,
      displayName: playerRanking.displayName,
      rank: playerRanking.totalScoreRank,
      ratingLabel: formatRating(playerRanking.totalScore, 'totalscore'),
    };
  }, [playerRanking]);

  const playerInTop = !!(accountId && leaderboardEntries.some(e => e.accountId === accountId));

  const leaderboardNavTarget = isMulti ? Routes.leaderboards : Routes.fullRankings(previewInstrument!, 'totalscore');
  const navigateToLeaderboards = () => {
    if (!isMulti && previewInstrument) rankingsCache.delete(`${previewInstrument}:totalscore`);
    navigate(leaderboardNavTarget);
  };

  // Rivals — closest 3 above + 3 below for first visible instrument
  const { data: rivalsData, error: rivalsError } = useQuery({
    queryKey: queryKeys.rivalsList(accountId, previewInstrument ?? ''),
    queryFn: () => api.getRivalsList(accountId, previewInstrument!),
    enabled: !!accountId && !!previewInstrument,
  });

  const above = rivalsData?.above.slice(0, 3) ?? [];
  const below = rivalsData?.below.slice(0, 3) ?? [];

  // Wait for leaderboards; also wait for rivals if a player is tracked
  const rivalsReady = !accountId || !previewInstrument || !!rivalsData || !!rivalsError;
  const isReady = (!leaderboardLoading || !!leaderboardError) && rivalsReady;
  const hasError = !!leaderboardError;
  const { phase, shouldStagger } = usePageTransition('compete', isReady, isReady);
  const { next: stagger, clearAnim } = useStagger(shouldStagger);
  const s = useCompeteStyles();
  const firstRunGateCtx = useMemo(() => ({ hasPlayer: !!player, experimentalRanksEnabled: settings.enableExperimentalRanks }), [player, settings.enableExperimentalRanks]);

  return (
    <Page scrollRestoreKey="compete" loadPhase={phase} before={isMobile ? undefined : <PageHeader title={t('compete.title')} />}
      firstRun={{ key: 'compete', label: t('nav.compete'), slides: competeSlides, gateContext: firstRunGateCtx }}
    >
      {phase === 'contentIn' && hasError && (() => {
        const parsed = parseApiError(String(leaderboardError));
        return <EmptyState fullPage title={parsed.title} subtitle={parsed.subtitle} style={buildStaggerStyle(200)} onAnimationEnd={clearStaggerStyle} />;
      })()}
      {phase === 'contentIn' && !hasError && (
      <div style={s.content}>
      {/* Leaderboards section */}
      <div style={s.section}>
        <div
          className={fx.sectionHeaderClickable}
          style={{ ...s.sectionHeaderClickable, ...stagger() }}
          onAnimationEnd={clearAnim}
          onClick={navigateToLeaderboards}
          role="button"
          tabIndex={0}
          onKeyDown={e => { if (e.key === 'Enter') navigateToLeaderboards(); }}
        >
          <div style={s.cardHeaderText}>
            <span style={s.cardTitle}>{t('compete.leaderboards')}</span>
          </div>
          <span style={s.seeAll}>{t('compete.seeAll')}</span>
          <IoChevronForward size={20} style={s.chevron} />
        </div>
        <div style={s.list}>
          {leaderboardEntries.map((e) => (
            <Link key={e.accountId} to={`/player/${e.accountId}`} style={{ ...(e.accountId === accountId ? s.playerRow : s.row), ...stagger() }} onAnimationEnd={clearAnim}>
              <RankingEntry
                rank={e.rank}
                displayName={e.displayName ?? e.accountId.slice(0, 8)}
                ratingLabel={e.ratingLabel}
                isPlayer={e.accountId === accountId}
              />
            </Link>
          ))}
          {playerEntry && !playerInTop && (
            <Link to={`/player/${playerEntry.accountId}`} style={{ ...s.playerRow, ...stagger() }} onAnimationEnd={clearAnim}>
              <RankingEntry
                rank={playerEntry.rank}
                displayName={playerEntry.displayName ?? playerEntry.accountId.slice(0, 8)}
                ratingLabel={playerEntry.ratingLabel}
                isPlayer
              />
            </Link>
          )}
        </div>
        <div style={{ ...s.viewAllButton, ...stagger() }} onAnimationEnd={clearAnim} onClick={navigateToLeaderboards}>
          {t('compete.viewFullLeaderboards')}
        </div>
      </div>

      {/* Rivals section */}
      <div style={s.section}>
        <div
          className={fx.sectionHeaderClickable}
          style={{ ...s.sectionHeaderClickable, ...stagger() }}
          onAnimationEnd={clearAnim}
          onClick={() => navigate(Routes.rivals)}
          role="button"
          tabIndex={0}
          onKeyDown={e => { if (e.key === 'Enter') navigate(Routes.rivals); }}
        >
          <div style={s.cardHeaderText}>
            <span style={s.cardTitle}>{t('compete.rivals')}</span>
          </div>
          <span style={s.seeAll}>{t('compete.seeAll')}</span>
          <IoChevronForward size={20} style={s.chevron} />
        </div>
        {above.length > 0 || below.length > 0 ? (
          <div style={s.rivalList}>
            {above.map((r) => (
              <RivalRow
                key={r.accountId}
                rival={r}
                direction="above"
                onClick={() => navigate(Routes.rivalDetail(r.accountId, r.displayName ?? undefined))}
                style={stagger()}
                onAnimationEnd={clearAnim}
              />
            ))}
            {below.map((r) => (
              <RivalRow
                key={r.accountId}
                rival={r}
                direction="below"
                onClick={() => navigate(Routes.rivalDetail(r.accountId, r.displayName ?? undefined))}
                style={stagger()}
                onAnimationEnd={clearAnim}
              />
            ))}
          </div>
        ) : (
          <div style={{ ...s.emptyHint, ...stagger() }} onAnimationEnd={clearAnim}>{t('compete.noRivals')}</div>
        )}
        <div style={{ ...s.viewAllButton, ...stagger() }} onAnimationEnd={clearAnim} onClick={() => navigate(Routes.rivals)}>
          {t('compete.viewAllRivals')}
        </div>
      </div>
      </div>
      )}
    </Page>
  );
}

function useCompeteStyles() {
  return useMemo(() => {
    const rowBase: CSSProperties = {
      ...frostedCard,
      display: Display.flex,
      alignItems: Align.center,
      gap: Gap.xl,
      padding: padding(0, Gap.xl),
      height: Layout.entryRowHeight,
      borderRadius: Radius.md,
      textDecoration: CssValue.none,
      color: CssValue.inherit,
      transition: transition(CssProp.backgroundColor, FAST_FADE_MS),
      fontSize: Font.md,
    };
    return {
      content: {
        ...flexColumn,
        gap: Gap.section,
      } as CSSProperties,
      section: {
        ...flexColumn,
        gap: Gap.sm,
      } as CSSProperties,
      sectionHeaderClickable: {
        ...flexRow,
        gap: Gap.md,
        paddingBottom: Gap.md,
        cursor: Cursor.pointer,
        borderRadius: Radius.sm,
        transition: transition(CssProp.opacity, NAV_TRANSITION_MS),
      } as CSSProperties,
      cardHeaderText: {
        flex: 1,
        minWidth: 0,
      } as CSSProperties,
      cardTitle: {
        display: Display.block,
        fontSize: Font.lg,
        fontWeight: Weight.bold,
        color: Colors.textPrimary,
      } as CSSProperties,
      seeAll: {
        fontSize: Font.lg,
        fontWeight: Weight.bold,
        color: Colors.textPrimary,
        flexShrink: 0,
        whiteSpace: WhiteSpace.nowrap,
      } as CSSProperties,
      chevron: {
        color: Colors.textPrimary,
        flexShrink: 0,
      } as CSSProperties,
      list: {
        ...flexColumn,
        gap: Gap.sm,
      } as CSSProperties,
      rivalList: {
        ...flexColumn,
        gap: 2,
        containerType: 'inline-size',
      } as CSSProperties,
      row: { ...rowBase } as CSSProperties,
      playerRow: {
        ...rowBase,
        backgroundColor: Colors.purpleHighlight,
        border: border(Border.thin, Colors.purpleHighlightBorder),
      } as CSSProperties,
      emptyHint: {
        fontSize: Font.sm,
        color: Colors.textSecondary,
        padding: padding(Gap.md, 0),
      } as CSSProperties,
      viewAllButton: {
        ...frostedCard,
        display: Display.flex,
        alignItems: Align.center,
        justifyContent: Justify.center,
        height: Layout.entryRowHeight,
        borderRadius: Radius.md,
        color: Colors.textPrimary,
        fontSize: Font.md,
        fontWeight: Weight.semibold,
        cursor: Cursor.pointer,
        transition: transition(CssProp.backgroundColor, FAST_FADE_MS),
      } as CSSProperties,
    };
  }, []);
}
