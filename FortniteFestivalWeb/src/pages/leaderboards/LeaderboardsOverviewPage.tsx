/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useState, useEffect, useCallback, useMemo, useRef, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { useQueries } from '@tanstack/react-query';
import { useSearchParams } from 'react-router-dom';
import { IoOptions } from 'react-icons/io5';
import { api } from '../../api/client';
import { queryKeys } from '../../api/queryKeys';
import { useSettings, visibleInstruments } from '../../contexts/SettingsContext';
import { useTrackedPlayer } from '../../hooks/data/useTrackedPlayer';
import Page from '../Page';
import PageHeader from '../../components/common/PageHeader';
import { ActionPill } from '../../components/common/ActionPill';
import Modal from '../../components/modals/Modal';
import { ModalSection } from '../../components/modals/components/ModalSection';
import { RadioRow } from '../../components/common/RadioRow';
import RankingCard from './components/RankingCard';
import EmptyState from '../../components/common/EmptyState';
import { parseApiError } from '../../utils/apiError';
import { buildStaggerStyle, clearStaggerStyle } from '../../hooks/ui/useStaggerStyle';
import type { RankingMetric, ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { LoadPhase } from '@festival/core';
import { RANKING_METRICS, EXPERIMENTAL_METRICS } from './helpers/rankingHelpers';
import { loadLeaderboardRankBy, saveLeaderboardRankBy } from '../../utils/leaderboardSettings';
import { useModalState } from '../../hooks/ui/useModalState';
import { useIsMobileChrome } from '../../hooks/ui/useIsMobile';
import { useGridColumnCount } from '../../hooks/ui/useGridColumnCount';
import { useFabSearch } from '../../contexts/FabSearchContext';

import {
  Display, Overflow, Gap,
  GridTemplate, Size, STAGGER_INTERVAL, FADE_DURATION,
} from '@festival/theme';
import { leaderboardsSlides } from './firstRun';
import FirstRunCarousel from '../../components/firstRun/FirstRunCarousel';
import { getMetricInfoSlides } from './firstRun/metricInfo';

/** Set to 1 to stagger the right column one slot (125 ms) after the left. */
const COLUMN_STAGGER_OFFSET = 1;

export default function LeaderboardsOverviewPage() {
  const { t } = useTranslation();
  const { settings } = useSettings();
  const { player } = useTrackedPlayer();
  const isMobile = useIsMobileChrome();
  const fabSearch = useFabSearch();
  const [searchParams, setSearchParams] = useSearchParams();
  const rawMetric = (searchParams.get('rankBy') ?? loadLeaderboardRankBy()) as RankingMetric;
  const metric = settings.enableExperimentalRanks ? rawMetric : 'totalscore' as RankingMetric;

  const metricModal = useModalState<RankingMetric>(() => 'totalscore');
  const [infoMetric, setInfoMetric] = useState<RankingMetric | null>(null);

  const openMetricModal = useCallback(() => {
    metricModal.open(metric);
  }, [metricModal, metric]);

  const applyMetric = useCallback(() => {
    scrollRef.current?.scrollTo(0, 0);
    setShouldStagger(true);
    saveLeaderboardRankBy(metricModal.draft);
    setSearchParams({ rankBy: metricModal.draft }, { replace: true });
    metricModal.close();
  }, [metricModal, setSearchParams]);

  useEffect(() => {
    fabSearch.registerLeaderboardActions({ openMetric: openMetricModal, openInstrument: () => {} });
    return () => fabSearch.registerLeaderboardActions({ openMetric: () => {}, openInstrument: () => {} });
  }, [fabSearch, openMetricModal]);

  const instruments = useMemo(() => visibleInstruments(settings), [settings]);

  // Fetch top-10 per visible instrument
  const rankingQueries = useQueries({
    queries: instruments.map((inst) => ({
      queryKey: queryKeys.rankings(inst, metric, 1, 10),
      queryFn: () => api.getRankings(inst, metric, 1, 10),
    })),
  });

  // Fetch player ranking per visible instrument (only when player is tracked)
  const playerQueries = useQueries({
    queries: player
      ? instruments.map((inst) => ({
          queryKey: queryKeys.playerRanking(inst, player.accountId),
          queryFn: () => api.getPlayerRanking(inst, player.accountId),
        }))
      : [],
  });

  const isLoading = rankingQueries.some(q => q.isLoading);
  const hasCachedData = rankingQueries.every(q => q.data != null);
  const allErrored = !isLoading && rankingQueries.length > 0 && rankingQueries.every(q => q.error);
  const loadPhase = isLoading ? LoadPhase.Loading : LoadPhase.ContentIn;

  const [shouldStagger, setShouldStagger] = useState(!hasCachedData);
  const maxEntriesPerCard = useMemo(
    () => Math.max(0, ...rankingQueries.map(q => q.data?.entries?.length ?? 0)),
    [rankingQueries],
  );
  const scrollRef = useRef<HTMLDivElement>(null);
  const [cols, gridRef] = useGridColumnCount();
  const itemsPerCard = maxEntriesPerCard + (player ? 3 : 2); // header + entries + (player footer?) + button

  useEffect(() => {
    if (loadPhase !== LoadPhase.ContentIn || !shouldStagger) return;
    const totalRows = Math.ceil(instruments.length / cols);
    const totalAnimTime =
      (totalRows * itemsPerCard + (cols - 1) * COLUMN_STAGGER_OFFSET) * STAGGER_INTERVAL + FADE_DURATION;
    const id = setTimeout(() => setShouldStagger(false), totalAnimTime);
    return () => clearTimeout(id);
  }, [loadPhase, shouldStagger, maxEntriesPerCard, cols, instruments.length, itemsPerCard]);

  const s = useLeaderboardsStyles();
  const firstRunGateCtx = useMemo(() => ({ hasPlayer: !!player, experimentalRanksEnabled: settings.enableExperimentalRanks }), [player, settings.enableExperimentalRanks]);

  return (
    <Page
      scrollRef={scrollRef}
      scrollRestoreKey="leaderboards"
      loadPhase={loadPhase}
      fabSpacer={loadPhase === LoadPhase.ContentIn && allErrored ? 'none' : 'end'}
      firstRun={{ key: 'leaderboards', label: t('nav.leaderboards'), slides: leaderboardsSlides, gateContext: firstRunGateCtx }}
      before={
        isMobile ? undefined : (
        <PageHeader
          title={t('rankings.title')}
          actions={
            !isMobile && !allErrored && settings.enableExperimentalRanks ? (
              <ActionPill
                icon={<IoOptions size={Size.iconAction} />}
                label={t(`rankings.metric.${metric}`)}
                onClick={openMetricModal}
                active={metric !== 'totalscore'}
              />
            ) : undefined
          }
        />
        )
      }
      after={<>
        <Modal
          visible={metricModal.visible}
          title={t('rankings.rankBy')}
          onClose={metricModal.close}
          onApply={applyMetric}
          onReset={metricModal.reset}
          resetLabel={t('rankings.rankByReset')}
          resetHint={t('rankings.rankByResetHint')}
        >
          <ModalSection title={t('rankings.rankBy')} hint={t('rankings.rankByHint')}>
            {RANKING_METRICS.map((m) => (
              <RadioRow
                key={m}
                label={t(`rankings.metric.${m}`)}
                hint={t(`rankings.metric.${m}Desc`)}
                selected={metricModal.draft === m}
                onSelect={() => metricModal.setDraft(m)}
                onInfo={EXPERIMENTAL_METRICS.includes(m) ? () => setInfoMetric(m) : undefined}
              />
            ))}
          </ModalSection>
        </Modal>
        {infoMetric && <FirstRunCarousel slides={getMetricInfoSlides(infoMetric)} onDismiss={() => {}} onExitComplete={() => setInfoMetric(null)} />}
      </>}
    >
      {loadPhase === LoadPhase.ContentIn && allErrored && (() => {
        const parsed = parseApiError(String(rankingQueries[0]!.error));
        return <EmptyState fullPage title={parsed.title} subtitle={parsed.subtitle} style={buildStaggerStyle(200)} onAnimationEnd={clearStaggerStyle} />;
      })()}
      {loadPhase === LoadPhase.ContentIn && !allErrored && (
        <div ref={gridRef} style={s.grid}>
          {instruments.map((inst, idx) => {
            const q = rankingQueries[idx];
            const pq = player ? playerQueries[idx] : undefined;
            const gridRow = Math.floor(idx / cols);
            const gridCol = idx % cols;
            const offset = gridRow * itemsPerCard + gridCol * COLUMN_STAGGER_OFFSET;
            return (
              <RankingCard
                key={inst}
                instrument={inst as InstrumentKey}
                metric={metric}
                entries={q?.data?.entries ?? []}
                totalAccounts={q?.data?.totalAccounts ?? 0}
                playerRanking={pq?.data ?? null}
                playerAccountId={player?.accountId}
                error={q?.error ? String(q.error) : null}
                shouldStagger={shouldStagger}
                staggerOffset={offset}
              />
            );
          })}
        </div>
      )}
    </Page>
  );
}

function useLeaderboardsStyles() {
  return useMemo(() => ({
    grid: {
      display: Display.grid,
      gridTemplateColumns: GridTemplate.autoFillInstrument,
      gap: `${Gap.section}px ${Gap.md}px`,
      overflow: Overflow.hidden,
    } as CSSProperties,
  }), []);
}
