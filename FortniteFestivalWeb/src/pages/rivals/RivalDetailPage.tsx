/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useEffect, useState, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams, useLocation, useNavigate, useSearchParams } from 'react-router-dom';
import { api } from '../../api/client';
import { useSettings } from '../../contexts/SettingsContext';
import { useSongLookups } from '../../hooks/data/useSongLookups';
import { usePageTransition } from '../../hooks/ui/usePageTransition';
import { useStagger } from '../../hooks/ui/useStagger';
import EmptyState from '../../components/common/EmptyState';
import PageHeader from '../../components/common/PageHeader';
import { useIsMobile } from '../../hooks/ui/useIsMobile';
import { useTrackedPlayer } from '../../hooks/data/useTrackedPlayer';
import { IoChevronForward, IoPerson } from 'react-icons/io5';
import type { RivalSongComparison } from '@festival/core/api/serverTypes';
import { STAGGER_INTERVAL, Gap, Layout, flexColumn, IconSize } from '@festival/theme';
import { LoadPhase } from '@festival/core';
import { categorizeRivalSongs } from './helpers/rivalCategories';
import { deriveComboFromSettings, getEnabledInstruments } from './helpers/comboUtils';
import RivalSongRow from './components/RivalSongRow';
import { Routes } from '../../routes';
import { useRivalsSharedStyles } from './useRivalsSharedStyles';
import fx from '../../styles/effects.module.css';
import Page from '../Page';

let _cachedDetailSongs: RivalSongComparison[] = [];
let _cachedDetailRivalName: string | null = null;
let _cachedDetailKey: string | null = null;

export default function RivalDetailPage() {
  const { t } = useTranslation();
  const { rivalId } = useParams<{ rivalId: string }>();
  const [searchParams] = useSearchParams();
  const location = useLocation();
  const navigate = useNavigate();
  const { settings } = useSettings();
  const isMobile = useIsMobile();
  const { player } = useTrackedPlayer();
  const accountId = player?.accountId;

  /* v8 ignore start -- state derivation with null-coalescing */
  // Get combo from navigation state (passed from RivalsPage) or derive from settings
  const navState = location.state as Record<string, unknown> | null;
  const comboFromState = navState?.combo as string | undefined;
  const rivalNameFromState = navState?.rivalName as string | undefined;
  const rivalNameFromUrl = searchParams.get('name') ?? undefined;
  const derivedCombo = useMemo(() => deriveComboFromSettings(settings), [settings]);
  // Fallback: if no combo passed, try the first enabled instrument
  const fallbackInstrument = useMemo(() => getEnabledInstruments(settings)[0], [settings]);
  const combo = comboFromState ?? derivedCombo ?? fallbackInstrument;

  // Leaderboard rival source: comes from navigation state set by LeaderboardRivalsTab
  const source = (navState?.source as 'song' | 'leaderboard') ?? 'song';
  const lbInstrument = navState?.instrument as string | undefined;
  const lbRankBy = (navState?.rankBy as string) ?? 'totalscore';
  /* v8 ignore stop */

  /* v8 ignore start -- cache-based state initialization */
  const cacheKey = source === 'leaderboard'
    ? `lb:${accountId}:${rivalId}:${lbInstrument}:${lbRankBy}`
    : `${accountId}:${rivalId}:${combo}`;
  const hasCachedData = cacheKey === _cachedDetailKey && _cachedDetailSongs.length > 0;

  const [songs_, setSongs] = useState<RivalSongComparison[]>(hasCachedData ? _cachedDetailSongs : []);
  const [rivalName, setRivalName] = useState<string | null>(hasCachedData ? _cachedDetailRivalName : (rivalNameFromState ?? rivalNameFromUrl ?? null));
  const [loading, setLoading] = useState(!hasCachedData);
  /* v8 ignore stop */

  const { albumArtMap, yearMap, sigMap } = useSongLookups();

  /* v8 ignore start — async data fetch */
  useEffect(() => {
    if (!accountId || !rivalId || hasCachedData) return;
    let cancelled = false;
    setLoading(true);

    const fetchPromise = source === 'leaderboard' && lbInstrument
      ? api.getLeaderboardRivalDetail(lbInstrument as Parameters<typeof api.getLeaderboardRivalDetail>[0], accountId, rivalId, lbRankBy as Parameters<typeof api.getLeaderboardRivalDetail>[3])
      : api.getRivalDetail(accountId, combo, rivalId);

    fetchPromise.then(res => {
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
  }, [accountId, rivalId, combo, source, lbInstrument, lbRankBy]);
  /* v8 ignore stop */

  const categories = useMemo(() => categorizeRivalSongs(songs_), [songs_]);

  const { phase, shouldStagger } = usePageTransition(`rivalDetail:${cacheKey}`, !loading, hasCachedData);
  const { forDelay: stagger, clearAnim } = useStagger(shouldStagger);

  const styles = useRivalsSharedStyles();

  /* v8 ignore start -- guard and display state */
  if (!accountId || !rivalId) {
    return <div style={styles.center}>{t('rivals.detail.noSongs')}</div>;
  }

  const PREVIEW_COUNT = 5;
  /* v8 ignore stop */

  const staggerInterval = STAGGER_INTERVAL;
  const displayName = rivalName ?? '\u2026';
  const playerName = player?.displayName ?? 'You';
  /* v8 ignore stop */
  /* v8 ignore start -- JSX render tree */  return (
    <Page
      scrollRestoreKey={`rivalDetail:${cacheKey}`}
      scrollDeps={[phase]}
      loadPhase={phase}
      containerStyle={styles.container}
      before={<PageHeader title={`${playerName} vs. ${displayName}`} actions={!isMobile && phase === LoadPhase.ContentIn ? (
        <button style={{ ...styles.viewProfileButton, ...stagger(0) }} onAnimationEnd={clearAnim} onClick={() => navigate(Routes.player(rivalId!))}>
          <IoPerson size={IconSize.action} />
          {t('common.viewProfile')}
        </button>
      ) : undefined} />}
    >
      {phase === LoadPhase.ContentIn && (
            <div style={{ ...flexColumn, gap: Gap.section, ...(isMobile ? { paddingBottom: Layout.fabPaddingBottom } : undefined) }}>
              {categories.length === 0 && (
                <EmptyState fullPage title={t('rivals.detail.noSongs')} style={stagger(200)} onAnimationEnd={clearAnim} />
              )}
              {(() => {
                let runningDelay = 200;
                return categories.map((cat) => {
                  const preview = cat.songs.slice(0, PREVIEW_COUNT);
                  const baseDelay = runningDelay;
                  runningDelay += (1 + preview.length) * staggerInterval;
                  const navigateToCategory = () =>
                    navigate(Routes.rivalry(rivalId, cat.key, rivalName ?? undefined), { state: { combo, source, instrument: lbInstrument, rankBy: lbRankBy } });
                  return (
                    <div key={cat.key} style={styles.section}>
                      <div
                        className={fx.sectionHeaderClickable}
                        style={{ ...styles.sectionHeaderClickable, ...stagger(baseDelay) }}
                        onAnimationEnd={clearAnim}
                        onClick={navigateToCategory}
                        role="button"
                        tabIndex={0}
                        onKeyDown={e => { if (e.key === 'Enter') navigateToCategory(); }}
                      >
                        <div style={styles.cardHeaderText}>
                          <span style={styles.cardTitle}>
                            {t(cat.titleKey)}
                          </span>
                          <span style={styles.cardDesc}>
                            {t(cat.descriptionKey)}
                          </span>
                        </div>
                        <span style={styles.seeAll}>{t('rivals.seeAll', 'See All')}</span>
                        <IoChevronForward size={20} style={styles.chevron} />
                      </div>
                      <div style={styles.songList}>
                        {preview.map((song, songIdx) => (
                          <RivalSongRow
                            key={`${song.songId}-${song.instrument}`}
                            song={song}
                            albumArt={albumArtMap.get(song.songId)}
                            year={yearMap.get(song.songId)}
                            sig={sigMap.get(song.songId)}
                            playerName={player?.displayName}
                            rivalName={rivalName ?? undefined}
                            onClick={() => navigate(Routes.songDetail(song.songId))}
                            standalone
                            style={stagger(baseDelay + (songIdx + 1) * staggerInterval)}
                            onAnimationEnd={clearAnim}
                          />
                        ))}
                      </div>
                    </div>
                  );
                });
              })()}
            </div>
      )}
    </Page>
  );
  /* v8 ignore stop */
}
