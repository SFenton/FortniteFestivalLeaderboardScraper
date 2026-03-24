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
import ArcSpinner from '../../components/common/ArcSpinner';
import { IoChevronForward } from 'react-icons/io5';
import type { RivalSongComparison } from '@festival/core/api/serverTypes';
import { categorizeRivalSongs } from './helpers/rivalCategories';
import { deriveComboFromSettings, getEnabledInstruments } from './helpers/comboUtils';
import RivalSongRow from './components/RivalSongRow';
import { Routes } from '../../routes';
import s from './RivalDetailPage.module.css';

const SENTIMENT_CLASS: Record<string, string | undefined> = {
  positive: s.cardTitlePositive,
  negative: s.cardTitleNegative,
  neutral: s.cardTitleNeutral,
};

export default function RivalDetailPage() {
  const { t } = useTranslation();
  const { accountId, rivalId } = useParams<{ accountId: string; rivalId: string }>();
  const location = useLocation();
  const navigate = useNavigate();
  const { settings } = useSettings();
  const { state: { songs } } = useFestival();
  const isMobile = useIsMobile();
  const scrollRef = useRef<HTMLDivElement>(null);

  // Get combo from navigation state (passed from RivalsPage) or derive from settings
  const comboFromState = (location.state as Record<string, unknown> | null)?.combo as string | undefined;
  const derivedCombo = useMemo(() => deriveComboFromSettings(settings), [settings]);
  // Fallback: if no combo passed, try the first enabled instrument
  const fallbackInstrument = useMemo(() => getEnabledInstruments(settings)[0], [settings]);
  const combo = comboFromState ?? derivedCombo ?? fallbackInstrument;

  const [songs_, setSongs] = useState<RivalSongComparison[]>([]);
  const [rivalName, setRivalName] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

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

  /* v8 ignore start — async data fetch */
  useEffect(() => {
    if (!accountId || !rivalId || !combo) return;
    let cancelled = false;
    setLoading(true);

    api.getRivalDetail(accountId, combo, rivalId).then(res => {
      if (cancelled) return;
      setSongs(res.songs);
      setRivalName(res.rival.displayName);
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

  // Compute summary stats
  const totalSongs = songs_.length;
  const aheadCount = songs_.filter(s => s.rankDelta > 0).length;
  const behindCount = songs_.filter(s => s.rankDelta < 0).length;

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

  if (!accountId || !rivalId) {
    return <div className={s.center}>{t('rivals.detail.noSongs')}</div>;
  }

  const PREVIEW_COUNT = 5;

  const stagger = (delayMs: number): React.CSSProperties => ({
    opacity: 0,
    animation: `fadeInUp 400ms ease-out ${delayMs}ms forwards`,
  });

  const displayName = rivalName ?? 'Unknown Player';

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
              <div className={s.headerTitle}>
                {t('rivals.detail.vs', { name: displayName })}
              </div>
              <div className={s.headerSubtitle}>
                {t('rivals.detail.summary', {
                  total: totalSongs,
                  ahead: aheadCount,
                  behind: behindCount,
                })}
              </div>
            </div>
          </div>
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
                  navigate(Routes.rivalCategory(accountId, rivalId, cat.key), { state: { combo } });
                return (
                  <div
                    key={cat.key}
                    className={s.card}
                    style={stagger(200 + catIdx * 150)}
                    onAnimationEnd={clearAnim}
                  >
                    <div
                      className={s.cardHeaderClickable}
                      onClick={navigateToCategory}
                      role="button"
                      tabIndex={0}
                      onKeyDown={e => { if (e.key === 'Enter') navigateToCategory(); }}
                    >
                      <div>
                        <span className={SENTIMENT_CLASS[cat.sentiment] ?? s.cardTitle}>
                          {t(cat.titleKey)}
                        </span>
                        <span className={s.cardDesc}>
                          {t(cat.descriptionKey)} · {cat.songs.length} songs
                        </span>
                      </div>
                      <IoChevronForward size={20} className={s.chevron} />
                    </div>
                    <div className={s.songList}>
                      {preview.map(song => (
                        <RivalSongRow
                          key={`${song.songId}-${song.instrument}`}
                          song={song}
                          albumArt={albumArtMap.get(song.songId)}
                          year={yearMap.get(song.songId)}
                          onClick={() => navigate(Routes.songDetail(song.songId))}
                        />
                      ))}
                    </div>
                  </div>
                );
              })}
            </div>
          </div>
        </>
      )}
    </div>
  );
}
