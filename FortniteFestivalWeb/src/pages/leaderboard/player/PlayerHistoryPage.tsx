/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useEffect, useRef, useState, useCallback, useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams } from 'react-router-dom';
import { IoSwapVerticalSharp } from 'react-icons/io5';
import { useVirtualizer } from '@tanstack/react-virtual';
import { useFestival } from '../../../contexts/FestivalContext';
import { useTrackedPlayer } from '../../../hooks/data/useTrackedPlayer';
import { useFabSearch } from '../../../contexts/FabSearchContext';
import { useModalState } from '../../../hooks/ui/useModalState';
import { useScrollContainer } from '../../../contexts/ScrollContainerContext';
import { clearScrollCache } from '../../../hooks/ui/useScrollRestore';
import { api } from '../../../api/client';
import {
  type ServerInstrumentKey as InstrumentKey,
  type ServerScoreHistoryEntry as ScoreHistoryEntry,
} from '@festival/core/api/serverTypes';
import SongInfoHeader from '../../../components/songs/headers/SongInfoHeader';
import { LeaderboardEntry } from '../global/components/LeaderboardEntry';
import PlayerScoreSortModal from './modals/PlayerScoreSortModal';
import type { PlayerScoreSortMode, PlayerScoreSortDraft } from './modals/PlayerScoreSortModal';
import { Gap, Size, QUERY_SHOW_ACCURACY, QUERY_SHOW_SEASON, Colors, Radius, Layout, MaxWidth, Font, Border, Overflow, Position, Display, Align, CssValue, CssProp, flexRow, flexColumn, flexCenter, frostedCard, padding, border, transition, SPINNER_FADE_MS, FADE_DURATION } from '@festival/theme';
import { buildStaggerStyle, clearStaggerStyle } from '../../../hooks/ui/useStaggerStyle';
import ArcSpinner from '../../../components/common/ArcSpinner';
import { ActionPill } from '../../../components/common/ActionPill';
import Page, { PageBackground } from '../../Page';
import { PageMessage } from '../../PageMessage';
import EmptyState from '../../../components/common/EmptyState';
import { parseApiError } from '../../../utils/apiError';
import { staggerDelay, estimateVisibleCount } from '@festival/ui-utils';
import { useIsMobile } from '../../../hooks/ui/useIsMobile';
import { useScoreFilter } from '../../../hooks/data/useScoreFilter';
import { useSortedScoreHistory } from '../../../hooks/data/useSortedScoreHistory';
import { PlayerScoreSortMode as CoreSortMode } from '@festival/core';
import { LoadPhase } from '@festival/core';
import { useMediaQuery } from '../../../hooks/ui/useMediaQuery';
import { useLoadPhase } from '../../../hooks/data/useLoadPhase';
import { IS_IOS, IS_ANDROID, IS_PWA } from '@festival/ui-utils';
import { playerHistorySlides } from './firstRun';
import { hasVisitedPage, markPageVisited } from '../../../hooks/ui/usePageTransition';

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

  const showAccuracy = useMediaQuery(QUERY_SHOW_ACCURACY);
  const showSeason = useMediaQuery(QUERY_SHOW_SEASON);
  const isMobile = !showAccuracy;
  const hasFab = useIsMobile();

  const historySlidesMemo = useMemo(() => playerHistorySlides(hasFab), [hasFab]);
  const firstRunGateCtx = useMemo(() => ({ hasPlayer: !!player }), [player]);

  const [history, setHistory] = useState<ScoreHistoryEntry[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const { filterHistory } = useScoreFilter();
  const [headerCollapsed, setHeaderCollapsed] = useState(hasFab);
  const historyKey = `history:${songId}:${instKey}`;
  const skipHistoryAnim = hasVisitedPage(historyKey);
  markPageVisited(historyKey);
  const { phase: loadPhase } = useLoadPhase(!loading && !error, { skipAnimation: skipHistoryAnim });

  const headerStagger: CSSProperties | undefined = hasFab || skipHistoryAnim
    ? undefined
    : loadPhase === LoadPhase.ContentIn
      ? { opacity: 0, animation: `fadeInUp ${FADE_DURATION}ms ease-out forwards` }
      : { opacity: 0 };

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
    scrollContainerRef.current?.scrollTo(0, 0);
    clearScrollCache(`history:${songId}:${instKey}`);
    // Defer stagger reset to the next frame so the programmatic scroll event
    // fires (and is ignored by rush) before new content mounts.
    requestAnimationFrame(() => {
      staggerDoneRef.current = false;
      resetRush();
      setStaggerKey(k => k + 1);
    });
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

  const staggerRushRef = useRef<(() => void) | undefined>(undefined);
  const resetRush = useCallback(() => staggerRushRef.current?.(), []);

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
  const staggerDoneRef = useRef(skipHistoryAnim);
  const listParentRef = useRef<HTMLDivElement>(null);
  const maxStagger = useMemo(() => estimateVisibleCount(ROW_HEIGHT), [ROW_HEIGHT]);
  const scrollContainerRef = useScrollContainer();
  const virtualizer = useVirtualizer({
    count: loadPhase === 'contentIn' ? sortedHistory.length : 0,
    estimateSize: () => ROW_HEIGHT + ROW_GAP,
    overscan: 10,
    getScrollElement: () => scrollContainerRef.current,
    scrollMargin: listParentRef.current?.offsetTop ?? 0,
  });
  /* v8 ignore stop */

  if (!songId || !instrument) {
    return <PageMessage>{t('history.notFound')}</PageMessage>;
  }

  return (
    <Page
      scrollRestoreKey={`history:${songId}:${instKey}`}
      scrollDeps={[loadPhase, history.length]}
      staggerRushRef={staggerRushRef}
      headerCollapse={{ disabled: hasFab, onCollapse: setHeaderCollapsed }}
      firstRun={{ key: 'playerhistory', label: t('history.title'), slides: historySlidesMemo, gateContext: firstRunGateCtx }}
      background={<PageBackground src={song?.albumArt} />}
      before={
        <div style={headerStagger} onAnimationEnd={clearStaggerStyle}>
          <SongInfoHeader
            song={song}
            songId={songId!}
            collapsed={!!(hasFab || headerCollapsed)}
            instrument={instKey}
            animate={!hasFab}
            hideBackground
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
        </div>
      }
      after={<>
        <PlayerScoreSortModal
          visible={sortModal.visible}
          draft={sortModal.draft}
          savedDraft={{ sortMode, sortAscending }}
          onChange={sortModal.setDraft}
          onCancel={sortModal.close}
          onReset={sortModal.reset}
          onApply={applySort}
        />
      </>}
    >

        {error && (() => { const parsed = parseApiError(error); return <EmptyState fullPage title={parsed.title} subtitle={parsed.subtitle} style={buildStaggerStyle(200)} onAnimationEnd={clearStaggerStyle} />; })()}

        {!error && !player && !loading && (
          <PageMessage>{t('history.selectPlayer')}</PageMessage>
        )}

        {!error && player && (
          <>
            {loadPhase !== LoadPhase.ContentIn && (
              <div
                style={{ ...histStyles.spinnerContainer, ...(loadPhase === LoadPhase.SpinnerOut ? histStyles.spinnerFadeOut : {}) }}
              >
                <ArcSpinner />
              </div>
            )}
            {/* v8 ignore start — virtual list rendering */}
            {loadPhase === LoadPhase.ContentIn && (
            <div key={staggerKey} ref={listParentRef} style={{ ...histStyles.list, ...(hasFab ? { paddingBottom: Layout.fabPaddingBottom } : {}), height: virtualizer.getTotalSize() }}>
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
                const rowStyle = {
                  ...histStyles.row,
                  ...(isHighScore ? histStyles.rowHighlight : {}),
                  ...(isMobile ? histStyles.rowMobile : {}),
                };
                return (
                <div
                  key={`${h.changedAt}-${h.newScore}`}
                  ref={virtualizer.measureElement}
                  data-index={virtualRow.index}
                  style={{ ...histStyles.virtualRow, transform: `translateY(${virtualRow.start - virtualizer.options.scrollMargin}px)` }}
                >
                <div
                  style={{ ...rowStyle, ...staggerStyle }}
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
                <div style={histStyles.emptyRow}>{t('history.noHistoryForInstrument')}</div>
              )}
            </div>
            )}            {/* v8 ignore stop */}          </>
        )}
    </Page>
  );
}

const histStyles = {
  spinnerContainer: {
    ...flexCenter,
    minHeight: 'calc(100vh - 350px)',
  } as CSSProperties,
  spinnerFadeOut: {
    animation: `fadeOut ${SPINNER_FADE_MS}ms ease-out forwards`,
  } as CSSProperties,
  list: {
    ...flexColumn,
    gap: Gap.sm,
    overflow: Overflow.hidden,
    position: Position.relative,
  } as CSSProperties,
  virtualRow: {
    position: Position.absolute,
    top: 0,
    left: 0,
    width: CssValue.full,
    paddingBottom: Gap.sm,
  } as CSSProperties,
  row: {
    ...frostedCard,
    ...flexRow,
    gap: Gap.xl,
    padding: padding(0, Gap.xl),
    height: Layout.entryRowHeight,
    borderRadius: Radius.md,
    color: CssValue.inherit,
    fontSize: Font.md,
    overflow: Overflow.hidden,
  } as CSSProperties,
  rowMobile: {
    gap: Gap.md,
    padding: padding(0, Gap.md),
  } as CSSProperties,
  rowHighlight: {
    backgroundColor: Colors.purpleHighlight,
    border: border(Border.thin, Colors.purpleHighlightBorder),
  } as CSSProperties,
  emptyRow: {
    padding: Gap.xl,
    textAlign: 'center' as const,
    color: Colors.textMuted,
  } as CSSProperties,
};

