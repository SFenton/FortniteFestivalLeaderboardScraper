/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useEffect, useRef, useState, useCallback, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams, useNavigationType } from 'react-router-dom';
import { IoSwapVerticalSharp } from 'react-icons/io5';
import { useVirtualizer } from '@tanstack/react-virtual';
import { useFestival } from '../../../contexts/FestivalContext';
import { useTrackedPlayer } from '../../../hooks/data/useTrackedPlayer';
import { useFabSearch } from '../../../contexts/FabSearchContext';
import { useModalState } from '../../../hooks/ui/useModalState';
import { api } from '../../../api/client';
import {
  type ServerInstrumentKey as InstrumentKey,
  type ServerScoreHistoryEntry as ScoreHistoryEntry,
} from '@festival/core/api/serverTypes';
import SongInfoHeader from '../../../components/songs/headers/SongInfoHeader';
import { LeaderboardEntry } from '../global/components/LeaderboardEntry';
import PlayerScoreSortModal from './modals/PlayerScoreSortModal';
import type { PlayerScoreSortMode, PlayerScoreSortDraft } from './modals/PlayerScoreSortModal';
import { Gap, Size, QUERY_SHOW_ACCURACY, QUERY_SHOW_SEASON } from '@festival/theme';
import ArcSpinner from '../../../components/common/ArcSpinner';
import { ActionPill } from '../../../components/common/ActionPill';
import s from './PlayerHistoryPage.module.css';
import { staggerDelay, estimateVisibleCount } from '@festival/ui-utils';
import { useScrollMask } from '../../../hooks/ui/useScrollMask';
import { useStaggerRush } from '../../../hooks/ui/useStaggerRush';
import { useIsMobile } from '../../../hooks/ui/useIsMobile';
import { useScoreFilter } from '../../../hooks/data/useScoreFilter';
import { useSortedScoreHistory } from '../../../hooks/data/useSortedScoreHistory';
import { PlayerScoreSortMode as CoreSortMode } from '@festival/core';
import { useMediaQuery } from '../../../hooks/ui/useMediaQuery';
import { useHeaderCollapse } from '../../../hooks/ui/useHeaderCollapse';
import { useScrollRestore } from '../../../hooks/ui/useScrollRestore';
import { useLoadPhase } from '../../../hooks/data/useLoadPhase';
import { IS_IOS, IS_ANDROID, IS_PWA } from '@festival/ui-utils';
import { useRegisterFirstRun } from '../../../hooks/ui/useRegisterFirstRun';
import { useFirstRun } from '../../../hooks/ui/useFirstRun';
import FirstRunCarousel from '../../../components/firstRun/FirstRunCarousel';
import { playerHistorySlides } from './firstRun';

export default function PlayerHistoryPage() {
  const { t } = useTranslation();
  const { songId, instrument } = useParams<{
    songId: string;
    instrument: string;
  }>();
  const {
    state: { songs },
  } = useFestival();
  const { player } = useTrackedPlayer();

  const song = songs.find((s) => s.songId === songId);
  const instKey = instrument as InstrumentKey;
  const navType = useNavigationType();

  const showAccuracy = useMediaQuery(QUERY_SHOW_ACCURACY);
  const showSeason = useMediaQuery(QUERY_SHOW_SEASON);
  const isMobile = !showAccuracy;
  const hasFab = useIsMobile();

  // First-run carousel
  const historySlidesMemo = useMemo(() => playerHistorySlides(hasFab), [hasFab]);
  useRegisterFirstRun('playerhistory', t('history.title'), historySlidesMemo);
  const firstRun = useFirstRun('playerhistory', { hasPlayer: !!player });

  const [history, setHistory] = useState<ScoreHistoryEntry[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const { filterHistory } = useScoreFilter();
  const scrollRef = useRef<HTMLDivElement>(null);
  const saveScroll = useScrollRestore(scrollRef, `history:${songId}:${instKey}`, navType);
  const [headerCollapsed, updateHeaderCollapse] = useHeaderCollapse(scrollRef, { disabled: hasFab, forcedValue: hasFab });
  const { phase: loadPhase } = useLoadPhase(!loading && !error);
  const updateScrollMask = useScrollMask(scrollRef, [loadPhase, history.length]);

  // Sort state
  const DEFAULT_SORT: PlayerScoreSortDraft = { sortMode: 'score', sortAscending: false };
  const [sortMode, setSortMode] = useState<PlayerScoreSortMode>('score');
  const [sortAscending, setSortAscending] = useState(false);
  const sortModal = useModalState(() => DEFAULT_SORT);
  const [staggerKey, setStaggerKey] = useState(0);

  const openSort = useCallback(() => {
    sortModal.open({ sortMode, sortAscending });
  }, [sortMode, sortAscending, sortModal]);
  /* v8 ignore start — modal apply callback */
  const applySort = () => {
    setSortMode(sortModal.draft.sortMode);
    setSortAscending(sortModal.draft.sortAscending);
    sortModal.close();
    staggerDoneRef.current = false;
    resetRush();
    setStaggerKey(k => k + 1);
  };
  /* v8 ignore stop */

  // Register sort action for FAB
  const fabSearch = useFabSearch();
  const openSortRef = useRef(openSort);
  openSortRef.current = openSort;
  /* v8 ignore start — FAB registration callback */
  useEffect(() => {
    fabSearch.registerPlayerHistoryActions({ openSort: () => openSortRef.current() });
  }, [fabSearch]);
  /* v8 ignore stop */

  const { rushOnScroll, resetRush } = useStaggerRush(scrollRef);
  /* v8 ignore start — scroll handler */
  const handleScroll = useCallback(() => {
    saveScroll();
    updateScrollMask();
    rushOnScroll();
    updateHeaderCollapse();
  }, [saveScroll, updateScrollMask, rushOnScroll, updateHeaderCollapse]);
  /* v8 ignore stop */

  /* v8 ignore start — async data fetch with cancellation */
  useEffect(() => {
    if (!player || !songId) {
      setLoading(false);
      return;
    }
    let cancelled = false;
    setLoading(true);
    setError(null);
    api.getPlayerHistory(player.accountId, songId)
      .then((res) => {
        if (!cancelled) {
          const filtered = res.history
            .filter(h => h.instrument === instKey);
          setHistory(filtered);
        }
      })
      .catch((e) => {
        if (!cancelled) setError(e instanceof Error ? e.message : t('history.failedToLoad'));
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => { cancelled = true; };
  // eslint-disable-next-line react-hooks/exhaustive-deps -- t is stable i18n fn
  }, [player, songId, instKey]);
  /* v8 ignore stop */

  const filteredHistory = useMemo(
    () => songId ? filterHistory(songId, instKey, history) : history,
    [songId, instKey, history, filterHistory],
  );

  const sortedHistory = useSortedScoreHistory(filteredHistory, sortMode as CoreSortMode, sortAscending);

  const scoreWidth = useMemo(() => {
    const maxLen = Math.max(
      ...filteredHistory.map((h) => h.newScore.toLocaleString().length),
      1,
    );
    return `${maxLen}ch`;
  }, [filteredHistory]);

  const highScoreIndex = useMemo(() => {
    if (sortedHistory.length === 0) return -1;
    let best = 0;
    for (let i = 1; i < sortedHistory.length; i++) {
      if (sortedHistory[i]!.newScore > sortedHistory[best]!.newScore) best = i;
    }
    return best;
  }, [sortedHistory]);

  /* v8 ignore start — responsive row height + virtualizer config */
  const ROW_HEIGHT = isMobile ? 44 : 52;
  const ROW_GAP = Gap.sm;
  const staggerDoneRef = useRef(false);
  const maxStagger = useMemo(() => estimateVisibleCount(ROW_HEIGHT), [ROW_HEIGHT]);
  const virtualizer = useVirtualizer({
    count: loadPhase === 'contentIn' ? sortedHistory.length : 0,
    getScrollElement: () => scrollRef.current,
    estimateSize: () => ROW_HEIGHT + ROW_GAP,
    overscan: 10,
  });
  /* v8 ignore stop */

  if (!songId || !instrument) {
    return <div className={s.center}>{t('history.notFound')}</div>;
  }

  return (
    <div className={s.page}>
        <SongInfoHeader
          song={song}
          songId={songId!}
          collapsed={!!(hasFab || headerCollapsed)}
          instrument={instKey}
          animate={!hasFab}
          /* v8 ignore start — platform-conditional sort button */
          actions={!hasFab && !IS_IOS && !IS_ANDROID && !IS_PWA ? (
            <ActionPill
              icon={<IoSwapVerticalSharp size={Size.iconAction} />}
              label={t('common.sort')}
              onClick={openSort}
              active={sortMode !== 'score' || sortAscending}
            />
          ) : undefined}
          /* v8 ignore stop */
        />

      <div ref={scrollRef} onScroll={handleScroll} className={s.scrollArea}>
        <div className={s.container}>

        {error && <div className={s.centerError}>{error}</div>}

        {!error && !player && !loading && (
          <div className={s.center}>{t('history.selectPlayer')}</div>
        )}

        {!error && player && (
          <>
            {loadPhase !== 'contentIn' && (
              <div
                className={`${s.spinnerContainer}${loadPhase === 'spinnerOut' ? ` ${s.spinnerOut}` : ''}`}
              >
                <ArcSpinner />
              </div>
            )}
            {/* v8 ignore start — virtual list rendering */}
            {loadPhase === 'contentIn' && (
            <div key={staggerKey} className={`${s.list}${hasFab ? ` ${s.listFab}` : ''}`} style={{ height: virtualizer.getTotalSize() }}>
              {virtualizer.getVirtualItems().map((virtualRow) => {
                const i = virtualRow.index;
                const h = sortedHistory[i]!;
                const delay = staggerDoneRef.current ? null : staggerDelay(i, 125, maxStagger);
                const staggerStyle: React.CSSProperties | undefined = delay != null
                  ? { opacity: 0, animation: `fadeInUp 400ms ease-out ${delay}ms forwards` }
                  : undefined;
                const dateStr = new Date(h.scoreAchievedAt ?? h.changedAt)
                  .toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
                const isHighScore = i === highScoreIndex;
                const rowClass = `${isHighScore ? s.rowHighlight : s.row}${isMobile ? ` ${s.rowMobile}` : ''}`;
                return (
                <div
                  key={`${h.changedAt}-${h.newScore}`}
                  ref={virtualizer.measureElement}
                  data-index={virtualRow.index}
                  className={s.virtualRow}
                  style={{ transform: `translateY(${virtualRow.start}px)` }}
                >
                <div
                  className={rowClass}
                  style={staggerStyle}
                  onAnimationEnd={(ev) => {
                    /* v8 ignore start — animation cleanup */
                    ev.currentTarget.style.opacity = '';
                    ev.currentTarget.style.animation = '';
                    if (i >= maxStagger - 1) staggerDoneRef.current = true;
                    /* v8 ignore stop */
                  }}
                >
                  <LeaderboardEntry
                    label={dateStr}
                    displayName={dateStr}
                    score={h.newScore}
                    season={h.season}
                    accuracy={h.accuracy}
                    isFullCombo={!!h.isFullCombo}
                    isPlayer={isHighScore}
                    showSeason={showSeason}
                    showAccuracy={showAccuracy}
                    scoreWidth={scoreWidth}
                  />
                </div>
                </div>
                );
              })}
              {sortedHistory.length === 0 && (
                <div className={s.emptyRow}>{t('history.noHistoryForInstrument')}</div>
              )}
            </div>
            )}            {/* v8 ignore stop */}          </>
        )}
      </div>
      </div>

      <PlayerScoreSortModal
        visible={sortModal.visible}
        draft={sortModal.draft}
        savedDraft={{ sortMode, sortAscending }}
        onChange={sortModal.setDraft}
        onCancel={sortModal.close}
        onReset={sortModal.reset}
        onApply={applySort}
      />
      {firstRun.show && <FirstRunCarousel slides={firstRun.slides} onDismiss={firstRun.dismiss} onExitComplete={firstRun.onExitComplete} />}
    </div>
  );
}

