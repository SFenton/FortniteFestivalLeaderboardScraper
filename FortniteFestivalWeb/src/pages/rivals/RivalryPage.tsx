/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useEffect, useState, useMemo, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams, useLocation, useNavigate, useSearchParams } from 'react-router-dom';
import { api } from '../../api/client';
import type { PageQuickLinksConfig } from '../../components/page/PageQuickLinks';
import { InstrumentIcon } from '../../components/display/InstrumentIcons';
import { useSettings } from '../../contexts/SettingsContext';
import { useScrollContainer } from '../../contexts/ScrollContainerContext';
import { usePageQuickLinks, type PageQuickLinkItem } from '../../hooks/ui/usePageQuickLinks';
import { useSongLookups } from '../../hooks/data/useSongLookups';
import { usePageTransition } from '../../hooks/ui/usePageTransition';
import { useStagger } from '../../hooks/ui/useStagger';
import EmptyState from '../../components/common/EmptyState';
import PressableButton from '../../components/common/PressableButton';
import PageHeader from '../../components/common/PageHeader';
import { useIsMobileChrome } from '../../hooks/ui/useIsMobile';
import { useTrackedPlayer } from '../../hooks/data/useTrackedPlayer';
import { IoPerson } from 'react-icons/io5';
import { serverInstrumentLabel, type RivalSongComparison, type ServerInstrumentKey } from '@festival/core/api/serverTypes';
import { STAGGER_INTERVAL, Gap, Layout, IconSize } from '@festival/theme';
import { LoadPhase } from '@festival/core';
import { categorizeRivalSongs } from './helpers/rivalCategories';
import RivalSongRow from './components/RivalSongRow';
import { Routes } from '../../routes';

import { useRivalsSharedStyles } from './useRivalsSharedStyles';
import Page from '../Page';
import { coerceRankingMetric } from '../leaderboards/helpers/rankingHelpers';
import { resolveRivalCombo, resolveRivalCombos, type RivalRouteState } from './helpers/rivalRouteState';
import { fetchCombinedRivalDetail } from './helpers/rivalDetailFetch';
import { getPlayerProfileRoute } from '../../utils/profileNavigation';

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

const QUICK_LINK_GLYPH_ICON_SIZE = 20;

type RivalryQuickLink = PageQuickLinkItem;

function rivalrySongQuickLinkId(song: RivalSongComparison, index: number): string {
  return `${song.songId}:${song.instrument}:${index}`;
}

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
  const isMobile = useIsMobileChrome();
  const scrollContainerRef = useScrollContainer();
  const { profile, player } = useTrackedPlayer();
  const accountId = player?.accountId;

  /* v8 ignore start -- state derivation with null-coalescing */
  const navState = location.state as RivalRouteState | null;
  const combos = useMemo(() => resolveRivalCombos(navState, settings), [navState, settings]);
  const combo = combos[0] ?? resolveRivalCombo(navState, settings);
  const comboKey = combos.join(',');

  // Leaderboard rival source: forwarded from RivalDetailPage
  const source = (navState?.source as 'song' | 'leaderboard') ?? 'song';
  const lbInstrument = navState?.instrument as string | undefined;
  const lbRankBy = coerceRankingMetric(navState?.rankBy as string | undefined, true);
  /* v8 ignore stop */

  /* v8 ignore start -- cache-based state initialization */
  const cacheKey = source === 'leaderboard'
    ? `lb:${accountId}:${rivalId}:${lbInstrument}:${lbRankBy}:${mode}`
    : `${accountId}:${rivalId}:${comboKey}`;
  const hasCachedData = cacheKey === _cachedRivalryKey && _cachedRivalrySongs.length > 0;

  const [allSongs, setAllSongs] = useState<RivalSongComparison[]>(hasCachedData ? _cachedRivalrySongs : []);
  const [rivalName, setRivalName] = useState<string | null>(hasCachedData ? _cachedRivalryName : null);
  const [loading, setLoading] = useState(!hasCachedData);
  /* v8 ignore stop */

  const { albumArtMap, yearMap, sigMap } = useSongLookups();

  /* v8 ignore start — async data fetch */
  useEffect(() => {
    if (!accountId || !rivalId || hasCachedData) return;
    if (source !== 'leaderboard' && !combo) return;
    let cancelled = false;
    setLoading(true);

    const fetchPromise = source === 'leaderboard' && lbInstrument
      ? api.getLeaderboardRivalDetail(lbInstrument as Parameters<typeof api.getLeaderboardRivalDetail>[0], accountId, rivalId, lbRankBy as Parameters<typeof api.getLeaderboardRivalDetail>[3])
      : fetchCombinedRivalDetail(accountId, rivalId, combos);

    fetchPromise.then(res => {
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
  }, [accountId, rivalId, comboKey, source, lbInstrument, lbRankBy]);
  /* v8 ignore stop */

  /* v8 ignore start -- category/score computation */
  const category = useMemo(() => {
    const cats = categorizeRivalSongs(allSongs);
    return cats.find(c => c.key === mode) ?? null;
  }, [allSongs, mode]);
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

  const quickLinkItems = useMemo<RivalryQuickLink[]>(() => {
    if (!isMobile || phase !== LoadPhase.ContentIn || !category || category.songs.length === 0) return [];

    return category.songs.map((song, index) => {
      const label = song.title ?? song.songId;
      const instrument = song.instrument as ServerInstrumentKey;
      const instrumentLabel = serverInstrumentLabel(instrument);

      return {
        id: rivalrySongQuickLinkId(song, index),
        label,
        landmarkLabel: `${label} (${instrumentLabel})`,
        icon: <InstrumentIcon instrument={instrument} sig={sigMap.get(song.songId)} size={QUICK_LINK_GLYPH_ICON_SIZE} />,
      };
    });
  }, [category, isMobile, phase, sigMap]);

  const {
    activeItemId,
    quickLinksOpen,
    openQuickLinks,
    closeQuickLinks,
    handleQuickLinkSelect,
    registerSectionRef,
  } = usePageQuickLinks<RivalryQuickLink>({
    items: quickLinkItems,
    scrollContainerRef,
    isDesktopRailEnabled: false,
  });

  const handleModalQuickLinkSelect = useCallback((item: RivalryQuickLink) => {
    closeQuickLinks();
    handleQuickLinkSelect(item);
  }, [closeQuickLinks, handleQuickLinkSelect]);

  const pageQuickLinks = useMemo<PageQuickLinksConfig | undefined>(() => {
    if (!isMobile || phase !== LoadPhase.ContentIn || quickLinkItems.length === 0) return undefined;

    return {
      title: t('common.quickLinks', 'Quick Links'),
      items: quickLinkItems,
      activeItemId,
      visible: quickLinksOpen,
      showDesktopRail: false,
      onOpen: openQuickLinks,
      onClose: closeQuickLinks,
      onSelect: (item) => handleModalQuickLinkSelect(item as RivalryQuickLink),
      testIdPrefix: 'rivalry',
    };
  }, [activeItemId, closeQuickLinks, handleModalQuickLinkSelect, isMobile, openQuickLinks, phase, quickLinkItems, quickLinksOpen, t]);
  /* v8 ignore stop */

  /* v8 ignore start -- JSX render tree */
  return (
    <Page
      scrollRestoreKey={`rivalry:${cacheKey}:${mode}`}
      scrollDeps={[phase]}
      loadPhase={phase}
      containerStyle={styles.container}
      quickLinks={pageQuickLinks}
      before={<PageHeader title={title} actions={!isMobile && phase === LoadPhase.ContentIn ? (
        <PressableButton style={{ ...styles.viewProfileButton, ...stagger(0) }} onAnimationEnd={clearAnim} onPress={() => navigate(getPlayerProfileRoute(rivalId!, profile))}>
          <IoPerson size={IconSize.action} />
          {(rivalName || searchParams.get('name'))
            ? t('common.viewNameProfile', { name: rivalName ?? searchParams.get('name') })
            : t('common.viewProfile')}
        </PressableButton>
      ) : undefined} />}
    >
      {phase === LoadPhase.ContentIn && (
            <div style={isMobile ? { paddingBottom: Layout.fabPaddingBottom } : undefined}>
              {!category || category.songs.length === 0 ? (
                <EmptyState fullPage title={t('rivals.detail.noSongs')} style={stagger(200)} onAnimationEnd={clearAnim} />
              ) : (
                <div style={{ ...styles.songList, paddingTop: Gap.md }}>
                  {category.songs.map((song, index) => {
                    const quickLinkId = rivalrySongQuickLinkId(song, index);

                    return (
                      <div key={`${song.songId}-${song.instrument}`} ref={(element) => { registerSectionRef(quickLinkId, element); }}>
                        <RivalSongRow
                          song={song}
                          albumArt={albumArtMap.get(song.songId)}
                          year={yearMap.get(song.songId)}
                          sig={sigMap.get(song.songId)}
                          playerName={player?.displayName}
                          rivalName={rivalName ?? undefined}
                          onClick={() => navigate(Routes.songDetail(song.songId))}
                          standalone
                          style={stagger((++staggerIdx) * staggerInterval)}
                          onAnimationEnd={clearAnim}
                        />
                      </div>
                    );
                  })}
                </div>
              )}
            </div>
      )}
    </Page>
  );
  /* v8 ignore stop */
}
