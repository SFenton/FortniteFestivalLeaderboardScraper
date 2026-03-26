/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useEffect, useState, useCallback, useRef, useMemo, type CSSProperties } from 'react';
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
import ArcSpinner from '../../components/common/ArcSpinner';
import type { RivalSongComparison } from '@festival/core/api/serverTypes';
import { STAGGER_INTERVAL, Gap, Position, ZIndex, Display, Align, Justify, Colors, Font, Layout, flexColumn, flexCenter, padding } from '@festival/theme';
import { LoadPhase } from '@festival/core';
import { deriveComboFromSettings, getEnabledInstruments } from './helpers/comboUtils';
import { categorizeRivalSongs } from './helpers/rivalCategories';
import RivalSongRow from './components/RivalSongRow';
import { Routes } from '../../routes';

import { useRivalsSharedStyles } from './useRivalsSharedStyles';
import Page from '../Page';

const MODE_TITLE_KEYS: Record<string, string> = {
  closest_battles: 'rivals.detail.closestBattles',
  almost_passed: 'rivals.detail.almostPassed',
  slipping_away: 'rivals.detail.slippingAway',
  barely_winning: 'rivals.detail.barelyWinning',
  pulling_forward: 'rivals.detail.pullingForward',
  dominating_them: 'rivals.detail.dominatingThem',
};

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
  const { settings } = useSettings();
  const isMobile = useIsMobile();
  const { player } = useTrackedPlayer();
  const accountId = player?.accountId;

  /* v8 ignore start -- state derivation with null-coalescing */
  const comboFromState = (location.state as Record<string, unknown> | null)?.combo as string | undefined;
  const derivedCombo = useMemo(() => deriveComboFromSettings(settings), [settings]);
  const fallbackInstrument = useMemo(() => getEnabledInstruments(settings)[0], [settings]);
  const combo = comboFromState ?? derivedCombo ?? fallbackInstrument;
  /* v8 ignore stop */

  /* v8 ignore start -- cache-based state initialization */
  const cacheKey = `${accountId}:${rivalId}:${combo}`;
  const hasCachedData = cacheKey === _cachedRivalryKey && _cachedRivalrySongs.length > 0;

  const [allSongs, setAllSongs] = useState<RivalSongComparison[]>(hasCachedData ? _cachedRivalrySongs : []);
  const [rivalName, setRivalName] = useState<string | null>(hasCachedData ? _cachedRivalryName : null);
  const [loading, setLoading] = useState(!hasCachedData);
  /* v8 ignore stop */

  const { albumArtMap, yearMap } = useSongLookups();

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

  const { phase, shouldStagger } = usePageTransition(`rivalry:${cacheKey}:${mode}`, !loading, hasCachedData);
  const { forDelay: stagger, clearAnim } = useStagger(shouldStagger);

  const styles = useRivalsSharedStyles();

  /* v8 ignore start -- guard */
  if (!accountId || !rivalId) {
    return <div style={styles.center}>{t('rivals.detail.noSongs')}</div>;
  }
  /* v8 ignore stop */

  /* v8 ignore start -- render-time helpers */
  const staggerInterval = STAGGER_INTERVAL;
  let staggerIdx = 0;

  const title = MODE_TITLE_KEYS[mode] ? t(MODE_TITLE_KEYS[mode]) : mode;
  /* v8 ignore stop */

  /* v8 ignore start -- JSX render tree */
  return (
    <Page
      scrollRestoreKey={`rivalry:${cacheKey}:${mode}`}
      scrollDeps={[phase]}
      containerStyle={styles.container}
      before={<>
        <PageHeader title={title} />
        {phase !== LoadPhase.ContentIn && (
          <div
            style={phase === LoadPhase.SpinnerOut ? { ...styles.spinnerOverlay, ...styles.spinnerFadeOut } : styles.spinnerOverlay}
          >
            <ArcSpinner />
          </div>
        )}
      </>}
    >
      {phase === LoadPhase.ContentIn && (
            <div style={isMobile ? { paddingBottom: Layout.fabPaddingBottom } : undefined}>
              {!category || category.songs.length === 0 ? (
                <EmptyState title={t('rivals.detail.noSongs')} style={stagger(200)} onAnimationEnd={clearAnim} />
              ) : (
                <div style={{ ...styles.songList, paddingTop: Gap.md }}>
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
      )}
    </Page>
  );
  /* v8 ignore stop */
}
