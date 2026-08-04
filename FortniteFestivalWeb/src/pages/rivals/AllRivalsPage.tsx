import { useMemo, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { useQueries, useQuery } from '@tanstack/react-query';
import { api } from '../../api/client';
import { queryKeys } from '../../api/queryKeys';
import { remoteDataQueryPolicy } from '../../api/queryPolicy';
import { useSettings, visibleInstruments } from '../../contexts/SettingsContext';
import { usePageTransition } from '../../hooks/ui/usePageTransition';
import { useSetPageReady } from '../../contexts/PageReadyContext';
import { useStagger } from '../../hooks/ui/useStagger';
import { useIsMobile } from '../../hooks/ui/useIsMobile';
import { useTrackedPlayer } from '../../hooks/data/useTrackedPlayer';
import InstrumentHeader from '../../components/display/InstrumentHeader';
import { InstrumentHeaderSize } from '@festival/core/runtime';
import { LoadPhase } from '@festival/core/runtime';
import { serverInstrumentLabel, type RivalsListResponse, type RivalSummary, type ServerInstrumentKey, type LeaderboardRivalsListResponse, type LeaderboardRivalSummary } from '@festival/core/api';
import RivalRow from './components/RivalRow';
import { Routes } from '../../routes';
import { deriveRivalScopeFromSettings, isProDrumsRivalScope, PRO_DRUMS_RIVAL_SCOPE } from './helpers/comboUtils';
import { Layout, Font, Weight, Colors, Gap } from '@festival/theme';
import Page from '../Page';
import EmptyState from '../../components/common/EmptyState';
import PageHeader from '../../components/common/PageHeader';
import PageHeaderTransition from '../../components/common/PageHeaderTransition';
import { comboScopeLabel, isRankingScopeComboId } from '../../utils/rankingScopes';
import { coerceRankingMetric } from '../leaderboards/helpers/rankingHelpers';

const VALID_INSTRUMENTS = new Set<string>([
  'Solo_Guitar', 'Solo_Bass', 'Solo_Drums', 'Solo_Vocals',
  'Solo_PeripheralGuitar', 'Solo_PeripheralBass',
  'Solo_PeripheralVocals', 'Solo_PeripheralCymbals', 'Solo_PeripheralDrums',
]);

/* v8 ignore start -- page component with multiple context/hook dependencies */
export default function AllRivalsPage() {
  const { t } = useTranslation();
  const [searchParams] = useSearchParams();
  const category = searchParams.get('category') ?? 'common';
  const mode = searchParams.get('mode');
  const { settings } = useSettings();
  const rankBy = coerceRankingMetric(searchParams.get('rankBy'), settings.enableExperimentalRanks);
  const isLeaderboard = mode === 'leaderboard';
  const navigate = useNavigate();
  const isMobile = useIsMobile();
  const { player } = useTrackedPlayer();
  const accountId = player?.accountId;

  const activeInstruments = visibleInstruments(settings);
  const combo = useMemo(() => deriveRivalScopeFromSettings(settings), [settings]);

  // Determine mode from category
  const isCommon = category === 'common';
  const isExactCombo = isRankingScopeComboId(category);
  const isProDrumsFamily = isProDrumsRivalScope(category);
  const isCombo = category === 'combo' || isExactCombo || isProDrumsFamily;
  const isInstrument = VALID_INSTRUMENTS.has(category);
  const instrument = isInstrument ? (category as ServerInstrumentKey) : null;
  const resolvedCombo = isProDrumsFamily ? PRO_DRUMS_RIVAL_SCOPE : isExactCombo ? category : combo;
  const rivalsScopeKey = isCommon
    ? activeInstruments.join(',')
    : isCombo
      ? (resolvedCombo ?? 'none')
      : (instrument ?? 'none');

  const cacheKey = `${accountId ?? ''}:${category}:${mode ?? 'song'}:${isLeaderboard ? rankBy : 'song'}:${rivalsScopeKey}`;
  const commonQueries = useQueries({
    queries: activeInstruments.map(currentInstrument => ({
      queryKey: queryKeys.rivalsList(accountId ?? '', currentInstrument),
      queryFn: ({ signal }: { signal: AbortSignal }) => api.getRivalsList(accountId!, currentInstrument, { signal }),
      enabled: !isLeaderboard && isCommon && !!accountId && activeInstruments.length >= 2,
      ...remoteDataQueryPolicy,
    })),
  });
  const singleScope = isInstrument ? instrument : isCombo ? resolvedCombo : null;
  const singleQuery = useQuery({
    queryKey: queryKeys.rivalsList(accountId ?? '', singleScope ?? ''),
    queryFn: ({ signal }) => api.getRivalsList(accountId!, singleScope!, { signal }),
    enabled: !isLeaderboard && !isCommon && !!accountId && !!singleScope,
    ...remoteDataQueryPolicy,
  });
  const leaderboardQuery = useQuery({
    queryKey: queryKeys.leaderboardRivals(accountId ?? '', instrument ?? '', rankBy),
    queryFn: ({ signal }) => api.getLeaderboardRivals(instrument!, accountId!, rankBy, { signal }),
    enabled: isLeaderboard && isInstrument && !!accountId && !!instrument,
    ...remoteDataQueryPolicy,
  });
  const instrumentData = useMemo(() => (
    activeInstruments.flatMap((currentInstrument, index) => {
      const data = commonQueries[index]?.data;
      return data ? [{ instrument: currentInstrument, data }] : [];
    })
  ), [activeInstruments, commonQueries]);
  const singleData: RivalsListResponse | null = singleQuery.data ?? null;
  const leaderboardData: LeaderboardRivalsListResponse | null = leaderboardQuery.data ?? null;
  const loading = isLeaderboard
    ? !!(isInstrument && accountId && instrument && leaderboardQuery.isPending)
    : isCommon
      ? !!(accountId && activeInstruments.length >= 2 && commonQueries.some(query => query.isPending))
      : !!(accountId && singleScope && singleQuery.isPending);
  const mountedWithDataRef = useRef(
    isLeaderboard
      ? leaderboardQuery.data !== undefined || leaderboardQuery.isError
      : isCommon
        ? activeInstruments.length >= 2 && commonQueries.every(query => query.data !== undefined || query.isError)
        : singleQuery.data !== undefined || singleQuery.isError,
  );
  const initialCacheKeyRef = useRef(cacheKey);
  const hasCachedData = initialCacheKeyRef.current === cacheKey && mountedWithDataRef.current;

  // ─── Common rivals: intersection logic ───────────────────────

  const commonRivals = useMemo<{ above: RivalSummary[]; below: RivalSummary[] }>(() => {
    if (!isCommon || instrumentData.length < 2) return { above: [], below: [] };

    const countMap = new Map<string, number>();
    const summaryMap = new Map<string, { above: RivalSummary[]; below: RivalSummary[] }>();
    for (const { data } of instrumentData) {
      const seen = new Set<string>();
      for (const rival of [...data.above, ...data.below]) {
        if (seen.has(rival.accountId)) continue;
        seen.add(rival.accountId);
        countMap.set(rival.accountId, (countMap.get(rival.accountId) ?? 0) + 1);
        if (!summaryMap.has(rival.accountId)) summaryMap.set(rival.accountId, { above: [], below: [] });
        const bucket = summaryMap.get(rival.accountId)!;
        if (data.above.some(r => r.accountId === rival.accountId)) bucket.above.push(rival);
        else bucket.below.push(rival);
      }
    }

    const threshold = instrumentData.length;
    const above: RivalSummary[] = [];
    const below: RivalSummary[] = [];
    for (const [id, count] of countMap) {
      if (count < threshold) continue;
      const bucket = summaryMap.get(id)!;
      const dir = bucket.above.length >= bucket.below.length ? 'above' : 'below';
      const allEntries = [...bucket.above, ...bucket.below];
      const best = allEntries.reduce((a, b) => a.sharedSongCount >= b.sharedSongCount ? a : b);
      (dir === 'above' ? above : below).push(best);
    }

    above.sort((a, b) => b.rivalScore - a.rivalScore);
    below.sort((a, b) => b.rivalScore - a.rivalScore);
    return { above, below };
  }, [isCommon, instrumentData]);

  // ─── Resolved rivals for rendering ───────────────────────────

  type AnyRival = RivalSummary | LeaderboardRivalSummary;
  const rivals: { above: AnyRival[]; below: AnyRival[] } = isLeaderboard && leaderboardData
    ? { above: leaderboardData.above, below: leaderboardData.below }
    : isCommon
      ? commonRivals
      : singleData
        ? { above: singleData.above, below: singleData.below }
        : { above: [], below: [] };

  // ─── UI hooks ────────────────────────────────────────────────

  const { phase, shouldStagger } = usePageTransition(`rivals-all:${cacheKey}`, !loading, hasCachedData);
  useSetPageReady(phase === LoadPhase.ContentIn);
  const { forDelay: stagger, next: nextStagger, clearAnim } = useStagger(shouldStagger);

  if (!accountId) {
    return <div>{t('rivals.noPlayer')}</div>;
  }

  /** Compute CSS variable for min name width based on longest name in a rival list. */
  const nameWidthVar = (list: AnyRival[]): React.CSSProperties => {
    const maxLen = list.reduce((max, r) => Math.max(max, (r.displayName ?? 'Unknown Player').length), 0);
    return { '--rival-name-width': `${Math.ceil(maxLen * 0.85)}ch` } as React.CSSProperties;
  };

  const effectiveCombo = isCommon ? combo : isCombo ? resolvedCombo : instrument;
  const navigateToRival = (rivalId: string, rivalName?: string | null) => {
    navigate(Routes.rivalDetail(rivalId, rivalName ?? undefined), {
      state: isLeaderboard
        ? { source: 'leaderboard', instrument, rankBy, rivalName }
        : { combo: effectiveCombo, rivalName },
    });
  };

  const hasRivals = rivals.above.length > 0 || rivals.below.length > 0;
  const allRivals = [...rivals.above, ...rivals.below];

  // Title: "{icon} {friendly name} Rivals" — no icon for common
  const comboTitleLabel = isProDrumsRivalScope(resolvedCombo)
    ? t('rivals.proDrumsFamily')
    : isExactCombo
      ? comboScopeLabel(category)
      : t('rivals.combo');
  const titleText = isCommon
    ? t('rivals.commonRivalsShort', 'Common Rivals')
    : isInstrument
      ? t('rivals.instrumentRivalsShort', { instrument: serverInstrumentLabel(instrument!) })
      : t('rivals.instrumentRivalsShort', { instrument: comboTitleLabel });
  const showMobilePageHeader = !isMobile || settings.showButtonsInHeaderMobile;

  return (
    <Page
      scrollRestoreKey={`rivals-all:${accountId}:${category}:${mode ?? 'song'}`}
      scrollDeps={[phase]}
      loadPhase={phase}
      containerClassName={undefined}
      fabSpacer={phase === LoadPhase.ContentIn && !hasRivals ? 'none' : 'end'}
      before={isMobile ? (
        <PageHeaderTransition visible={showMobilePageHeader}>
          <PageHeader
            title={
              <h1 style={{ display: 'flex', alignItems: 'center', gap: Gap.sm, margin: 0, fontSize: Font.title, fontWeight: Weight.bold, color: Colors.textPrimary }}>
                {isInstrument && instrument && (
                  <InstrumentHeader instrument={instrument} size={InstrumentHeaderSize.SM} iconOnly />
                )}
                {titleText}
              </h1>
            }
          />
        </PageHeaderTransition>
      ) : showMobilePageHeader ? (
        <PageHeader
          title={
            <h1 style={{ display: 'flex', alignItems: 'center', gap: Gap.sm, margin: 0, fontSize: Font.title, fontWeight: Weight.bold, color: Colors.textPrimary }}>
              {isInstrument && instrument && (
                <InstrumentHeader instrument={instrument} size={InstrumentHeaderSize.SM} iconOnly />
              )}
              {titleText}
            </h1>
          }
        />
      ) : undefined}
    >
      {phase === LoadPhase.ContentIn && (
            <div style={isMobile ? { paddingBottom: Layout.fabPaddingBottom } : undefined}>
              {!hasRivals && (
                <EmptyState fullPage title={t('rivals.noRivals')} style={stagger(200)} onAnimationEnd={clearAnim} />
              )}

              {hasRivals && (
                <div style={{ paddingTop: 'var(--gap-md)' }}>
                  <div style={nameWidthVar(allRivals)}>
                    {allRivals.map(rival => (
                      <RivalRow
                        key={rival.accountId}
                        rival={rival}
                        direction={rivals.above.includes(rival) ? 'above' : 'below'}
                        onClick={() => navigateToRival(rival.accountId, rival.displayName)}
                        style={nextStagger()}
                        onAnimationEnd={clearAnim}
                      />
                    ))}
                  </div>
                </div>
              )}
            </div>
      )}
    </Page>
  );
}
/* v8 ignore stop */
