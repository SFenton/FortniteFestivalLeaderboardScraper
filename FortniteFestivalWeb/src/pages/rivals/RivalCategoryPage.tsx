/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useEffect, useState, useCallback, useRef, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams, useLocation, useNavigate } from 'react-router-dom';
import { api } from '../../api/client';
import { useFestival } from '../../contexts/FestivalContext';
import { useSettings } from '../../contexts/SettingsContext';
import { useScrollMask } from '../../hooks/ui/useScrollMask';
import { useStaggerRush } from '../../hooks/ui/useStaggerRush';
import { useLoadPhase } from '../../hooks/data/useLoadPhase';
import { useIsMobile } from '../../hooks/ui/useIsMobile';
import { useTrackedPlayer } from '../../hooks/data/useTrackedPlayer';
import ArcSpinner from '../../components/common/ArcSpinner';
import type { RivalSongComparison } from '@festival/core/api/serverTypes';
import { categorizeRivalSongs } from './helpers/rivalCategories';
import { deriveComboFromSettings, getEnabledInstruments } from './helpers/comboUtils';
import RivalSongRow from './components/RivalSongRow';
import { Routes } from '../../routes';
import s from './RivalCategoryPage.module.css';

export default function RivalCategoryPage() {
  const { t } = useTranslation();
  const { accountId, rivalId, categoryKey } = useParams<{
    accountId: string;
    rivalId: string;
    categoryKey: string;
  }>();
  const location = useLocation();
  const navigate = useNavigate();
  const { settings } = useSettings();
  const { state: { songs } } = useFestival();
  const isMobile = useIsMobile();
  const { player } = useTrackedPlayer();
  const scrollRef = useRef<HTMLDivElement>(null);

  const comboFromState = (location.state as Record<string, unknown> | null)?.combo as string | undefined;
  const derivedCombo = useMemo(() => deriveComboFromSettings(settings), [settings]);
  const fallbackInstrument = useMemo(() => getEnabledInstruments(settings)[0], [settings]);
  const combo = comboFromState ?? derivedCombo ?? fallbackInstrument;

  const [allSongs, setAllSongs] = useState<RivalSongComparison[]>([]);
  const [rivalName, setRivalName] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

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

  /* v8 ignore start — async data fetch */
  useEffect(() => {
    if (!accountId || !rivalId || !combo) return;
    let cancelled = false;
    setLoading(true);

    api.getRivalDetail(accountId, combo, rivalId).then(res => {
      if (cancelled) return;
      setAllSongs(res.songs);
      setRivalName(res.rival.displayName);
      setLoading(false);
    }).catch(() => {
      if (cancelled) return;
      setAllSongs([]);
      setLoading(false);
    });

    return () => { cancelled = true; };
  }, [accountId, rivalId, combo]);
  /* v8 ignore stop */

  // Find the matching category from the full categorisation
  const category = useMemo(() => {
    const cats = categorizeRivalSongs(allSongs);
    return cats.find(c => c.key === categoryKey) ?? null;
  }, [allSongs, categoryKey]);

  // Pre-compute score diff pill width for consistent sizing across all rows
  const scoreDeltaWidth = useMemo(() => {
    if (!category || category.songs.length === 0) return undefined;
    let maxLen = 1;
    for (const song of category.songs) {
      const diff = Math.abs((song.userScore ?? 0) - (song.rivalScore ?? 0));
      const formatted = `+${diff.toLocaleString()}`;
      maxLen = Math.max(maxLen, formatted.length);
    }
    return `${maxLen + 2}ch`;
  }, [category]);

  const { phase } = useLoadPhase(!loading);
  const updateScrollMask = useScrollMask(scrollRef, [phase]);
  const { rushOnScroll } = useStaggerRush(scrollRef);

  /* v8 ignore start — scroll handler */
  const handleScroll = useCallback(() => {
    updateScrollMask();
    rushOnScroll();
  }, [updateScrollMask, rushOnScroll]);
  /* v8 ignore stop */

  const clearAnim = useCallback((e: React.AnimationEvent<HTMLElement>) => {
    const el = e.currentTarget;
    el.style.opacity = '';
    el.style.animation = '';
  }, []);

  if (!accountId || !rivalId || !categoryKey) {
    return <div className={s.center}>{t('rivals.detail.noSongs')}</div>;
  }

  const stagger = (delayMs: number): React.CSSProperties => ({
    opacity: 0,
    animation: `fadeInUp 400ms ease-out ${delayMs}ms forwards`,
  });

  const displayName = rivalName ?? 'Unknown Player';
  const title = category ? t(category.titleKey) : categoryKey;
  const desc = category ? t(category.descriptionKey) : '';

  return (
    <div className={s.page}>
      {phase !== 'contentIn' && (
        <div
          className={s.spinnerOverlay}
          style={phase === 'spinnerOut' ? { animation: 'fadeOut 500ms ease-out forwards' } : undefined}
        >
          <ArcSpinner />
        </div>
      )}
      {phase === 'contentIn' && (
        <>
          <div className={s.stickyHeader}>
            <div className={s.headerContent} style={stagger(100)} onAnimationEnd={clearAnim}>
              <div className={s.headerTitle}>{title}</div>
              <div className={s.headerSubtitle}>
                {t('rivals.detail.vs', { name: displayName })}
                {category ? ` · ${category.songs.length} songs` : ''}
              </div>
              {desc && <div className={s.headerSubtitle}>{desc}</div>}
            </div>
          </div>
          <div ref={scrollRef} onScroll={handleScroll} className={s.scrollArea}>
            <div className={s.container} style={isMobile ? { paddingBottom: 96 } : undefined}>
              {!category || category.songs.length === 0 ? (
                <div className={s.emptyState} style={stagger(200)} onAnimationEnd={clearAnim}>
                  <div className={s.emptyTitle}>{t('rivals.detail.noSongs')}</div>
                </div>
              ) : (
                <div className={s.songList}>
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
                    />
                  ))}
                </div>
              )}
            </div>
          </div>
        </>
      )}
    </div>
  );
}
