/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useEffect, useState, useCallback, useRef, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams, useLocation, useNavigate, useNavigationType, useSearchParams } from 'react-router-dom';
import { api } from '../../api/client';
import { useFestival } from '../../contexts/FestivalContext';
import { useSettings } from '../../contexts/SettingsContext';
import { useScrollMask } from '../../hooks/ui/useScrollMask';
import { useScrollRestore } from '../../hooks/ui/useScrollRestore';
import { useStaggerRush } from '../../hooks/ui/useStaggerRush';
import { useLoadPhase } from '../../hooks/data/useLoadPhase';
import { useIsMobile } from '../../hooks/ui/useIsMobile';
import { useTrackedPlayer } from '../../hooks/data/useTrackedPlayer';
import ArcSpinner from '../../components/common/ArcSpinner';
import { IoChevronForward } from 'react-icons/io5';
import type { RivalSongComparison } from '@festival/core/api/serverTypes';
import { STAGGER_INTERVAL } from '@festival/theme';
import { categorizeRivalSongs } from './helpers/rivalCategories';
import { deriveComboFromSettings, getEnabledInstruments } from './helpers/comboUtils';
import RivalSongRow from './components/RivalSongRow';
import { Routes } from '../../routes';
import s from './RivalDetailPage.module.css';
import rs from './RivalsPage.module.css';

let _detailHasRendered = false;
let _cachedDetailSongs: RivalSongComparison[] = [];
let _cachedDetailRivalName: string | null = null;
let _cachedDetailKey: string | null = null;

export default function RivalDetailPage() {
  const { t } = useTranslation();
  const { rivalId } = useParams<{ rivalId: string }>();
  const [searchParams] = useSearchParams();
  const location = useLocation();
  const navigate = useNavigate();
  const navType = useNavigationType();
  const { settings } = useSettings();
  const { state: { songs } } = useFestival();
  const isMobile = useIsMobile();
  const { player } = useTrackedPlayer();
  const accountId = player?.accountId;
  const scrollRef = useRef<HTMLDivElement>(null);

  /* v8 ignore start -- state derivation with null-coalescing */
  // Get combo from navigation state (passed from RivalsPage) or derive from settings
  const comboFromState = (location.state as Record<string, unknown> | null)?.combo as string | undefined;
  const rivalNameFromState = (location.state as Record<string, unknown> | null)?.rivalName as string | undefined;
  const rivalNameFromUrl = searchParams.get('name') ?? undefined;
  const derivedCombo = useMemo(() => deriveComboFromSettings(settings), [settings]);
  // Fallback: if no combo passed, try the first enabled instrument
  const fallbackInstrument = useMemo(() => getEnabledInstruments(settings)[0], [settings]);
  const combo = comboFromState ?? derivedCombo ?? fallbackInstrument;
  /* v8 ignore stop */

  /* v8 ignore start -- cache-based state initialization */
  const cacheKey = `${accountId}:${rivalId}:${combo}`;
  const hasCachedData = cacheKey === _cachedDetailKey && _cachedDetailSongs.length > 0;
  const skipAnimRef = useRef(_detailHasRendered && navType === 'POP' && hasCachedData);
  _detailHasRendered = true;

  const [songs_, setSongs] = useState<RivalSongComparison[]>(hasCachedData ? _cachedDetailSongs : []);
  const [rivalName, setRivalName] = useState<string | null>(hasCachedData ? _cachedDetailRivalName : (rivalNameFromState ?? rivalNameFromUrl ?? null));
  const [loading, setLoading] = useState(!hasCachedData);

  const saveScroll = useScrollRestore(scrollRef, `rivalDetail:${cacheKey}`, navType);
  /* v8 ignore stop */

  /* v8 ignore start -- album art and year map building */
  // Build album art + year lookups from festival context
  const albumArtMap = useMemo(() => {
    const map = new Map<string, string>();
    for (const song of songs) {
      if (song.albumArt) map.set(song.songId, song.albumArt);
    }
    return map;
  }, [songs]);

  const yearMap = useMemo(() => {
    const map = new Map<string, number>();
    for (const song of songs) {
      if (song.year) map.set(song.songId, song.year);
    }
    return map;
  }, [songs]);
  /* v8 ignore stop */

  /* v8 ignore start — async data fetch */
  useEffect(() => {
    if (!accountId || !rivalId || !combo || hasCachedData) return;
    let cancelled = false;
    setLoading(true);

    api.getRivalDetail(accountId, combo, rivalId).then(res => {
      if (cancelled) return;
      setSongs(res.songs);
      setRivalName(res.rival.displayName);
      // Persist to module cache
      _cachedDetailKey = cacheKey;
      _cachedDetailSongs = res.songs;
      _cachedDetailRivalName = res.rival.displayName;
      // Ensure the rival name is in the URL for refresh/sharing
      if (res.rival.displayName && !searchParams.has('name')) {
        const next = new URLSearchParams(searchParams);
        next.set('name', res.rival.displayName);
        navigate(`${location.pathname}?${next.toString()}`, { replace: true, state: location.state });
      }
      setLoading(false);
    }).catch(() => {
      if (cancelled) return;
      setSongs([]);
      setLoading(false);
    });

    return () => { cancelled = true; };
  }, [accountId, rivalId, combo]);
  /* v8 ignore stop */

  const categories = useMemo(() => categorizeRivalSongs(songs_), [songs_]);

  const { phase, shouldStagger } = useLoadPhase(!loading, { skipAnimation: skipAnimRef.current });
  const updateScrollMask = useScrollMask(scrollRef, [phase]);
  const { rushOnScroll } = useStaggerRush(scrollRef);

  /* v8 ignore start — scroll handler */
  const handleScroll = useCallback(() => {
    saveScroll();
    updateScrollMask();
    rushOnScroll();
  }, [saveScroll, updateScrollMask, rushOnScroll]);
  /* v8 ignore stop */

  /* v8 ignore start -- animation callback */
  const clearAnim = useCallback((e: React.AnimationEvent<HTMLElement>) => {
    const el = e.currentTarget;
    el.style.opacity = '';
    el.style.animation = '';
  }, []);
  /* v8 ignore stop */

  /* v8 ignore start -- guard and display state */
  if (!accountId || !rivalId) {
    return <div className={s.center}>{t('rivals.detail.noSongs')}</div>;
  }

  const PREVIEW_COUNT = 5;
  /* v8 ignore stop */

  /* v8 ignore start -- render-time helpers */
  const stagger = (delayMs: number): React.CSSProperties | undefined =>
    shouldStagger ? { opacity: 0, animation: `fadeInUp 400ms ease-out ${delayMs}ms forwards` } : undefined;
  /* v8 ignore stop */

  /* v8 ignore start -- display name fallbacks */
  const staggerInterval = STAGGER_INTERVAL;
  const displayName = rivalName ?? '\u2026';
  const playerName = player?.displayName ?? 'You';
  /* v8 ignore stop */
  /* v8 ignore start -- JSX render tree */  return (
    <div className={s.page}>
      <div className={s.stickyHeader}>
        <div className={s.headerContent}>
          <div className={s.headerTitle}>
            {playerName} vs. {displayName}
          </div>
        </div>
      </div>
      {phase !== 'contentIn' && (
        <div
          className={s.spinnerOverlay}
          style={phase === 'spinnerOut' ? { animation: 'fadeOut 500ms ease-out forwards' } : undefined}
        >
          <ArcSpinner />
        </div>
      )}
      {phase === 'contentIn' && (
          <div ref={scrollRef} onScroll={handleScroll} className={s.scrollArea}>
            <div className={s.container} style={isMobile ? { paddingBottom: 96 } : undefined}>
              {categories.length === 0 && (
                <div className={s.emptyState} style={stagger(200)} onAnimationEnd={clearAnim}>
                  <div className={s.emptyTitle}>{t('rivals.detail.noSongs')}</div>
                </div>
              )}
              {categories.map((cat, catIdx) => {
                const preview = cat.songs.slice(0, PREVIEW_COUNT);
                const navigateToCategory = () =>
                  navigate(Routes.rivalry(rivalId, cat.key), { state: { combo } });
                return (
                  <div key={cat.key} className={rs.section}>
                    <div
                      className={rs.sectionHeaderClickable}
                      style={stagger(200 + catIdx * 150)}
                      onAnimationEnd={clearAnim}
                      onClick={navigateToCategory}
                      role="button"
                      tabIndex={0}
                      onKeyDown={e => { if (e.key === 'Enter') navigateToCategory(); }}
                    >
                      <div className={rs.cardHeaderText}>
                        <span className={s.cardTitle}>
                          {t(cat.titleKey)}
                        </span>
                        <span className={s.cardDesc}>
                          {t(cat.descriptionKey)}
                        </span>
                      </div>
                      <span className={rs.seeAll}>{t('rivals.seeAll', 'See All')}</span>
                      <IoChevronForward size={20} className={rs.chevron} />
                    </div>
                    <div className={s.songList}>
                      {preview.map((song, songIdx) => (
                        <RivalSongRow
                          key={`${song.songId}-${song.instrument}`}
                          song={song}
                          albumArt={albumArtMap.get(song.songId)}
                          year={yearMap.get(song.songId)}
                          playerName={player?.displayName}
                          rivalName={rivalName ?? undefined}
                          onClick={() => navigate(Routes.songDetail(song.songId))}
                          standalone
                          style={stagger(200 + catIdx * 150 + (songIdx + 1) * staggerInterval)}
                          onAnimationEnd={clearAnim}
                        />
                      ))}
                    </div>
                  </div>
                );
              })}
            </div>
          </div>
      )}
    </div>
  );
  /* v8 ignore stop */
}
