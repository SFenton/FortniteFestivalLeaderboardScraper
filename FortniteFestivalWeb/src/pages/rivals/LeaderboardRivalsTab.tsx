import { useEffect, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import { useQueries } from '@tanstack/react-query';
import { api } from '../../api/client';
import { queryKeys } from '../../api/queryKeys';
import { remoteDataQueryPolicy } from '../../api/queryPolicy';
import { useSettings, visibleInstruments } from '../../contexts/SettingsContext';
import { staggerCompletionDelay, useStagger } from '../../hooks/ui/useStagger';
import EmptyState from '../../components/common/EmptyState';
import InstrumentHeader from '../../components/display/InstrumentHeader';
import { InstrumentIcon } from '../../components/display/InstrumentIcons';
import { InstrumentHeaderSize } from '@festival/core';
import { IoChevronForward } from 'react-icons/io5';
import { Gap, flexColumn } from '@festival/theme';
import { serverInstrumentLabel, type ServerInstrumentKey, type RankingMetric } from '@festival/core/api/serverTypes';
import type { LeaderboardRivalsListResponse, LeaderboardRivalSummary } from '@festival/core/api/serverTypes';
import RivalRow from './components/RivalRow';
import CardPressable from '../../components/common/CardPressable';
import { useRivalsSharedStyles } from './useRivalsSharedStyles';
import { Routes } from '../../routes';
import fx from '../../styles/effects.module.css';
import type { PageQuickLinkItem } from '../../hooks/ui/usePageQuickLinks';

type InstrumentLeaderboardRivals = {
  instrument: ServerInstrumentKey;
  data: LeaderboardRivalsListResponse | null;
  loading: boolean;
  error: string | null;
};

const QUICK_LINK_GLYPH_ICON_SIZE = 20;

export type LeaderboardRivalQuickLink = PageQuickLinkItem & {
  id: ServerInstrumentKey;
};

interface LeaderboardRivalsTabProps {
  accountId: string;
  shouldStagger: boolean;
  rankBy: RankingMetric;
  registerSectionRef: (id: string, element: HTMLElement | null) => void;
  onQuickLinksChange?: (items: LeaderboardRivalQuickLink[]) => void;
  onDesktopRailRevealDelayChange?: (delayMs: number) => void;
}

export default function LeaderboardRivalsTab({
  accountId,
  shouldStagger,
  rankBy,
  registerSectionRef,
  onQuickLinksChange,
  onDesktopRailRevealDelayChange,
}: LeaderboardRivalsTabProps) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { settings } = useSettings();
  const activeInstruments = visibleInstruments(settings);
  const queries = useQueries({
    queries: activeInstruments.map(instrument => ({
      queryKey: queryKeys.leaderboardRivals(accountId, instrument, rankBy),
      queryFn: () => api.getLeaderboardRivals(instrument, accountId, rankBy),
      enabled: !!accountId,
      ...remoteDataQueryPolicy,
    })),
  });
  const instrumentRivals = useMemo<InstrumentLeaderboardRivals[]>(() => (
    activeInstruments.map((instrument, index) => {
      const query = queries[index];
      return {
        instrument,
        data: query?.data ?? null,
        loading: query?.isPending ?? true,
        error: query?.error
          ? (query.error instanceof Error ? query.error.message : 'Error')
          : null,
      };
    })
  ), [activeInstruments, queries]);

  const allReady = activeInstruments.length === 0 || instrumentRivals.every(r => !r.loading);

  const { next: nextStagger, clearAnim } = useStagger(shouldStagger);
  const shared = useRivalsSharedStyles();

  const hasAnyRivals = instrumentRivals.some(r =>
    r.data && (r.data.above.length > 0 || r.data.below.length > 0),
  );

  const quickLinkItems = useMemo<LeaderboardRivalQuickLink[]>(() => instrumentRivals.flatMap((entry) => {
    if (!entry.data || (entry.data.above.length === 0 && entry.data.below.length === 0)) {
      return [];
    }

    const instrumentLabel = t('rivals.instrumentRivalsShort', { instrument: serverInstrumentLabel(entry.instrument) });

    return [{
      id: entry.instrument,
      label: instrumentLabel,
      landmarkLabel: instrumentLabel,
      icon: (
        <InstrumentIcon
          instrument={entry.instrument}
          size={QUICK_LINK_GLYPH_ICON_SIZE}
        />
      ),
    }];
  }), [instrumentRivals, t]);

  useEffect(() => {
    onQuickLinksChange?.(quickLinkItems);
  }, [onQuickLinksChange, quickLinkItems]);

  useEffect(() => () => {
    onQuickLinksChange?.([]);
  }, [onQuickLinksChange]);

  const hasAnyError = instrumentRivals.some(r => r.error);

  const PREVIEW_COUNT = 3;

  const desktopRailRevealDelayMs = useMemo(() => {
    if (!shouldStagger) {
      return 0;
    }

    if (allReady && !hasAnyRivals) {
      return staggerCompletionDelay(1);
    }

    let staggerItemCount = 0;
    for (const entry of instrumentRivals) {
      if (!entry.data || (entry.data.above.length === 0 && entry.data.below.length === 0)) {
        continue;
      }

      const previewCount = Math.min(PREVIEW_COUNT, entry.data.above.length) + Math.min(PREVIEW_COUNT, entry.data.below.length);
      staggerItemCount += previewCount + 2;
    }

    return staggerCompletionDelay(staggerItemCount);
  }, [allReady, hasAnyRivals, instrumentRivals, shouldStagger]);

  useEffect(() => {
    onDesktopRailRevealDelayChange?.(desktopRailRevealDelayMs);
  }, [desktopRailRevealDelayMs, onDesktopRailRevealDelayChange]);

  useEffect(() => () => {
    onDesktopRailRevealDelayChange?.(0);
  }, [onDesktopRailRevealDelayChange]);

  /* v8 ignore start -- render helpers */
  const navigateToRival = (instrument: ServerInstrumentKey, rivalId: string, rivalName?: string | null) => {
    navigate(Routes.rivalDetail(rivalId, rivalName ?? undefined), {
      state: { source: 'leaderboard', instrument, rankBy, rivalName },
    });
  };
  /* v8 ignore stop */

  /* v8 ignore start -- JSX render tree */
  return (
    <div style={{ ...flexColumn, gap: Gap.section }}>
      {allReady && !hasAnyRivals && (
        <EmptyState
          fullPage
          title={hasAnyError ? t('common.failedToLoad') : t('rivals.leaderboardEmpty')}
          style={nextStagger()}
          onAnimationEnd={clearAnim}
        />
      )}

      {instrumentRivals.map(entry => {
        if (!entry.data) return null;
        const { above, below } = entry.data;
        if (above.length === 0 && below.length === 0) return null;

        const previewAbove = above.slice(0, PREVIEW_COUNT);
        const previewBelow = below.slice(0, PREVIEW_COUNT);
        const allPreview = [...previewAbove, ...previewBelow];

        const navigateToAllRivals = () => navigate(Routes.allRivals(entry.instrument, 'leaderboard', rankBy));

        return (
          <div key={entry.instrument} ref={(element) => registerSectionRef(entry.instrument, element)} style={shared.section}>
            <CardPressable
              className={fx.sectionHeaderClickable}
              style={{ ...shared.sectionHeaderClickable, ...nextStagger() }}
              pressedStyle={shared.pressablePressed}
              onAnimationEnd={clearAnim}
              onPress={navigateToAllRivals}
            >
              <InstrumentHeader instrument={entry.instrument} size={InstrumentHeaderSize.SM} iconOnly />
              <div style={shared.cardHeaderText}>
                <span style={shared.cardTitle}>
                  {t('rivals.instrumentRivalsShort', { instrument: serverInstrumentLabel(entry.instrument) })}
                </span>
              </div>
              <span style={shared.seeAll}>{t('rivals.seeAll', 'See All')}</span>
              <IoChevronForward size={20} style={shared.chevron} />
            </CardPressable>
            <div style={shared.rivalList}>
              {allPreview.map((rival: LeaderboardRivalSummary) => (
                <RivalRow
                  key={rival.accountId}
                  rival={rival}
                  direction={previewAbove.includes(rival) ? 'above' : 'below'}
                  onClick={() => navigateToRival(entry.instrument, rival.accountId, rival.displayName)}
                  style={nextStagger()}
                  onAnimationEnd={clearAnim}
                />
              ))}
              <CardPressable style={{ ...shared.viewAllButton, ...nextStagger() }} pressedStyle={shared.pressablePressed} onAnimationEnd={clearAnim} onPress={navigateToAllRivals}>
                {t('rivals.viewAllRivals')}
              </CardPressable>
            </div>
          </div>
        );
      })}
    </div>
  );
  /* v8 ignore stop */
}
