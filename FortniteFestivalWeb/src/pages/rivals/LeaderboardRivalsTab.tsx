/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import { api } from '../../api/client';
import { useSettings, visibleInstruments } from '../../contexts/SettingsContext';
import { useStagger } from '../../hooks/ui/useStagger';
import EmptyState from '../../components/common/EmptyState';
import InstrumentHeader from '../../components/display/InstrumentHeader';
import { InstrumentHeaderSize } from '@festival/core';
import { IoChevronForward } from 'react-icons/io5';
import { Gap, flexColumn } from '@festival/theme';
import { serverInstrumentLabel, type ServerInstrumentKey, type RankingMetric } from '@festival/core/api/serverTypes';
import type { LeaderboardRivalsListResponse, LeaderboardRivalSummary } from '@festival/core/api/serverTypes';
import RivalRow from './components/RivalRow';
import { useRivalsSharedStyles } from './useRivalsSharedStyles';
import { Routes } from '../../routes';
import fx from '../../styles/effects.module.css';

// Module-level cache for instant back-navigation
let _cachedInstrumentRivals: InstrumentLeaderboardRivals[] = [];
let _cachedAccountId: string | null = null;
let _cachedRankBy: string | null = null;

type InstrumentLeaderboardRivals = {
  instrument: ServerInstrumentKey;
  data: LeaderboardRivalsListResponse | null;
  loading: boolean;
  error: string | null;
};

interface LeaderboardRivalsTabProps {
  accountId: string;
  shouldStagger: boolean;
  rankBy: RankingMetric;
}

export default function LeaderboardRivalsTab({ accountId, shouldStagger, rankBy }: LeaderboardRivalsTabProps) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { settings } = useSettings();
  const activeInstruments = visibleInstruments(settings);

  const hasCached = accountId === _cachedAccountId
    && rankBy === _cachedRankBy
    && _cachedInstrumentRivals.length > 0;

  const [instrumentRivals, setInstrumentRivals] = useState<InstrumentLeaderboardRivals[]>(
    hasCached ? _cachedInstrumentRivals : [],
  );

  // Fetch per-instrument leaderboard rivals
  /* v8 ignore start — async data fetch */
  useEffect(() => {
    if (!accountId || hasCached) return;
    let cancelled = false;

    const entries: InstrumentLeaderboardRivals[] = activeInstruments.map(inst => ({
      instrument: inst,
      data: null,
      loading: true,
      error: null,
    }));
    setInstrumentRivals(entries);

    activeInstruments.forEach((inst, idx) => {
      api.getLeaderboardRivals(inst, accountId, rankBy).then(res => {
        if (cancelled) return;
        setInstrumentRivals(prev => {
          const next = [...prev];
          next[idx] = { instrument: inst, data: res, loading: false, error: null };
          return next;
        });
      }).catch(err => {
        if (cancelled) return;
        setInstrumentRivals(prev => {
          const next = [...prev];
          next[idx] = { instrument: inst, data: null, loading: false, error: err instanceof Error ? err.message : 'Error' };
          return next;
        });
      });
    });

    return () => { cancelled = true; };
  // eslint-disable-next-line react-hooks/exhaustive-deps -- activeInstruments derived from settings
  }, [accountId, rankBy, settings.showLead, settings.showBass, settings.showDrums, settings.showVocals, settings.showProLead, settings.showProBass]);
  /* v8 ignore stop */

  const allReady = instrumentRivals.length > 0 && instrumentRivals.every(r => !r.loading);

  // Persist to module cache
  useEffect(() => {
    if (!allReady || !accountId) return;
    _cachedAccountId = accountId;
    _cachedRankBy = rankBy;
    _cachedInstrumentRivals = instrumentRivals;
  }, [allReady, accountId, rankBy, instrumentRivals]);

  const { next: nextStagger, clearAnim } = useStagger(shouldStagger);
  const shared = useRivalsSharedStyles();

  const hasAnyRivals = instrumentRivals.some(r =>
    r.data && (r.data.above.length > 0 || r.data.below.length > 0),
  );

  const hasAnyError = instrumentRivals.some(r => r.error);

  const PREVIEW_COUNT = 3;

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
          <div key={entry.instrument} style={shared.section}>
            <div
              className={fx.sectionHeaderClickable}
              style={{ ...shared.sectionHeaderClickable, ...nextStagger() }}
              onAnimationEnd={clearAnim}
              onClick={navigateToAllRivals}
              role="button"
              tabIndex={0}
              onKeyDown={e => { if (e.key === 'Enter') navigateToAllRivals(); }}
            >
              <InstrumentHeader instrument={entry.instrument} size={InstrumentHeaderSize.SM} iconOnly />
              <div style={shared.cardHeaderText}>
                <span style={shared.cardTitle}>
                  {t('rivals.instrumentRivalsShort', { instrument: serverInstrumentLabel(entry.instrument) })}
                </span>
              </div>
              <span style={shared.seeAll}>{t('rivals.seeAll', 'See All')}</span>
              <IoChevronForward size={20} style={shared.chevron} />
            </div>
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
              <div style={{ ...shared.viewAllButton, ...nextStagger() }} onAnimationEnd={clearAnim} onClick={navigateToAllRivals}>
                {t('rivals.viewAllRivals')}
              </div>
            </div>
          </div>
        );
      })}
    </div>
  );
  /* v8 ignore stop */
}
