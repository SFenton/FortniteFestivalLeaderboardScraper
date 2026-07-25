import { useEffect, useMemo, useCallback, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams, useLocation, useNavigate, useSearchParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { api } from '../../api/client';
import { queryKeys } from '../../api/queryKeys';
import { remoteDataQueryPolicy } from '../../api/queryPolicy';
import type { PageQuickLinksConfig } from '../../components/page/PageQuickLinks';
import { useSettings } from '../../contexts/SettingsContext';
import { useScrollContainer } from '../../contexts/ScrollContainerContext';
import { usePageQuickLinks, type PageQuickLinkItem } from '../../hooks/ui/usePageQuickLinks';
import { useSongLookups } from '../../hooks/data/useSongLookups';
import { usePageTransition } from '../../hooks/ui/usePageTransition';
import { useSetPageReady } from '../../contexts/PageReadyContext';
import { useStagger } from '../../hooks/ui/useStagger';
import EmptyState from '../../components/common/EmptyState';
import PressableButton from '../../components/common/PressableButton';
import PageHeader from '../../components/common/PageHeader';
import { useIsMobileChrome } from '../../hooks/ui/useIsMobile';
import { useTrackedPlayer } from '../../hooks/data/useTrackedPlayer';
import { IoChevronForward, IoPerson } from 'react-icons/io5';
import type { RivalSongComparison } from '@festival/core/api';
import { STAGGER_INTERVAL, Gap, Layout, flexColumn, IconSize } from '@festival/theme';
import { LoadPhase } from '@festival/core/runtime';
import { categorizeRivalSongs } from './helpers/rivalCategories';
import RivalSongRow from './components/RivalSongRow';
import CardPressable from '../../components/common/CardPressable';
import { Routes } from '../../routes';
import { useRivalsSharedStyles } from './useRivalsSharedStyles';
import fx from '../../styles/effects.module.css';
import Page from '../Page';
import { coerceRankingMetric } from '../leaderboards/helpers/rankingHelpers';
import { resolveRivalCombo, resolveRivalCombos, rivalComboStateForNavigation, type RivalRouteState } from './helpers/rivalRouteState';
import { fetchCombinedRivalDetail } from './helpers/rivalDetailFetch';
import { getPlayerProfileRoute } from '../../utils/profileNavigation';

type RivalDetailQuickLink = PageQuickLinkItem;

function rivalDetailCategoryQuickLinkId(categoryKey: string): string {
  return `rival-category:${categoryKey}`;
}

export default function RivalDetailPage() {
  const { t } = useTranslation();
  const { rivalId } = useParams<{ rivalId: string }>();
  const [searchParams] = useSearchParams();
  const location = useLocation();
  const navigate = useNavigate();
  const { settings } = useSettings();
  const isMobile = useIsMobileChrome();
  const scrollContainerRef = useScrollContainer();
  const { profile, player } = useTrackedPlayer();
  const accountId = player?.accountId;

  /* v8 ignore start -- state derivation with null-coalescing */
  // Get combo from navigation state (passed from RivalsPage) or derive from settings
  const navState = location.state as RivalRouteState | null;
  const rivalNameFromState = navState?.rivalName as string | undefined;
  const rivalNameFromUrl = searchParams.get('name') ?? undefined;
  const combos = useMemo(() => resolveRivalCombos(navState, settings), [navState, settings]);
  const combo = combos[0] ?? resolveRivalCombo(navState, settings);
  const comboKey = combos.join(',');
  const allowLiveFallback = navState?.allowLiveFallback === true;

  // Leaderboard rival source: comes from navigation state set by LeaderboardRivalsTab
  const source = (navState?.source as 'song' | 'leaderboard') ?? 'song';
  const lbInstrument = navState?.instrument as string | undefined;
  const lbRankBy = coerceRankingMetric(navState?.rankBy as string | undefined, true);
  /* v8 ignore stop */

  const cacheKey = source === 'leaderboard'
    ? `lb:${accountId}:${rivalId}:${lbInstrument}:${lbRankBy}`
    : `${accountId}:${rivalId}:${comboKey}`;
  const detailQuery = useQuery({
    queryKey: queryKeys.rivalDetail(accountId ?? '', rivalId ?? '', source === 'leaderboard'
      ? {
          source,
          instrument: lbInstrument,
          rankBy: lbRankBy,
        }
      : {
          source,
          scopes: combos,
          allowLiveFallback,
        }),
    queryFn: ({ signal }) => source === 'leaderboard' && lbInstrument
      ? api.getLeaderboardRivalDetail(
          lbInstrument as Parameters<typeof api.getLeaderboardRivalDetail>[0],
          accountId!,
          rivalId!,
          lbRankBy as Parameters<typeof api.getLeaderboardRivalDetail>[3],
          undefined,
          { signal },
        )
      : fetchCombinedRivalDetail(
          accountId!,
          rivalId!,
          combos,
          undefined,
          allowLiveFallback ? { allowLiveFallback: true } : undefined,
          { signal },
        ),
    enabled: !!accountId && !!rivalId && (source === 'leaderboard' ? !!lbInstrument : !!combo),
    ...remoteDataQueryPolicy,
  });
  const songs_: RivalSongComparison[] = detailQuery.data?.songs ?? [];
  const rivalName = detailQuery.data?.rival.displayName ?? rivalNameFromState ?? rivalNameFromUrl ?? null;
  const loading = detailQuery.isPending;
  const mountedWithDataRef = useRef(detailQuery.data !== undefined);
  const initialCacheKeyRef = useRef(cacheKey);
  const hasCachedData = initialCacheKeyRef.current === cacheKey && mountedWithDataRef.current;

  const { albumArtMap, yearMap, sigMap } = useSongLookups();

  useEffect(() => {
    const resolvedName = detailQuery.data?.rival.displayName;
    if (!resolvedName || searchParams.has('name')) return;
    const next = new URLSearchParams(searchParams);
    next.set('name', resolvedName);
    navigate(`${location.pathname}?${next.toString()}`, { replace: true, state: location.state });
  }, [detailQuery.data?.rival.displayName, location.pathname, location.state, navigate, searchParams]);

  const categories = useMemo(() => categorizeRivalSongs(songs_), [songs_]);

  const { phase, shouldStagger } = usePageTransition(`rivalDetail:${cacheKey}`, !loading, hasCachedData);
  useSetPageReady(phase === LoadPhase.ContentIn);
  const { forDelay: stagger, clearAnim } = useStagger(shouldStagger);

  const styles = useRivalsSharedStyles();

  const PREVIEW_COUNT = 5;

  const quickLinkItems = useMemo<RivalDetailQuickLink[]>(() => {
    if (!isMobile || phase !== LoadPhase.ContentIn || categories.length === 0) return [];

    return categories.map((cat) => {
      const label = t(cat.titleKey);
      return {
        id: rivalDetailCategoryQuickLinkId(cat.key),
        label,
        landmarkLabel: label,
      };
    });
  }, [categories, isMobile, phase, t]);

  const {
    activeItemId,
    quickLinksOpen,
    openQuickLinks,
    closeQuickLinks,
    handleQuickLinkSelect,
    registerSectionRef,
  } = usePageQuickLinks<RivalDetailQuickLink>({
    items: quickLinkItems,
    scrollContainerRef,
    isDesktopRailEnabled: false,
  });

  const handleModalQuickLinkSelect = useCallback((item: RivalDetailQuickLink) => {
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
      onSelect: (item) => handleModalQuickLinkSelect(item as RivalDetailQuickLink),
      testIdPrefix: 'rival-detail',
    };
  }, [activeItemId, closeQuickLinks, handleModalQuickLinkSelect, isMobile, openQuickLinks, phase, quickLinkItems, quickLinksOpen, t]);

  /* v8 ignore start -- guard and display state */
  if (!accountId || !rivalId) {
    return <div style={styles.center}>{t('rivals.detail.noSongs')}</div>;
  }

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
      quickLinks={pageQuickLinks}
      before={<PageHeader title={`${playerName} vs. ${displayName}`} actions={!isMobile && phase === LoadPhase.ContentIn ? (
        <PressableButton style={{ ...styles.viewProfileButton, ...stagger(0) }} onAnimationEnd={clearAnim} onPress={() => navigate(getPlayerProfileRoute(rivalId!, profile))}>
          <IoPerson size={IconSize.action} />
          {(rivalName || searchParams.get('name'))
            ? t('common.viewNameProfile', { name: rivalName ?? searchParams.get('name') })
            : t('common.viewProfile')}
        </PressableButton>
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
                  const quickLinkId = rivalDetailCategoryQuickLinkId(cat.key);
                  const navigateToCategory = () =>
                    navigate(Routes.rivalry(rivalId, cat.key, rivalName ?? undefined), {
                      state: { ...rivalComboStateForNavigation(navState, combo), source, instrument: lbInstrument, rankBy: lbRankBy },
                    });
                  return (
                    <div key={cat.key} style={styles.section} ref={(element) => { registerSectionRef(quickLinkId, element); }}>
                      <CardPressable
                        className={fx.sectionHeaderClickable}
                        style={{ ...styles.sectionHeaderClickable, ...stagger(baseDelay) }}
                        pressedStyle={styles.pressablePressed}
                        onAnimationEnd={clearAnim}
                        onPress={navigateToCategory}
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
                      </CardPressable>
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
