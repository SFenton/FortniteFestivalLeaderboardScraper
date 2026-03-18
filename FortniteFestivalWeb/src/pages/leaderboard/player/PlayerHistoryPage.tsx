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
import SeasonPill from '../../../components/songs/metadata/SeasonPill';
import AccuracyDisplay from '../../../components/songs/metadata/AccuracyDisplay';
import PlayerScoreSortModal from './modals/PlayerScoreSortModal';
import type { PlayerScoreSortMode, PlayerScoreSortDraft } from './modals/PlayerScoreSortModal';
import { Gap, QUERY_SHOW_ACCURACY, QUERY_SHOW_SEASON } from '@festival/theme';
import ArcSpinner from '../../../components/common/ArcSpinner';
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
  const applySort = () => {
    setSortMode(sortModal.draft.sortMode);
    setSortAscending(sortModal.draft.sortAscending);
    sortModal.close();
    setStaggerKey(k => k + 1);
  };

  // Register sort action for FAB
  const fabSearch = useFabSearch();
  const openSortRef = useRef(openSort);
  openSortRef.current = openSort;
  useEffect(() => {
    fabSearch.registerPlayerHistoryActions({ openSort: () => openSortRef.current() });
  }, [fabSearch]);

  const rushOnScroll = useStaggerRush(scrollRef);
  const handleScroll = useCallback(() => {
    saveScroll();
    updateScrollMask();
    rushOnScroll();
    updateHeaderCollapse();
  }, [saveScroll, updateScrollMask, rushOnScroll, updateHeaderCollapse]);

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
  }, [player, songId, instKey]);

  if (!songId || !instrument) {
    return <div className={s.center}>{t('history.notFound')}</div>;
  }

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

  const ROW_HEIGHT = isMobile ? 44 : 52;
  const virtualizer = useVirtualizer({
    count: loadPhase === 'contentIn' ? sortedHistory.length : 0,
    getScrollElement: () => scrollRef.current,
    estimateSize: () => ROW_HEIGHT,
    overscan: 10,
  });

  return (
    <div className={s.page}>
        <SongInfoHeader
          song={song}
          songId={songId!}
          collapsed={!!(hasFab || headerCollapsed)}
          instrument={instKey}
          animate={!hasFab}
          actions={!hasFab && !IS_IOS && !IS_ANDROID && !IS_PWA ? (
            <button className={s.sortBtn} onClick={openSort} title={t('common.sort')} aria-label={t('common.sortPlayerScores')}>
              <IoSwapVerticalSharp size={18} />
            </button>
          ) : undefined}
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
                className={s.spinnerContainer}
                style={loadPhase === 'spinnerOut'
                    ? { animation: 'fadeOut 500ms ease-out forwards' }
                    : undefined}
              >
                <ArcSpinner />
              </div>
            )}
            {loadPhase === 'contentIn' && (
            <div key={staggerKey} className={s.list} style={{ height: virtualizer.getTotalSize(), position: 'relative', ...(hasFab ? { paddingBottom: 96 } : {}) }}>
              {virtualizer.getVirtualItems().map((virtualRow) => {
                const i = virtualRow.index;
                const h = sortedHistory[i]!;
                const delay = staggerDelay(i, 125, estimateVisibleCount(ROW_HEIGHT));
                const staggerStyle: React.CSSProperties | undefined = delay != null
                  ? { opacity: 0, animation: `fadeInUp 400ms ease-out ${delay}ms forwards` }
                  : undefined;
                const dateStr = new Date(h.scoreAchievedAt ?? h.changedAt)
                  .toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
                const isHighScore = i === highScoreIndex;
                const rowClass = isHighScore ? s.rowHighlight : s.row;
                const mobileStyle: React.CSSProperties | undefined = isMobile ? { gap: Gap.md, padding: `0 ${Gap.md}px`, height: 40 } : undefined;
                return (
                <div
                  key={`${h.changedAt}-${h.newScore}`}
                  ref={virtualizer.measureElement}
                  data-index={virtualRow.index}
                  style={{ position: 'absolute', top: 0, left: 0, width: '100%', transform: `translateY(${virtualRow.start}px)` }}
                >
                <div
                  className={rowClass}
                  style={{ ...mobileStyle, ...staggerStyle }}
                  onAnimationEnd={(ev) => {
                    /* v8 ignore start — animation cleanup */
                    ev.currentTarget.style.opacity = '';
                    ev.currentTarget.style.animation = '';
                    /* v8 ignore stop */
                  }}
                >
                  <span className={s.colName} style={isHighScore ? { fontWeight: 700 } : undefined}>{dateStr}</span>
                  <span className={s.seasonScoreGroup}>
                    {showSeason && h.season != null && (
                      <SeasonPill season={h.season} />
                    )}
                    <span className={s.colScore} style={{ width: scoreWidth }}>
                      {h.newScore.toLocaleString()}
                    </span>
                  </span>
                  {showAccuracy && (
                  <span className={s.colAcc}>
                    <AccuracyDisplay
                      accuracy={h.accuracy}
                      isFullCombo={!!h.isFullCombo}
                    />
                  </span>
                  )}
                </div>
                </div>
                );
              })}
              {sortedHistory.length === 0 && (
                <div className={s.emptyRow}>{t('history.noHistoryForInstrument')}</div>
              )}
            </div>
            )}
          </>
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
    </div>
  );
}

