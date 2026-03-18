import { useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams, useNavigationType } from 'react-router-dom';
import { useFestival } from '../../contexts/FestivalContext';
import { usePlayerData } from '../../contexts/PlayerDataContext';
import { useSyncStatus } from '../../hooks/data/useSyncStatus';
import { api } from '../../api/client';
import ArcSpinner from '../../components/common/ArcSpinner';
import { useLoadPhase } from '../../hooks/data/useLoadPhase';
import s from './PlayerPage.module.css';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { queryKeys } from '../../api/queryKeys';
import PlayerContent from '../leaderboard/player/components/PlayerContent';


/** Track rendered accounts so we can skip stagger animation on revisit. */
let _renderedPlayerAccount: string | null = null;
let _renderedTrackedAccount: string | null = null;

export function clearPlayerPageCache() {
  _renderedPlayerAccount = null;
  _renderedTrackedAccount = null;
}

export default function PlayerPage({ accountId: propAccountId }: { accountId?: string } = {}) {
  const { t } = useTranslation();
  const params = useParams<{ accountId: string }>();
  const accountId = propAccountId ?? params.accountId;
  useNavigationType();
  const {
    state: { songs },
  } = useFestival();

  // Use cached context data when viewing the tracked player (statistics tab)
  const ctx = usePlayerData();
  const isTrackedPlayer = !!propAccountId;

  // Local state for when viewing an arbitrary player via URL Ã¢â‚¬â€ use React Query
  const { data: queryData, isLoading: queryLoading, error: queryError } = useQuery({
    queryKey: queryKeys.player(accountId ?? ''),
    queryFn: () => api.getPlayer(accountId!),
    enabled: !!accountId && !isTrackedPlayer,
  });
  const qc = useQueryClient();

  const { isSyncing: localSyncing, phase: localPhase, backfillProgress: localBfProg, historyProgress: localHrProg, justCompleted, clearCompleted } =
    useSyncStatus(!isTrackedPlayer ? accountId : undefined);

  // Auto-reload when sync completes
  useEffect(() => {
    if (justCompleted && accountId && !isTrackedPlayer) {
      clearCompleted();
      void qc.invalidateQueries({ queryKey: queryKeys.player(accountId) });
    }
  }, [justCompleted, clearCompleted, accountId, isTrackedPlayer, qc]);

  // Resolve effective values: context for tracked player, query for others
  const data = isTrackedPlayer ? ctx.playerData : (queryData ?? null);
  const loading = isTrackedPlayer ? ctx.playerLoading : queryLoading;
  const error = isTrackedPlayer ? ctx.playerError : (queryError ? (queryError instanceof Error ? queryError.message : t('player.failedToLoad')) : null);
  const isSyncing = isTrackedPlayer ? ctx.isSyncing : localSyncing;
  const phase = isTrackedPlayer ? ctx.syncPhase : localPhase;
  const backfillProgress = isTrackedPlayer ? ctx.backfillProgress : localBfProg;
  const historyProgress = isTrackedPlayer ? ctx.historyProgress : localHrProg;

  // Skip stagger if we've rendered this account before.
  const hasRendered = isTrackedPlayer
    ? _renderedTrackedAccount === accountId
    : _renderedPlayerAccount === accountId;
  const prevAccountRef = useRef(accountId);
  const skipAnimRef = useRef(hasRendered);
  // When accountId changes within the same component instance, re-evaluate skip
  /* v8 ignore start -- animation: skip-stagger state for re-renders */
  if (prevAccountRef.current !== accountId) {
    prevAccountRef.current = accountId;
    const alreadyRendered = isTrackedPlayer
      ? _renderedTrackedAccount === accountId
      : _renderedPlayerAccount === accountId;
    skipAnimRef.current = alreadyRendered;
  }
  /* v8 ignore stop */
  const skipAnim = skipAnimRef.current;
  const dataReady = !loading && !error && !!data;
  const { phase: loadPhase } = useLoadPhase(dataReady, { skipAnimation: skipAnim });
  if (data) {
    if (isTrackedPlayer) _renderedTrackedAccount = accountId!;
    else _renderedPlayerAccount = accountId!;
  }

  if (error) return <div className={s.page}><div className={s.centerError}>{error}</div></div>;
  if (!loading && !data) return <div className={s.page}><div className={s.center}>{t('player.playerNotFound')}</div></div>;

  return (
    <div className={s.page}>
      {loadPhase !== 'contentIn' && (
        <div
          className={s.center}
          style={loadPhase === 'spinnerOut' ? { animation: 'fadeOut 500ms ease-out forwards' } : undefined}
        >
          <ArcSpinner />
        </div>
      )}
      {loadPhase === 'contentIn' && data && (
        <PlayerContent key={accountId} data={data} songs={songs} isSyncing={isSyncing} phase={phase} backfillProgress={backfillProgress} historyProgress={historyProgress} isTrackedPlayer={isTrackedPlayer} skipAnim={skipAnim} />
      )}
    </div>
  );
}

