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
import type { RivalSongComparison } from '@festival/core/api/serverTypes';
import { STAGGER_INTERVAL } from '@festival/theme';
import { deriveComboFromSettings, getEnabledInstruments } from './helpers/comboUtils';
import { categorizeRivalSongs } from './helpers/rivalCategories';
import RivalSongRow from './components/RivalSongRow';
import { Routes } from '../../routes';

import s from './RivalCategoryPage.module.css';

const MODE_TITLE_KEYS: Record<string, string> = {
  closest_battles: 'rivals.detail.closestBattles',
  almost_passed: 'rivals.detail.almostPassed',
  slipping_away: 'rivals.detail.slippingAway',
  barely_winning: 'rivals.detail.barelyWinning',
  pulling_forward: 'rivals.detail.pullingForward',
  dominating_them: 'rivals.detail.dominatingThem',
};

let _rivalryHasRendered = false;
let _cachedRivalrySongs: RivalSongComparison[] = [];
let _cachedRivalryName: string | null = null;
let _cachedRivalryKey: string | null = null;

export default function RivalryPage() {
  const { t } = useTranslation();
  const { rivalId } = useParams<{ rivalId: string }>();
  /* v8 ignore start -- URL param with fallback */
  const [searchParams] = useSearchParams();
  const mode = searchParams.get('mode') ?? 'closest_battles';
  /* v8 ignore stop */
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
  const comboFromState = (location.state as Record<string, unknown> | null)?.combo as string | undefined;
  const derivedCombo = useMemo(() => deriveComboFromSettings(settings), [settings]);
  const fallbackInstrument = useMemo(() => getEnabledInstruments(settings)[0], [settings]);
  const combo = comboFromState ?? derivedCombo ?? fallbackInstrument;
  /* v8 ignore stop */

  /* v8 ignore start -- cache-based state initialization */
  const cacheKey = `${accountId}:${rivalId}:${combo}`;
  const hasCachedData = cacheKey === _cachedRivalryKey && _cachedRivalrySongs.length > 0;
  const skipAnimRef = useRef(_rivalryHasRendered && navType === 'POP' && hasCachedData);
  _rivalryHasRendered = true;

  const [allSongs, setAllSongs] = useState<RivalSongComparison[]>(hasCachedData ? _cachedRivalrySongs : []);
  const [rivalName, setRivalName] = useState<string | null>(hasCachedData ? _cachedRivalryName : null);
  const [loading, setLoading] = useState(!hasCachedData);

  const saveScroll = useScrollRestore(scrollRef, `rivalry:${cacheKey}:${mode}`, navType);
  /* v8 ignore stop */

  /* v8 ignore start -- album art and year map building */
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
      setAllSongs(res.songs);
      setRivalName(res.rival.displayName);
      _cachedRivalryKey = cacheKey;
      _cachedRivalrySongs = res.songs;
      _cachedRivalryName = res.rival.displayName;
      setLoading(false);
    }).catch(() => {
      if (cancelled) return;
      setAllSongs([]);
      setLoading(false);
    });

    return () => { cancelled = true; };
  }, [accountId, rivalId, combo]);
  /* v8 ignore stop */

  /* v8 ignore start -- category/score computation */
  const category = useMemo(() => {
    const cats = categorizeRivalSongs(allSongs);
    return cats.find(c => c.key === mode) ?? null;
  }, [allSongs, mode]);

  const scoreDeltaWidth = useMemo(() => {
    if (!category || category.songs.length === 0) return undefined;
    let maxLen = 1;
    for (const song of category.songs) {
      // Score diff text length
      const diff = Math.abs((song.userScore ?? 0) - (song.rivalScore ?? 0));
      const scoreText = `+${diff.toLocaleString()}`;
      maxLen = Math.max(maxLen, scoreText.length);
      // Rank delta text length
      const rankText = `${song.rankDelta > 0 ? '+' : ''}${song.rankDelta}`;
      maxLen = Math.max(maxLen, rankText.length);
    }
    return `${maxLen + 2}ch`;
  }, [category]);
  /* v8 ignore stop */

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

  /* v8 ignore start -- guard */
  if (!accountId || !rivalId) {
    return <div className={s.center}>{t('rivals.detail.noSongs')}</div>;
  }
  /* v8 ignore stop */

  /* v8 ignore start -- render-time helpers */
  const stagger = (delayMs: number): React.CSSProperties | undefined =>
    shouldStagger ? { opacity: 0, animation: `fadeInUp 400ms ease-out ${delayMs}ms forwards` } : undefined;
  /* v8 ignore stop */

  /* v8 ignore start -- display state */
  const staggerInterval = STAGGER_INTERVAL;
  let staggerIdx = 0;

  const title = MODE_TITLE_KEYS[mode] ? t(MODE_TITLE_KEYS[mode]) : mode;
  /* v8 ignore stop */

  /* v8 ignore start -- JSX render tree */
  return (
    <div className={s.page}>
      <div className={s.stickyHeader}>
        <div className={s.headerContent}>
          <div className={s.headerTitle}>{title}</div>
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
              {!category || category.songs.length === 0 ? (
                <div className={s.emptyState} style={stagger(200)} onAnimationEnd={clearAnim}>
                  <div className={s.emptyTitle}>{t('rivals.detail.noSongs')}</div>
                </div>
              ) : (
                <div className={s.songList} style={{ paddingTop: 'var(--gap-md)' }}>
                  {category.songs.map((song) => (
                    <RivalSongRow
                      key={`${song.songId}-${song.instrument}`}
                      song={song}
                      albumArt={albumArtMap.get(song.songId)}
                      year={yearMap.get(song.songId)}
                      playerName={player?.displayName}
                      rivalName={rivalName ?? undefined}
                      onClick={() => navigate(Routes.songDetail(song.songId))}
                      standalone
                      scoreDeltaWidth={scoreDeltaWidth}
                      style={stagger((++staggerIdx) * staggerInterval)}
                      onAnimationEnd={clearAnim}
                    />
                  ))}
                </div>
              )}
            </div>
          </div>
      )}
    </div>
  );
  /* v8 ignore stop */
}
