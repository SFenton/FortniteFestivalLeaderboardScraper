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
import { Gap, Font, Colors, flexColumn } from '@festival/theme';
import { serverInstrumentLabel, type ServerInstrumentKey } from '@festival/core/api/serverTypes';
import type {
  LeaderboardNeighborhoodResponse,
  CompositeNeighborhoodResponse,
} from '@festival/core/api/serverTypes';
import { LeaderboardNeighborRow } from './components/LeaderboardNeighborRow';
import { computeRankWidth } from '../leaderboards/helpers/rankingHelpers';
import { useRivalsSharedStyles } from './useRivalsSharedStyles';
import { Routes } from '../../routes';
import fx from '../../styles/effects.module.css';

// Module-level cache for instant back-navigation
let _cachedInstrumentNeighborhoods: InstrumentNeighborhood[] = [];
let _cachedCompositeNeighborhood: CompositeNeighborhoodResponse | null = null;
let _cachedAccountId: string | null = null;

type InstrumentNeighborhood = {
  instrument: ServerInstrumentKey;
  data: LeaderboardNeighborhoodResponse | null;
  loading: boolean;
  error: string | null;
};

interface LeaderboardRivalsTabProps {
  accountId: string;
  shouldStagger: boolean;
}

export default function LeaderboardRivalsTab({ accountId, shouldStagger }: LeaderboardRivalsTabProps) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { settings } = useSettings();
  const activeInstruments = visibleInstruments(settings);
  const showComposite = activeInstruments.length >= 2;

  const hasCached = accountId === _cachedAccountId && _cachedInstrumentNeighborhoods.length > 0;

  const [neighborhoods, setNeighborhoods] = useState<InstrumentNeighborhood[]>(
    hasCached ? _cachedInstrumentNeighborhoods : [],
  );
  const [composite, setComposite] = useState<CompositeNeighborhoodResponse | null>(
    hasCached ? _cachedCompositeNeighborhood : null,
  );
  const [compositeLoading, setCompositeLoading] = useState(false);

  // Fetch per-instrument neighborhoods
  /* v8 ignore start — async data fetch */
  useEffect(() => {
    if (!accountId || hasCached) return;
    let cancelled = false;

    const entries: InstrumentNeighborhood[] = activeInstruments.map(inst => ({
      instrument: inst,
      data: null,
      loading: true,
      error: null,
    }));
    setNeighborhoods(entries);

    activeInstruments.forEach((inst, idx) => {
      api.getLeaderboardNeighborhood(inst, accountId).then(res => {
        if (cancelled) return;
        setNeighborhoods(prev => {
          const next = [...prev];
          next[idx] = { instrument: inst, data: res, loading: false, error: null };
          return next;
        });
      }).catch(err => {
        if (cancelled) return;
        setNeighborhoods(prev => {
          const next = [...prev];
          next[idx] = { instrument: inst, data: null, loading: false, error: err instanceof Error ? err.message : 'Error' };
          return next;
        });
      });
    });

    return () => { cancelled = true; };
  // eslint-disable-next-line react-hooks/exhaustive-deps -- activeInstruments derived from settings
  }, [accountId, settings.showLead, settings.showBass, settings.showDrums, settings.showVocals, settings.showProLead, settings.showProBass]);
  /* v8 ignore stop */

  // Fetch composite neighborhood
  /* v8 ignore start — async data fetch */
  useEffect(() => {
    if (!accountId || !showComposite) {
      setComposite(null);
      setCompositeLoading(false);
      return;
    }
    if (hasCached) return;
    let cancelled = false;
    setCompositeLoading(true);

    api.getCompositeNeighborhood(accountId).then(res => {
      if (!cancelled) { setComposite(res); setCompositeLoading(false); }
    }).catch(() => {
      if (!cancelled) setCompositeLoading(false);
    });

    return () => { cancelled = true; };
  }, [accountId, showComposite]);
  /* v8 ignore stop */

  const allReady = neighborhoods.length > 0 && neighborhoods.every(n => !n.loading) && !compositeLoading;

  // Persist to module cache
  useEffect(() => {
    if (!allReady || !accountId) return;
    _cachedAccountId = accountId;
    _cachedInstrumentNeighborhoods = neighborhoods;
    _cachedCompositeNeighborhood = composite;
  }, [allReady, accountId, neighborhoods, composite]);

  const { forDelay: stagger, next: nextStagger, clearAnim } = useStagger(shouldStagger);
  const shared = useRivalsSharedStyles();

  const hasAnyData = neighborhoods.some(n => n.data) || composite;

  const LEADERBOARD_PREVIEW = 2;

  /* v8 ignore start -- JSX render tree */
  return (
    <div style={{ ...flexColumn, gap: Gap.section }}>
      {allReady && !hasAnyData && (
        <EmptyState fullPage title={t('rivals.leaderboardEmpty')} style={stagger(200)} onAnimationEnd={clearAnim} />
      )}

      {/* Per-instrument neighborhoods */}
      {neighborhoods.map(entry => {
        if (!entry.data) return null;
        const previewAbove = entry.data.above.slice(-LEADERBOARD_PREVIEW);
        const previewBelow = entry.data.below.slice(0, LEADERBOARD_PREVIEW);
        const sectionRankWidth = computeRankWidth([
          ...previewAbove.map(n => n.totalScoreRank),
          entry.data.self.totalScoreRank,
          ...previewBelow.map(n => n.totalScoreRank),
        ]);
        const navigateToRankings = () => navigate(Routes.fullRankings(entry.instrument));
        return (
          <div key={entry.instrument} style={shared.section}>
            <div
              className={fx.sectionHeaderClickable}
              style={{ ...shared.sectionHeaderClickable, ...nextStagger() }}
              onAnimationEnd={clearAnim}
              onClick={navigateToRankings}
              role="button"
              tabIndex={0}
              onKeyDown={e => { if (e.key === 'Enter') navigateToRankings(); }}
            >
              <InstrumentHeader instrument={entry.instrument} size={InstrumentHeaderSize.SM} iconOnly />
              <div style={shared.cardHeaderText}>
                <span style={shared.cardTitle}>
                  {t('rivals.instrumentRivalsShort', { instrument: serverInstrumentLabel(entry.instrument) })}
                </span>
                <span style={styles.subtitle}>{t('rivals.leaderboardSubtitle')}</span>
              </div>
              <span style={shared.seeAll}>{t('rivals.seeAll', 'See All')}</span>
              <IoChevronForward size={20} style={shared.chevron} />
            </div>
            <div style={styles.neighborhoodList}>
              {previewAbove.map(n => (
                <LeaderboardNeighborRow
                  key={n.accountId}
                  rank={n.totalScoreRank}
                  displayName={n.displayName ?? 'Unknown Player'}
                  score={n.totalScore}
                  songsPlayed={n.songsPlayed}
                  accountId={n.accountId}
                  rankWidth={sectionRankWidth}
                  style={nextStagger()}
                  onAnimationEnd={clearAnim}
                />
              ))}
              <LeaderboardNeighborRow
                rank={entry.data.self.totalScoreRank}
                displayName={entry.data.self.displayName ?? 'Unknown Player'}
                score={entry.data.self.totalScore}
                songsPlayed={entry.data.self.songsPlayed}
                accountId={entry.data.self.accountId}
                isPlayer
                rankWidth={sectionRankWidth}
                style={nextStagger()}
                onAnimationEnd={clearAnim}
              />
              {previewBelow.map(n => (
                <LeaderboardNeighborRow
                  key={n.accountId}
                  rank={n.totalScoreRank}
                  displayName={n.displayName ?? 'Unknown Player'}
                  score={n.totalScore}
                  songsPlayed={n.songsPlayed}
                  accountId={n.accountId}
                  rankWidth={sectionRankWidth}
                  style={nextStagger()}
                  onAnimationEnd={clearAnim}
                />
              ))}
              <div style={{ ...shared.viewAllButton, ...nextStagger() }} onAnimationEnd={clearAnim} onClick={navigateToRankings}>
                {t('rivals.viewAllRankings')}
              </div>
            </div>
          </div>
        );
      })}

      {/* Composite neighborhood */}
      {composite && (() => {
        const previewAbove = composite.above.slice(-LEADERBOARD_PREVIEW);
        const previewBelow = composite.below.slice(0, LEADERBOARD_PREVIEW);
        const compositeRankWidth = computeRankWidth([
          ...previewAbove.map(n => n.compositeRank),
          composite.self.compositeRank,
          ...previewBelow.map(n => n.compositeRank),
        ]);
        return (
        <div style={shared.section}>
          <div style={{ ...shared.sectionHeader, ...nextStagger() }} onAnimationEnd={clearAnim}>
            <div style={shared.cardHeaderText}>
              <span style={shared.cardTitle}>{t('rivals.leaderboardComposite')}</span>
              <span style={styles.subtitle}>{t('rivals.leaderboardSubtitle')}</span>
            </div>
          </div>
          <div style={styles.neighborhoodList}>
            {previewAbove.map(n => (
              <LeaderboardNeighborRow
                key={n.accountId}
                rank={n.compositeRank}
                displayName={n.displayName ?? 'Unknown Player'}
                score={n.totalSongsPlayed}
                songsPlayed={n.instrumentsPlayed}
                accountId={n.accountId}
                rankWidth={compositeRankWidth}
                style={nextStagger()}
                onAnimationEnd={clearAnim}
              />
            ))}
            <LeaderboardNeighborRow
              rank={composite.self.compositeRank}
              displayName={composite.self.displayName ?? 'Unknown Player'}
              score={composite.self.totalSongsPlayed}
              songsPlayed={composite.self.instrumentsPlayed}
              accountId={composite.self.accountId}
              isPlayer
              rankWidth={compositeRankWidth}
              style={nextStagger()}
              onAnimationEnd={clearAnim}
            />
            {previewBelow.map(n => (
              <LeaderboardNeighborRow
                key={n.accountId}
                rank={n.compositeRank}
                displayName={n.displayName ?? 'Unknown Player'}
                score={n.totalSongsPlayed}
                songsPlayed={n.instrumentsPlayed}
                accountId={n.accountId}
                rankWidth={compositeRankWidth}
                style={nextStagger()}
                onAnimationEnd={clearAnim}
              />
            ))}
            <div style={{ ...shared.viewAllButton, ...nextStagger() }} onAnimationEnd={clearAnim} onClick={() => navigate(Routes.leaderboards)}>
              {t('rivals.viewAllRankings')}
            </div>
          </div>
        </div>
        );
      })()}
    </div>
  );
  /* v8 ignore stop */
}

const styles = {
  subtitle: {
    fontSize: Font.xs,
    color: Colors.textSecondary,
  },
  neighborhoodList: {
    ...flexColumn,
    gap: Gap.sm,
  },
} as const;
