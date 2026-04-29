/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import { LoadPhase } from '@festival/core';
import { DEFAULT_INSTRUMENT, type AccountSearchResult, type BandSearchResult, type PlayerBandEntry, type ServerSong } from '@festival/core/api/serverTypes';
import { staggerDelay } from '@festival/ui-utils';
import SearchBar, { type SearchBarRef } from '../common/SearchBar';
import ArcSpinner, { SpinnerSize } from '../common/ArcSpinner';
import ModalShell from '../modals/components/ModalShell';
import PlayerBandCard from '../../pages/player/components/PlayerBandCard';
import { SongRow } from '../../pages/songs/components/SongRow';
import { useUnifiedSearch } from '../../hooks/data/useUnifiedSearch';
import { useIsMobile } from '../../hooks/ui/useIsMobile';
import { useScrollMask } from '../../hooks/ui/useScrollMask';
import { useStaggerRush } from '../../hooks/ui/useStaggerRush';
import { Routes } from '../../routes';
import { SEARCH_TARGETS, type SearchTarget } from '../../types/search';
import {
  Align, Border, BoxSizing, Colors, Cursor, CssValue, Display, Font, Gap, Justify,
  LineHeight, Overflow, Radius, TextAlign, Weight, flexCenter, flexColumn,
  border, frostedCard, padding, Shadow, FADE_DURATION, SPINNER_FADE_MS, STAGGER_INTERVAL,
} from '@festival/theme';

const SEARCH_MODAL_DESKTOP: CSSProperties = { width: 520, height: 640, maxHeight: '90vh' };
const MODAL_TRANSITION_MS = 250;
const SEARCH_STAGGER_VISIBLE_ITEMS = 8;
const SEARCH_SCROLL_FADE_SIZE = 40;

const SEARCH_TARGET_LABEL_KEYS: Record<SearchTarget, string> = {
  songs: 'search.tabs.songs',
  players: 'search.tabs.players',
  bands: 'search.tabs.bands',
};

const EMPTY_INSTRUMENTS = [];
const EMPTY_METADATA_ORDER = [];

interface SearchModalProps {
  visible: boolean;
  onClose: () => void;
  defaultTarget: SearchTarget;
}


export default function SearchModal({ visible, onClose, defaultTarget }: SearchModalProps) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const isMobile = useIsMobile();
  const st = useStyles(isMobile);
  const inputRef = useRef<SearchBarRef>(null);
  const resultsRef = useRef<HTMLDivElement>(null);
  const wasVisibleRef = useRef(false);
  const [query, setQuery] = useState('');
  const [activeTarget, setActiveTarget] = useState<SearchTarget>(defaultTarget);
  const search = useUnifiedSearch(query);

  useEffect(() => {
    if (visible && !wasVisibleRef.current) {
      setActiveTarget(defaultTarget);
    }
    if (!visible) {
      setQuery('');
    }
    wasVisibleRef.current = visible;
  }, [defaultTarget, visible]);

  const handleOpenComplete = useCallback(() => {
    setTimeout(() => inputRef.current?.focus(), 50);
  }, []);

  const handleCloseComplete = useCallback(() => {
    setQuery('');
    setActiveTarget(defaultTarget);
  }, [defaultTarget]);

  const closeAndNavigate = useCallback((path: string) => {
    onClose();
    navigate(path);
  }, [navigate, onClose]);

  const handleSongSelect = useCallback((song: ServerSong) => {
    closeAndNavigate(Routes.songDetail(song.songId));
  }, [closeAndNavigate]);

  const handlePlayerSelect = useCallback((player: AccountSearchResult) => {
    closeAndNavigate(Routes.player(player.accountId));
  }, [closeAndNavigate]);

  const handleBandSelect = useCallback((band: BandSearchResult) => {
    const names = band.members.map(member => member.displayName ?? member.accountId).filter(Boolean).join(', ');
    closeAndNavigate(Routes.band(band.bandId, {
      bandType: band.bandType,
      teamKey: band.teamKey,
      names,
    }));
  }, [closeAndNavigate]);

  const handleTabKeyDown = useCallback((target: SearchTarget, e: React.KeyboardEvent<HTMLButtonElement>) => {
    if (e.key !== 'ArrowRight' && e.key !== 'ArrowLeft') return;
    e.preventDefault();
    const index = SEARCH_TARGETS.indexOf(target);
    const delta = e.key === 'ArrowRight' ? 1 : -1;
    const next = SEARCH_TARGETS[(index + delta + SEARCH_TARGETS.length) % SEARCH_TARGETS.length] ?? target;
    setActiveTarget(next);
  }, []);

  const tabs = (
    <div style={st.tabs} role="tablist" aria-label={t('search.targetTabs')}>
      {SEARCH_TARGETS.map(target => {
        const selected = activeTarget === target;
        return (
          <button
            key={target}
            type="button"
            role="tab"
            aria-selected={selected}
            aria-controls={`search-results-${target}`}
            style={selected ? st.tabSelected : st.tab}
            onClick={() => setActiveTarget(target)}
            onKeyDown={e => handleTabKeyDown(target, e)}
          >
            {t(SEARCH_TARGET_LABEL_KEYS[target])}
          </button>
        );
      })}
    </div>
  );

  return (
    <ModalShell
      visible={visible}
      title={t('search.title')}
      onClose={onClose}
      desktopStyle={SEARCH_MODAL_DESKTOP}
      transitionMs={MODAL_TRANSITION_MS}
      onOpenComplete={handleOpenComplete}
      onCloseComplete={handleCloseComplete}
    >
      <div style={st.body}>
        <SearchBar
          ref={inputRef}
          value={query}
          onChange={setQuery}
          placeholder={t('search.placeholder')}
          enterKeyHint="search"
          style={st.searchBar}
        />
        {!isMobile && tabs}
        <div ref={resultsRef} id={`search-results-${activeTarget}`} role="tabpanel" style={st.results} aria-live="polite">
          <SearchResultsPanel
            activeTarget={activeTarget}
            query={query}
            search={search}
            styles={st}
            resultsRef={resultsRef}
            isMobile={isMobile}
            t={t}
            onSongSelect={handleSongSelect}
            onPlayerSelect={handlePlayerSelect}
            onBandSelect={handleBandSelect}
          />
        </div>
        {isMobile && tabs}
      </div>
    </ModalShell>
  );
}

interface RenderResultsArgs {
  activeTarget: SearchTarget;
  query: string;
  search: ReturnType<typeof useUnifiedSearch>;
  styles: ReturnType<typeof useStyles>;
  resultsRef: React.RefObject<HTMLDivElement | null>;
  isMobile: boolean;
  t: ReturnType<typeof useTranslation>['t'];
  onSongSelect: (song: ServerSong) => void;
  onPlayerSelect: (player: AccountSearchResult) => void;
  onBandSelect: (band: BandSearchResult) => void;
}

function SearchResultsPanel(props: RenderResultsArgs) {
  const { activeTarget, query, search, styles: st, resultsRef } = props;
  const resultListRef = useRef<HTMLDivElement>(null);
  const trimmedQuery = query.trim();
  const isShort = trimmedQuery.length < 2;
  const loading = !isShort && (search.debouncing || search.loading[activeTarget]);
  const contentSignature = useMemo(
    () => getContentSignature(activeTarget, trimmedQuery, search),
    [activeTarget, search, trimmedQuery],
  );
  const [loadPhase, setLoadPhase] = useState<LoadPhase>(LoadPhase.ContentIn);
  const [staggerSignature, setStaggerSignature] = useState<string | null>(null);
  const loadPhaseRef = useRef(loadPhase);
  const staggeredSignaturesRef = useRef<Partial<Record<SearchTarget, string>>>({});
  const previousQueryRef = useRef(trimmedQuery);
  const updateScrollMask = useScrollMask(resultsRef, [activeTarget, contentSignature, loadPhase], { selfScroll: true, size: SEARCH_SCROLL_FADE_SIZE });
  const { resetRush } = useStaggerRush(resultListRef, resultsRef);
  loadPhaseRef.current = loadPhase;

  useEffect(() => {
    if (previousQueryRef.current === trimmedQuery) return;
    previousQueryRef.current = trimmedQuery;
    staggeredSignaturesRef.current = {};
    setStaggerSignature(null);
    resetRush();
  }, [resetRush, trimmedQuery]);

  useEffect(() => {
    resetRush();
  }, [contentSignature, resetRush]);

  useEffect(() => {
    updateScrollMask();
  }, [contentSignature, loadPhase, updateScrollMask]);

  useEffect(() => {
    if (isShort) {
      setStaggerSignature(null);
      setLoadPhase(LoadPhase.ContentIn);
      return undefined;
    }

    if (loading) {
      setStaggerSignature(null);
      setLoadPhase(LoadPhase.Loading);
      return undefined;
    }

    if (staggeredSignaturesRef.current[activeTarget] === contentSignature) {
      setStaggerSignature(null);
      setLoadPhase(LoadPhase.ContentIn);
      return undefined;
    }

    if (loadPhaseRef.current === LoadPhase.Loading || loadPhaseRef.current === LoadPhase.SpinnerOut) {
      setLoadPhase(LoadPhase.SpinnerOut);
      const id = setTimeout(() => {
        staggeredSignaturesRef.current[activeTarget] = contentSignature;
        setStaggerSignature(contentSignature);
        setLoadPhase(LoadPhase.ContentIn);
      }, SPINNER_FADE_MS);
      return () => clearTimeout(id);
    }

    staggeredSignaturesRef.current[activeTarget] = contentSignature;
    setStaggerSignature(contentSignature);
    setLoadPhase(LoadPhase.ContentIn);
    return undefined;
  }, [activeTarget, contentSignature, isShort, loading, search.debouncedQuery]);

  if (loadPhase !== LoadPhase.ContentIn && !isShort) {
    const spinnerStyle = loadPhase === LoadPhase.SpinnerOut ? st.spinnerWrapOut : st.spinnerWrapIn;
    return <div style={spinnerStyle}><ArcSpinner size={SpinnerSize.MD} /></div>;
  }

  return (
    <div ref={resultListRef} style={st.resultList}>
      {renderResults({ ...props, shouldStagger: staggerSignature === contentSignature && loadPhase === LoadPhase.ContentIn })}
    </div>
  );
}

function getContentSignature(target: SearchTarget, query: string, search: ReturnType<typeof useUnifiedSearch>): string {
  if (query.length < 2) return `${target}:short`;
  const debouncedQuery = search.debouncedQuery || query;
  if (search.errors[target]) return `${target}:${debouncedQuery}:error`;

  if (target === 'songs') {
    return `${target}:${debouncedQuery}:${search.songResults.map(song => song.songId).join('|')}`;
  }

  if (target === 'players') {
    return `${target}:${debouncedQuery}:${search.playerResults.map(player => player.accountId).join('|')}`;
  }

  return `${target}:${debouncedQuery}:${search.bandResults.map(band => `${band.bandId}:${band.teamKey}`).join('|')}`;
}

function getStaggerStyle(index: number, enabled: boolean): CSSProperties | undefined {
  if (!enabled) return undefined;
  const delay = staggerDelay(index, STAGGER_INTERVAL, SEARCH_STAGGER_VISIBLE_ITEMS);
  return delay == null ? undefined : { opacity: 0, animation: `fadeInUp ${FADE_DURATION}ms ease-out ${delay}ms forwards` };
}

function renderResults({ activeTarget, query, search, styles: st, isMobile, t, onSongSelect, onPlayerSelect, onBandSelect, shouldStagger }: RenderResultsArgs & { shouldStagger: boolean }) {
  const isShort = query.trim().length < 2;
  const hasError = search.errors[activeTarget];

  if (isShort) {
    return <div style={st.hintCenter}>{t('search.enterQuery')}</div>;
  }

  if (hasError) {
    return <div style={{ ...st.hintCenter, ...getStaggerStyle(0, shouldStagger) }}>{t('search.failed')}</div>;
  }

  if (activeTarget === 'songs') {
    if (search.songResults.length === 0) return <div style={{ ...st.hintCenter, ...getStaggerStyle(0, shouldStagger) }}>{t('search.noResults.songs')}</div>;
    return search.songResults.map((song, index) => (
      <div
        key={song.songId}
        style={{ ...st.songRowWrap, ...getStaggerStyle(index, shouldStagger) }}
        onClickCapture={(event) => {
          event.preventDefault();
          onSongSelect(song);
        }}
      >
        <SongRow
          song={song}
          instrument={DEFAULT_INSTRUMENT}
          instrumentFilter={null}
          showInstrumentIcons={false}
          enabledInstruments={EMPTY_INSTRUMENTS}
          metadataOrder={EMPTY_METADATA_ORDER}
          sortMode="title"
          isMobile={isMobile}
        />
      </div>
    ));
  }

  if (activeTarget === 'players') {
    if (search.playerResults.length === 0) return <div style={{ ...st.hintCenter, ...getStaggerStyle(0, shouldStagger) }}>{t('search.noResults.players')}</div>;
    return search.playerResults.map((player, index) => (
      <button key={player.accountId} type="button" style={{ ...st.resultBtn, ...getStaggerStyle(index, shouldStagger) }} onClick={() => onPlayerSelect(player)}>
        <span style={st.resultTitle}>{player.displayName}</span>
      </button>
    ));
  }

  if (search.bandResults.length === 0) return <div style={{ ...st.hintCenter, ...getStaggerStyle(0, shouldStagger) }}>{t('search.noResults.bands')}</div>;
  return search.bandResults.map((band, index) => {
    const members = band.members.map(member => member.displayName ?? member.accountId).filter(Boolean).join(' · ');
    const cardEntry = toPlayerBandEntry(band);
    return (
      <div
        key={band.bandId}
        style={{ ...st.bandCardWrap, ...getStaggerStyle(index, shouldStagger) }}
        onClickCapture={(event) => {
          event.preventDefault();
          onBandSelect(band);
        }}
      >
        <PlayerBandCard
          entry={cardEntry}
          ariaLabel={members || t('search.unknownBand')}
          appearanceLabel={t('bandList.appearanceLabel', { count: band.appearanceCount })}
        />
      </div>
    );
  });
}

function toPlayerBandEntry(band: BandSearchResult): PlayerBandEntry {
  return {
    bandId: band.bandId,
    teamKey: band.teamKey,
    bandType: band.bandType,
    appearanceCount: band.appearanceCount,
    members: band.members,
  };
}

function useStyles(isMobile: boolean) {
  return useMemo(() => {
    const selectorPad = padding(Gap.lg, Gap.md + 4);
    const tabBase: CSSProperties = {
      ...frostedCard,
      flex: 1,
      minHeight: 44,
      padding: selectorPad,
      borderRadius: Radius.md,
      color: Colors.textSecondary,
      fontSize: Font.md,
      fontWeight: Weight.semibold,
      cursor: Cursor.pointer,
      textAlign: TextAlign.center,
    };

    return {
      body: {
        flex: 1,
        ...flexColumn,
        padding: padding(Gap.sm, Gap.section, Gap.section),
        gap: Gap.md,
        overflow: Overflow.hidden,
      } as CSSProperties,
      searchBar: {
        display: Display.flex,
        alignItems: Align.center,
        gap: Gap.sm,
        height: 48,
        padding: padding(0, Gap.xl),
        boxSizing: BoxSizing.borderBox,
        borderRadius: Radius.full,
        border: `${Border.thin}px solid ${Colors.borderPrimary}`,
        backgroundColor: Colors.backgroundCard,
        cursor: Cursor.text,
        flexShrink: 0,
      } as CSSProperties,
      tabs: {
        display: Display.flex,
        gap: Gap.sm,
        flexShrink: 0,
      } as CSSProperties,
      tab: tabBase,
      tabSelected: {
        flex: 1,
        minHeight: 44,
        padding: selectorPad,
        borderRadius: Radius.md,
        fontSize: Font.md,
        fontWeight: Weight.semibold,
        cursor: Cursor.pointer,
        textAlign: TextAlign.center,
        color: Colors.textPrimary,
        backgroundColor: Colors.purpleHighlight,
        backgroundImage: CssValue.none,
        border: border(1, Colors.purpleHighlightBorder),
        boxShadow: Shadow.frostedActive,
      } as CSSProperties,
      results: {
        flex: 1,
        minHeight: 0,
        ...flexColumn,
        overflowY: Overflow.auto,
      } as CSSProperties,
      resultList: {
        ...flexColumn,
        gap: Gap.xs,
        flexShrink: 0,
        minHeight: CssValue.full,
        boxSizing: BoxSizing.borderBox,
        paddingTop: SEARCH_SCROLL_FADE_SIZE,
        paddingBottom: SEARCH_SCROLL_FADE_SIZE,
      } as CSSProperties,
      spinnerWrapIn: {
        ...flexCenter,
        flex: 1,
        animation: `fadeIn ${SPINNER_FADE_MS}ms ease-out forwards`,
      } as CSSProperties,
      spinnerWrapOut: {
        ...flexCenter,
        flex: 1,
        animation: `fadeOut ${SPINNER_FADE_MS}ms ease-out forwards`,
      } as CSSProperties,
      hintCenter: {
        ...flexCenter,
        flex: 1,
        color: Colors.textTertiary,
        fontSize: isMobile ? Font.md : Font.lg,
        lineHeight: LineHeight.relaxed,
        textAlign: TextAlign.center,
        padding: padding(Gap.section),
      } as CSSProperties,
      resultBtn: {
        ...frostedCard,
        width: CssValue.full,
        flexShrink: 0,
        minHeight: 54,
        ...flexColumn,
        justifyContent: Justify.center,
        gap: 0,
        padding: padding(Gap.md, Gap.section),
        borderRadius: Radius.md,
        color: Colors.textSecondary,
        cursor: Cursor.pointer,
        textAlign: TextAlign.left,
      } as CSSProperties,
      songRowWrap: {
        cursor: Cursor.pointer,
        flexShrink: 0,
      } as CSSProperties,
      bandCardWrap: {
        cursor: Cursor.pointer,
        flexShrink: 0,
      } as CSSProperties,
      resultTitle: {
        color: Colors.textPrimary,
        fontSize: Font.md,
        fontWeight: Weight.semibold,
      } as CSSProperties,
      resultSubtitle: {
        color: Colors.textMuted,
        fontSize: Font.sm,
        lineHeight: LineHeight.relaxed,
      } as CSSProperties,
    };
  }, [isMobile]);
}
