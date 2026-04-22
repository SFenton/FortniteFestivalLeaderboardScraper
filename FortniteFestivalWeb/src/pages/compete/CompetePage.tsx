/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate } from 'react-router-dom';
import { useQueries } from '@tanstack/react-query';
import { IoChevronForward } from 'react-icons/io5';
import { api } from '../../api/client';
import { queryKeys } from '../../api/queryKeys';
import { rankingsCache } from '../../api/pageCache';
import { useTrackedPlayer } from '../../hooks/data/useTrackedPlayer';
import { useSettings, visibleInstruments } from '../../contexts/SettingsContext';
import { useFeatureFlags } from '../../contexts/FeatureFlagsContext';
import { useIsMobileChrome } from '../../hooks/ui/useIsMobile';
import type {
  RankingsPageResponse,
  ComboPageResponse,
  AccountRankingDto,
  ComboRankingEntry,
  RivalSummary,
} from '@festival/core/api/serverTypes';
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
import { rankingScopeLabel, resolveSupportedRankingScopes, type RankingScope } from '../../utils/rankingScopes';

type PlayerRankingResult = AccountRankingDto | ({ comboId: string; rankBy: string; totalAccounts: number } & ComboRankingEntry);

type NormalizedRankingEntry = {
  accountId: string;
  displayName?: string;
  rank: number;
  ratingLabel: string;
};

type CompeteScopeViewModel = {
  scope: RankingScope;
  label: string;
  leaderboardEntries: NormalizedRankingEntry[];
  playerEntry: NormalizedRankingEntry | null;
  playerInTop: boolean;
  leaderboardError: unknown;
  rivalsAbove: RivalSummary[];
  rivalsBelow: RivalSummary[];
  rivalsError: unknown;
};

export default function CompetePage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { player } = useTrackedPlayer();
  const { settings } = useSettings();
  const { experimentalRanks: experimentalRanksEnabled = false } = useFeatureFlags();
  const isMobile = useIsMobileChrome();

  const accountId = player?.accountId ?? '';
  const instruments = useMemo(() => visibleInstruments(settings), [settings]);
  const scopes = useMemo(() => resolveSupportedRankingScopes(instruments), [instruments]);

  const leaderboardQueries = useQueries({
    queries: scopes.map((scope) => (
      scope.kind === 'combo'
        ? {
            queryKey: queryKeys.comboRankings(scope.comboId, 'totalscore', 1, 10),
            queryFn: () => api.getComboRankings(scope.comboId, 'totalscore', 1, 10),
          }
        : {
            queryKey: queryKeys.rankings(scope.instrument, 'totalscore', 1, 10),
            queryFn: () => api.getRankings(scope.instrument, 'totalscore', 1, 10),
          }
    )),
  });

  const playerQueries = useQueries({
    queries: accountId
      ? scopes.map((scope) => (
          scope.kind === 'combo'
            ? {
                queryKey: queryKeys.playerComboRanking(accountId, scope.comboId, 'totalscore'),
                queryFn: () => api.getPlayerComboRanking(accountId, scope.comboId, 'totalscore'),
              }
            : {
                queryKey: queryKeys.playerRanking(scope.instrument, accountId, 'totalscore'),
                queryFn: () => api.getPlayerRanking(scope.instrument, accountId, 'totalscore'),
              }
        ))
      : [],
  });

  const rivalsQueries = useQueries({
    queries: accountId
      ? scopes.map((scope) => ({
          queryKey: queryKeys.rivalsList(accountId, scope.queryValue),
          queryFn: () => api.getRivalsList(accountId, scope.queryValue),
        }))
      : [],
  });

  const scopeSections = useMemo<CompeteScopeViewModel[]>(() => (
    scopes.map((scope, index) => {
      const leaderboardData = leaderboardQueries[index]?.data as RankingsPageResponse | ComboPageResponse | undefined;
      const playerRanking = playerQueries[index]?.data as PlayerRankingResult | undefined;
      const rivalsData = rivalsQueries[index]?.data as { above: RivalSummary[]; below: RivalSummary[] } | undefined;

      const leaderboardEntries = normalizeLeaderboardEntries(leaderboardData);
      const playerEntry = normalizePlayerEntry(playerRanking);

      return {
        scope,
        label: rankingScopeLabel(scope),
        leaderboardEntries,
        playerEntry,
        playerInTop: !!(accountId && leaderboardEntries.some((entry) => entry.accountId === accountId)),
        leaderboardError: leaderboardQueries[index]?.error,
        rivalsAbove: rivalsData?.above.slice(0, 3) ?? [],
        rivalsBelow: rivalsData?.below.slice(0, 3) ?? [],
        rivalsError: rivalsQueries[index]?.error,
      };
    })
  ), [accountId, leaderboardQueries, playerQueries, rivalsQueries, scopes]);

  const allLeaderboardsErrored = leaderboardQueries.length > 0 && leaderboardQueries.every((query) => !!query.error);
  const firstLeaderboardError = leaderboardQueries.find((query) => query.error)?.error;
  const leaderboardReady = leaderboardQueries.every((query) => !query.isLoading);
  const rivalsReady = !accountId || rivalsQueries.every((query) => !query.isLoading);

  const isReady = (leaderboardReady && rivalsReady) || allLeaderboardsErrored;
  const hasError = allLeaderboardsErrored;
  const { phase, shouldStagger } = usePageTransition('compete', isReady, isReady);
  const { next: stagger, clearAnim } = useStagger(shouldStagger);
  const s = useCompeteStyles();
  const firstRunGateCtx = useMemo(
    () => ({ hasPlayer: !!player, experimentalRanksEnabled }),
    [experimentalRanksEnabled, player],
  );

  const navigateToLeaderboards = (scope: RankingScope) => {
    const navTarget = scope.kind === 'combo'
      ? Routes.fullComboRankings(scope.comboId, 'totalscore')
      : Routes.fullRankings(scope.instrument, 'totalscore');
    const cacheKey = scope.kind === 'combo'
      ? `combo:${scope.comboId}:totalscore`
      : `${scope.instrument}:totalscore`;

    rankingsCache.delete(cacheKey);
    navigate(navTarget);
  };

  const navigateToAllRivals = (scope: RankingScope) => {
    navigate(Routes.allRivals(scope.queryValue), { state: { from: 'compete' } });
  };

  const navigateToRivalDetail = (scope: RankingScope, rival: RivalSummary) => {
    navigate(Routes.rivalDetail(rival.accountId, rival.displayName ?? undefined), {
      state: { combo: scope.queryValue, rivalName: rival.displayName ?? undefined },
    });
  };

  return (
    <Page
      scrollRestoreKey="compete"
      loadPhase={phase}
      before={isMobile ? undefined : <PageHeader title={t('compete.title')} />}
      firstRun={{ key: 'compete', label: t('nav.compete'), slides: competeSlides, gateContext: firstRunGateCtx }}
    >
      {phase === 'contentIn' && hasError && (() => {
        const parsed = parseApiError(String(firstLeaderboardError));
        return (
          <EmptyState
            fullPage
            title={parsed.title}
            subtitle={parsed.subtitle}
            style={buildStaggerStyle(200)}
            onAnimationEnd={clearStaggerStyle}
          />
        );
      })()}
      {phase === 'contentIn' && !hasError && (
        <div style={s.content}>
          <div style={s.section}>
            <div style={{ ...s.sectionHeader, ...stagger() }} onAnimationEnd={clearAnim}>
              <span style={s.sectionTitle}>{t('compete.leaderboards')}</span>
            </div>
            {scopeSections.map((section) => (
              <div key={`leaderboards:${section.scope.scopeKey}`} style={s.scopeGroup}>
                <div
                  className={fx.sectionHeaderClickable}
                  style={{ ...s.sectionHeaderClickable, ...stagger() }}
                  onAnimationEnd={clearAnim}
                  onClick={() => navigateToLeaderboards(section.scope)}
                  role="button"
                  tabIndex={0}
                  onKeyDown={(event) => { if (event.key === 'Enter') navigateToLeaderboards(section.scope); }}
                >
                  <div style={s.cardHeaderText}>
                    <span style={s.cardTitle}>{section.label}</span>
                  </div>
                  <span style={s.seeAll}>{t('compete.seeAll')}</span>
                  <IoChevronForward size={20} style={s.chevron} />
                </div>
                <div style={s.list}>
                  {section.leaderboardEntries.map((entry) => (
                    <Link
                      key={`${section.scope.scopeKey}:${entry.accountId}`}
                      to={`/player/${entry.accountId}`}
                      style={{ ...(entry.accountId === accountId ? s.playerRow : s.row), ...stagger() }}
                      onAnimationEnd={clearAnim}
                    >
                      <RankingEntry
                        rank={entry.rank}
                        displayName={entry.displayName ?? entry.accountId.slice(0, 8)}
                        ratingLabel={entry.ratingLabel}
                        isPlayer={entry.accountId === accountId}
                      />
                    </Link>
                  ))}
                  {section.playerEntry && !section.playerInTop && (
                    <Link to={`/player/${section.playerEntry.accountId}`} style={{ ...s.playerRow, ...stagger() }} onAnimationEnd={clearAnim}>
                      <RankingEntry
                        rank={section.playerEntry.rank}
                        displayName={section.playerEntry.displayName ?? section.playerEntry.accountId.slice(0, 8)}
                        ratingLabel={section.playerEntry.ratingLabel}
                        isPlayer
                      />
                    </Link>
                  )}
                  {!section.leaderboardError && section.leaderboardEntries.length === 0 && !section.playerEntry && (
                    <div style={{ ...s.emptyHint, ...stagger() }} onAnimationEnd={clearAnim}>{t('rankings.noRankings')}</div>
                  )}
                  {section.leaderboardError && (
                    <div style={{ ...s.emptyHint, ...stagger() }} onAnimationEnd={clearAnim}>
                      {parseApiError(String(section.leaderboardError)).title}
                    </div>
                  )}
                </div>
                <div style={{ ...s.viewAllButton, ...stagger() }} onAnimationEnd={clearAnim} onClick={() => navigateToLeaderboards(section.scope)}>
                  {t('compete.viewFullLeaderboards')}
                </div>
              </div>
            ))}
          </div>

          <div style={s.section}>
            <div style={{ ...s.sectionHeader, ...stagger() }} onAnimationEnd={clearAnim}>
              <span style={s.sectionTitle}>{t('compete.rivals')}</span>
            </div>
            {scopeSections.map((section) => (
              <div key={`rivals:${section.scope.scopeKey}`} style={s.scopeGroup}>
                <div
                  className={fx.sectionHeaderClickable}
                  style={{ ...s.sectionHeaderClickable, ...stagger() }}
                  onAnimationEnd={clearAnim}
                  onClick={() => navigateToAllRivals(section.scope)}
                  role="button"
                  tabIndex={0}
                  onKeyDown={(event) => { if (event.key === 'Enter') navigateToAllRivals(section.scope); }}
                >
                  <div style={s.cardHeaderText}>
                    <span style={s.cardTitle}>{section.label}</span>
                  </div>
                  <span style={s.seeAll}>{t('compete.seeAll')}</span>
                  <IoChevronForward size={20} style={s.chevron} />
                </div>
                {section.rivalsAbove.length > 0 || section.rivalsBelow.length > 0 ? (
                  <div style={s.rivalList}>
                    {section.rivalsAbove.map((rival) => (
                      <RivalRow
                        key={`above:${section.scope.scopeKey}:${rival.accountId}`}
                        rival={rival}
                        direction="above"
                        onClick={() => navigateToRivalDetail(section.scope, rival)}
                        style={stagger()}
                        onAnimationEnd={clearAnim}
                      />
                    ))}
                    {section.rivalsBelow.map((rival) => (
                      <RivalRow
                        key={`below:${section.scope.scopeKey}:${rival.accountId}`}
                        rival={rival}
                        direction="below"
                        onClick={() => navigateToRivalDetail(section.scope, rival)}
                        style={stagger()}
                        onAnimationEnd={clearAnim}
                      />
                    ))}
                  </div>
                ) : (
                  <div style={{ ...s.emptyHint, ...stagger() }} onAnimationEnd={clearAnim}>
                    {section.rivalsError ? parseApiError(String(section.rivalsError)).title : t('compete.noRivals')}
                  </div>
                )}
                <div style={{ ...s.viewAllButton, ...stagger() }} onAnimationEnd={clearAnim} onClick={() => navigateToAllRivals(section.scope)}>
                  {t('compete.viewAllRivals')}
                </div>
              </div>
            ))}
          </div>
        </div>
      )}
    </Page>
  );
}

function normalizeLeaderboardEntries(data: RankingsPageResponse | ComboPageResponse | undefined): NormalizedRankingEntry[] {
  if (!data) {
    return [];
  }

  if ('comboId' in data) {
    return data.entries.map((entry) => ({
      accountId: entry.accountId,
      displayName: entry.displayName,
      rank: entry.rank,
      ratingLabel: formatRating(entry.totalScore, 'totalscore'),
    }));
  }

  return data.entries.map((entry) => ({
    accountId: entry.accountId,
    displayName: entry.displayName,
    rank: entry.totalScoreRank,
    ratingLabel: formatRating(entry.totalScore, 'totalscore'),
  }));
}

function normalizePlayerEntry(playerRanking: PlayerRankingResult | undefined): NormalizedRankingEntry | null {
  if (!playerRanking) {
    return null;
  }

  if ('comboId' in playerRanking) {
    return {
      accountId: playerRanking.accountId,
      displayName: playerRanking.displayName,
      rank: playerRanking.rank,
      ratingLabel: formatRating(playerRanking.totalScore, 'totalscore'),
    };
  }

  return {
    accountId: playerRanking.accountId,
    displayName: playerRanking.displayName,
    rank: playerRanking.totalScoreRank,
    ratingLabel: formatRating(playerRanking.totalScore, 'totalscore'),
  };
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
      sectionHeader: {
        paddingBottom: Gap.md,
      } as CSSProperties,
      sectionTitle: {
        display: Display.block,
        fontSize: Font.lg,
        fontWeight: Weight.bold,
        color: Colors.textPrimary,
      } as CSSProperties,
      scopeGroup: {
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
