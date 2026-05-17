/* eslint-disable react/forbid-dom-props -- page-level dynamic styles use inline style objects */
import { useCallback, useEffect, useMemo, useRef, useState, type AnimationEvent, type CSSProperties, type ReactNode } from 'react';
import { Link, useLocation, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { IoChevronForward, IoList, IoMusicalNotes, IoPeople, IoStatsChart, IoTrophy } from 'react-icons/io5';
import { DEFAULT_INSTRUMENT, type BandDetailResponse, type BandRankingDto, type BandRankingMetric, type BandType, type PlayerBandEntry, type PlayerBandMember, type ServerInstrumentKey } from '@festival/core/api/serverTypes';
import { ACCURACY_SCALE, LoadPhase } from '@festival/core';
import { Colors, Font, Gap, GridTemplate, IconSize, Layout, Radius, TRANSITION_MS, Weight, flexColumn, flexRow, frostedCard, transition, transitions } from '@festival/theme';
import { api } from '../../api/client';
import { queryKeys } from '../../api/queryKeys';
import type { PageQuickLinksConfig } from '../../components/page/PageQuickLinks';
import EmptyState from '../../components/common/EmptyState';
import PageHeader from '../../components/common/PageHeader';
import { InstrumentIcon } from '../../components/display/InstrumentIcons';
import { SelectProfilePill } from '../../components/player/SelectProfilePill';
import StatBox from '../../components/player/StatBox';
import GoldStars from '../../components/songs/metadata/GoldStars';
import { useAppliedBandComboFilter } from '../../contexts/BandFilterActionContext';
import { useBandPageSelect } from '../../contexts/FabSearchContext';
import { useScrollContainer } from '../../contexts/ScrollContainerContext';
import { useBandRankHistory } from '../../hooks/chart/useBandRankHistory';
import { useNavLinkPress } from '../../hooks/navigation/useNavLinkPress';
import { usePageQuickLinks, type PageQuickLinkItem } from '../../hooks/ui/usePageQuickLinks';
import { usePageTransition } from '../../hooks/ui/usePageTransition';
import { useIsMobile, useIsMobileChrome, useIsWideDesktop } from '../../hooks/ui/useIsMobile';
import { useStagger } from '../../hooks/ui/useStagger';
import { useSelectedProfile, type SelectedBandProfile } from '../../hooks/data/useSelectedProfile';
import { defaultSongFilters, loadSongSettings, saveSongSettings, type SongSettings } from '../../utils/songSettings';
import { createPreserveShellScrollState } from '../../utils/quietNavigation';
import { Routes } from '../../routes';
import Page from '../Page';
import { getLeaderboardPageForRank } from '../leaderboards/helpers/rankingHelpers';
import PlayerSectionHeading from '../player/sections/PlayerSectionHeading';
import BandRankHistoryChart from './components/BandRankHistoryChart';
import BandSongsSection, { useBandSongs } from './components/BandSongsSection';

const VALID_BAND_TYPES: BandType[] = ['Band_Duets', 'Band_Trios', 'Band_Quad'];
const SELECT_BAND_PROFILE_ACTION_SLOT_MAX_WIDTH = 360;
const SELECT_BAND_PROFILE_ACTION_SHADOW_GUTTER = 20;
const SELECT_BAND_PROFILE_ACTION_SLOT_DESKTOP_MAX_WIDTH = SELECT_BAND_PROFILE_ACTION_SLOT_MAX_WIDTH + (SELECT_BAND_PROFILE_ACTION_SHADOW_GUTTER * 2);
const SELECT_BAND_PROFILE_ACTION_SLOT_STYLE: CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'flex-end',
  overflow: 'hidden',
  flexShrink: 0,
  minWidth: 0,
  transition: transitions(
    transition('max-width', TRANSITION_MS),
    transition('opacity', TRANSITION_MS),
  ),
};
const SELECT_BAND_PROFILE_ACTION_SLOT_SHADOW_SAFE_STYLE: CSSProperties = {
  ...SELECT_BAND_PROFILE_ACTION_SLOT_STYLE,
  padding: SELECT_BAND_PROFILE_ACTION_SHADOW_GUTTER,
  margin: -SELECT_BAND_PROFILE_ACTION_SHADOW_GUTTER,
};
const INLINE_MOBILE_PAGE_HEADER_STYLE: CSSProperties = {
  paddingLeft: 0,
  paddingRight: 0,
};
const QUICK_LINK_GLYPH_ICON_SIZE = 20;
const HEADER_FILTER_INSTRUMENT_ICON_SIZE = 32;

type BandQuickLinkId = 'members' | 'summary' | 'statistics' | 'rank-history' | 'songs';

type BandQuickLink = PageQuickLinkItem & {
  id: BandQuickLinkId;
};

type MemberInstrumentFilter = ReadonlyMap<string, ServerInstrumentKey>;

type BandPageProps = {
  statisticsBand?: SelectedBandProfile | null;
};

export default function BandPage({ statisticsBand = null }: BandPageProps) {
  const { t } = useTranslation();
  const { bandId } = useParams<{ bandId?: string }>();
  const [searchParams] = useSearchParams();
  const location = useLocation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { profile, selectBand } = useSelectedProfile();
  const { registerBandPageSelect } = useBandPageSelect();
  const appliedBandComboFilter = useAppliedBandComboFilter();

  const isStatisticsBandMode = !!statisticsBand;
  const lookupAccountId = isStatisticsBandMode ? undefined : searchParams.get('accountId') ?? undefined;
  const lookupTeamKey = statisticsBand?.teamKey ?? searchParams.get('teamKey') ?? undefined;
  const lookupBandTypeRaw = statisticsBand?.bandType ?? searchParams.get('bandType') ?? undefined;
  const lookupBandType = statisticsBand?.bandType ?? (isBandType(lookupBandTypeRaw) ? lookupBandTypeRaw : undefined);
  const routeNames = statisticsBand?.displayName.trim() || searchParams.get('names')?.trim() || undefined;
  const hasTeamContext = !!lookupBandType && !!lookupTeamKey;
  const hasAccountLookupContext = !isStatisticsBandMode && !!lookupAccountId && hasTeamContext;
  const contextualComboId = lookupBandType && appliedBandComboFilter?.bandType === lookupBandType
    ? appliedBandComboFilter.comboId
    : undefined;

  const lookupQuery = useQuery({
    queryKey: queryKeys.bandLookup(lookupAccountId ?? '', lookupBandType ?? '', lookupTeamKey ?? ''),
    queryFn: async () => {
      const response = await api.getPlayerBandsByType(lookupAccountId!, lookupBandType!);
      const match = response.entries.find(entry => entry.bandType === lookupBandType && entry.teamKey === lookupTeamKey);
      if (!match) throw new Error(t('band.lookupFailed'));
      return match;
    },
    enabled: hasAccountLookupContext,
    staleTime: 5 * 60_000,
  });

  const rankingQuery = useQuery({
    queryKey: queryKeys.bandRanking(lookupBandType ?? '', lookupTeamKey ?? ''),
    queryFn: () => getBandRanking(lookupBandType!, lookupTeamKey!),
    enabled: hasTeamContext,
    staleTime: 5 * 60_000,
  });

  const scopedRankingQuery = useQuery({
    queryKey: queryKeys.bandRanking(lookupBandType ?? '', lookupTeamKey ?? '', contextualComboId),
    queryFn: () => getBandRanking(lookupBandType!, lookupTeamKey!, contextualComboId),
    enabled: hasTeamContext && !!contextualComboId,
    staleTime: 5 * 60_000,
    retry: false,
  });

  const rankedContextBand = useMemo(
    () => rankingQuery.data ? buildBandEntryFromRanking(rankingQuery.data, routeNames) : null,
    [rankingQuery.data, routeNames],
  );
  const statisticsContextBand = useMemo(
    () => statisticsBand ? buildBandEntryFromSelectedProfile(statisticsBand) : null,
    [statisticsBand],
  );
  const contextBand = lookupQuery.data ?? rankedContextBand ?? statisticsContextBand;
  const effectiveBandId = bandId ?? statisticsBand?.bandId ?? contextBand?.bandId ?? rankingQuery.data?.bandId ?? null;
  const bandRouteContext = useMemo(() => {
    if (lookupBandType && lookupTeamKey) {
      return { accountId: lookupAccountId, bandType: lookupBandType, teamKey: lookupTeamKey, names: routeNames };
    }
    return routeNames ? { names: routeNames } : undefined;
  }, [lookupAccountId, lookupBandType, lookupTeamKey, routeNames]);

  useEffect(() => {
    if (isStatisticsBandMode) return;
    if (!bandId && contextBand?.bandId) {
      navigate(Routes.band(contextBand.bandId, bandRouteContext), { replace: true });
    }
  }, [bandId, bandRouteContext, contextBand?.bandId, isStatisticsBandMode, navigate]);

  const detailQuery = useQuery({
    queryKey: queryKeys.bandDetail(effectiveBandId ?? ''),
    queryFn: () => api.getBandDetail(effectiveBandId!),
    enabled: !!effectiveBandId && !hasTeamContext && !contextBand,
    staleTime: 5 * 60_000,
  });

  const missingLookupParams = !bandId && !hasTeamContext;
  const loading = (hasTeamContext && rankingQuery.isLoading && !contextBand)
    || (hasAccountLookupContext && lookupQuery.isLoading && !rankingQuery.data)
    || (!!effectiveBandId && !hasTeamContext && !contextBand && detailQuery.isLoading);
  const error = missingLookupParams
    ? new Error(t('band.missingId'))
    : (!contextBand ? (lookupQuery.error ?? rankingQuery.error ?? detailQuery.error ?? null) : null);
  const basePayload = useMemo(() => contextBand
    ? { band: contextBand, ranking: rankingQuery.data ?? null, configurations: rankingQuery.data?.configurations ?? [] }
    : (detailQuery.data ?? null), [contextBand, detailQuery.data, rankingQuery.data]);
  const activeBandType = basePayload?.band.bandType ?? lookupBandType;
  const activeComboId = activeBandType && appliedBandComboFilter?.bandType === activeBandType
    ? appliedBandComboFilter.comboId
    : undefined;

  const detailScopedRankingQuery = useQuery({
    queryKey: queryKeys.bandRanking(basePayload?.band.bandType ?? '', basePayload?.band.teamKey ?? '', activeComboId),
    queryFn: () => getBandRanking(basePayload!.band.bandType, basePayload!.band.teamKey, activeComboId),
    enabled: !!basePayload?.band && !hasTeamContext && !!activeComboId,
    staleTime: 5 * 60_000,
    retry: false,
  });

  const payload = useMemo(() => {
    if (!basePayload) return null;
    if (!activeComboId) return basePayload;

    const scopedRanking = hasTeamContext
      ? (scopedRankingQuery.data ?? null)
      : (detailScopedRankingQuery.data ?? null);

    return {
      ...basePayload,
      ranking: scopedRanking,
      configurations: scopedRanking?.configurations ?? basePayload.configurations ?? [],
    };
  }, [activeComboId, basePayload, detailScopedRankingQuery.data, hasTeamContext, scopedRankingQuery.data]);
  const genericBandTitle = t('band.title');
  const unknownMemberName = t('common.unknownUser');
  const resolvedTitle = payload ? formatBandTitle(payload.band, unknownMemberName, genericBandTitle) : undefined;

  useEffect(() => {
    if (!effectiveBandId || !contextBand || !rankingQuery.data || !Array.isArray(rankingQuery.data.configurations)) return;
    queryClient.setQueryData<BandDetailResponse>(queryKeys.bandDetail(effectiveBandId), {
      band: contextBand,
      ranking: rankingQuery.data,
      configurations: rankingQuery.data.configurations,
    });
  }, [contextBand, effectiveBandId, queryClient, rankingQuery.data]);

  useEffect(() => {
    if (isStatisticsBandMode) return;
    if (!payload || routeNames || (!bandId && contextBand?.bandId)) return;
    if (!resolvedTitle || resolvedTitle === genericBandTitle) return;
    const next = new URLSearchParams(searchParams);
    next.set('names', resolvedTitle);
    navigate(`${location.pathname}?${next.toString()}`, { replace: true, state: location.state });
  }, [bandId, contextBand?.bandId, genericBandTitle, isStatisticsBandMode, location.pathname, location.state, navigate, payload, resolvedTitle, routeNames, searchParams]);

  const pageKey = effectiveBandId ?? `${lookupAccountId ?? 'missing'}:${lookupBandType ?? 'missing'}:${lookupTeamKey ?? 'missing'}`;
  const scopedPageKey = `${pageKey}:${activeComboId ?? 'all'}`;
  const bandRankHistory = useBandRankHistory(payload?.band.bandType, payload?.band.teamKey, 'adjusted', 30, activeComboId);
  const bandSongsQuery = useBandSongs(payload?.band.bandType, payload?.band.teamKey, 5, activeComboId);
  const secondaryLoading = !!payload && (bandRankHistory.loading || bandSongsQuery.isLoading || scopedRankingQuery.isLoading || detailScopedRankingQuery.isLoading);
  const hasCachedData = !!payload && bandRankHistory.hasData && bandSongsQuery.data != null;
  const { phase, shouldStagger } = usePageTransition(`band:${scopedPageKey}`, !loading && !secondaryLoading, hasCachedData);
  const { forIndex: stagger, clearAnim } = useStagger(shouldStagger);
  const isMobile = useIsMobile();
  const isMobileChrome = useIsMobileChrome();
  const isWideDesktop = useIsWideDesktop();
  const scrollContainerRef = useScrollContainer();
  const styles = useStyles();

  const title = resolvedTitle ?? routeNames ?? genericBandTitle;
  const subtitle = payload
    ? t('band.subtitle', {
        type: formatBandType(payload.band.bandType),
        count: payload.band.appearanceCount ?? 0,
      })
    : undefined;
  const activeFilterInstrumentByAccountId = useMemo<MemberInstrumentFilter | null>(
    () => activeComboId && appliedBandComboFilter?.assignments.length
      ? new Map(appliedBandComboFilter.assignments.map(assignment => [assignment.accountId, assignment.instrument]))
      : null,
    [activeComboId, appliedBandComboFilter],
  );
  const activeFilterInstruments = activeFilterInstrumentByAccountId
    ? Array.from(activeFilterInstrumentByAccountId.values())
    : [];
  const bandFilterHeaderIcons = activeFilterInstruments.length > 0 ? (
    <div data-testid="band-header-filter-instruments" aria-hidden="true" style={styles.headerFilterInstruments}>
      {activeFilterInstruments.map((instrument, index) => (
        <InstrumentIcon
          key={`${instrument}:${index}`}
          instrument={instrument}
          size={HEADER_FILTER_INSTRUMENT_ICON_SIZE}
          style={styles.headerFilterInstrumentIcon}
        />
      ))}
    </div>
  ) : undefined;

  const isCurrentBandSelected = !!payload && profile?.type === 'band'
    && profile.bandId === payload.band.bandId
    && profile.teamKey === payload.band.teamKey;
  const selectBandProfileVisible = !!payload && !isCurrentBandSelected;
  const [selectBandProfileMounted, setSelectBandProfileMounted] = useState(selectBandProfileVisible);
  const selectBandProfileExitTimerRef = useRef<number | null>(null);

  const clearSelectBandProfileExitTimer = useCallback(() => {
    if (selectBandProfileExitTimerRef.current === null) return;
    window.clearTimeout(selectBandProfileExitTimerRef.current);
    selectBandProfileExitTimerRef.current = null;
  }, []);

  useEffect(() => () => {
    clearSelectBandProfileExitTimer();
  }, [clearSelectBandProfileExitTimer]);

  useEffect(() => {
    if (selectBandProfileVisible) {
      clearSelectBandProfileExitTimer();
      setSelectBandProfileMounted(true);
      return;
    }

    if (!selectBandProfileMounted) return;

    clearSelectBandProfileExitTimer();
    selectBandProfileExitTimerRef.current = window.setTimeout(() => {
      selectBandProfileExitTimerRef.current = null;
      setSelectBandProfileMounted(false);
    }, TRANSITION_MS);
  }, [clearSelectBandProfileExitTimer, selectBandProfileMounted, selectBandProfileVisible]);

  const buildSelectedBand = useCallback(() => {
    if (!payload) return;
    const bandId = payload.band.bandId;
    if (!bandId) return;

    return {
      bandId,
      bandType: payload.band.bandType,
      teamKey: payload.band.teamKey,
      displayName: title,
      members: payload.band.members.map(member => ({
        accountId: member.accountId,
        displayName: formatMemberName(member, unknownMemberName),
      })),
    };
  }, [payload, title, unknownMemberName]);

  const handleBandProfileClick = useCallback(() => {
    const selectedBand = buildSelectedBand();
    if (!selectedBand) return;
    selectBand(selectedBand);
    if (location.pathname !== Routes.statistics) {
      navigate(Routes.statistics, {
        replace: true,
        state: createPreserveShellScrollState(`profile-select:${selectedBand.bandId}`),
      });
    }
  }, [buildSelectedBand, location.pathname, navigate, selectBand]);

  useEffect(() => {
    if (!selectBandProfileVisible) {
      registerBandPageSelect(null);
      return;
    }

    registerBandPageSelect({ onSelect: handleBandProfileClick });
    return () => registerBandPageSelect(null);
  }, [handleBandProfileClick, registerBandPageSelect, selectBandProfileVisible]);

  const navigateToBandLeaderboard = useCallback((metric: BandRankingMetric, rank: number) => {
    if (!payload || rank <= 0) return;
    navigate(Routes.bandRankings(payload.band.bandType, metric, getLeaderboardPageForRank(rank)));
  }, [navigate, payload]);

  const navigateToSongDetail = useCallback((songId: string) => {
    navigate(Routes.songDetail(songId), { state: { backTo: location.pathname } });
  }, [location.pathname, navigate]);

  const navigateToBandSongs = useCallback((settingsUpdater: (settings: SongSettings) => SongSettings) => {
    const selectedBand = buildSelectedBand();
    if (!selectedBand) return;
    selectBand(selectedBand);
    saveSongSettings(settingsUpdater(loadSongSettings()));
    navigate(Routes.songs, { state: { backTo: location.pathname, restagger: true } });
  }, [buildSelectedBand, location.pathname, navigate, selectBand]);

  const canNavigateToBandSongs = !!payload && (!activeComboId || isCurrentBandSelected);
  const selectBandProfileSlotStyle = isMobile
    ? SELECT_BAND_PROFILE_ACTION_SLOT_STYLE
    : SELECT_BAND_PROFILE_ACTION_SLOT_SHADOW_SAFE_STYLE;

  const bandProfileAction = !isMobileChrome && selectBandProfileMounted ? (
    <div
      data-testid="band-select-profile-slot"
      aria-hidden={!selectBandProfileVisible}
      style={{
        ...selectBandProfileSlotStyle,
        maxWidth: selectBandProfileVisible
          ? SELECT_BAND_PROFILE_ACTION_SLOT_DESKTOP_MAX_WIDTH
          : 0,
        opacity: selectBandProfileVisible ? 1 : 0,
      }}
    >
      <SelectProfilePill
        visible={selectBandProfileVisible}
        label={t('band.selectProfile')}
        ariaLabel={t('band.selectProfile')}
        icon={<IoPeople size={IconSize.action} />}
        onClick={handleBandProfileClick}
      />
    </div>
  ) : undefined;

  const quickLinks = useMemo<BandQuickLink[]>(() => {
    if (!payload) return [];

    return [
      {
        id: 'members',
        label: t('band.members'),
        landmarkLabel: t('band.members'),
        icon: <IoPeople size={QUICK_LINK_GLYPH_ICON_SIZE} />,
      },
      {
        id: 'summary',
        label: t('band.summaryShort'),
        landmarkLabel: t('band.summary'),
        icon: <IoList size={QUICK_LINK_GLYPH_ICON_SIZE} />,
      },
      {
        id: 'statistics',
        label: t('band.statisticsShort'),
        landmarkLabel: t('band.statistics'),
        icon: <IoStatsChart size={QUICK_LINK_GLYPH_ICON_SIZE} />,
      },
      {
        id: 'rank-history',
        label: t('band.rankHistoryShort'),
        landmarkLabel: t('band.rankHistory'),
        icon: <IoTrophy size={QUICK_LINK_GLYPH_ICON_SIZE} />,
      },
      {
        id: 'songs',
        label: t('band.songsShort'),
        landmarkLabel: t('band.songs'),
        icon: <IoMusicalNotes size={QUICK_LINK_GLYPH_ICON_SIZE} />,
      },
    ];
  }, [payload, t]);

  const {
    activeItemId,
    quickLinksOpen,
    openQuickLinks,
    closeQuickLinks,
    handleQuickLinkSelect,
    registerSectionRef,
  } = usePageQuickLinks<BandQuickLink>({
    items: quickLinks,
    scrollContainerRef,
    isDesktopRailEnabled: isWideDesktop,
  });

  const handleModalQuickLinkSelect = useCallback((link: BandQuickLink) => {
    closeQuickLinks();
    handleQuickLinkSelect(link);
  }, [closeQuickLinks, handleQuickLinkSelect]);

  const pageQuickLinks = useMemo<PageQuickLinksConfig | undefined>(() => {
    if (phase !== LoadPhase.ContentIn || error || quickLinks.length < 2) return undefined;

    return {
      title: t('band.quickLinks'),
      items: quickLinks,
      activeItemId,
      visible: quickLinksOpen,
      onOpen: openQuickLinks,
      onClose: closeQuickLinks,
      onSelect: (item) => {
        const nextItem = item as BandQuickLink;
        if (isWideDesktop) {
          handleQuickLinkSelect(nextItem);
          return;
        }
        handleModalQuickLinkSelect(nextItem);
      },
      testIdPrefix: 'band',
    };
  }, [activeItemId, closeQuickLinks, error, handleModalQuickLinkSelect, handleQuickLinkSelect, isWideDesktop, openQuickLinks, phase, quickLinks, quickLinksOpen, t]);

  const registerMembersSectionRef = useCallback((element: HTMLElement | null) => registerSectionRef('members', element), [registerSectionRef]);
  const registerSummarySectionRef = useCallback((element: HTMLElement | null) => registerSectionRef('summary', element), [registerSectionRef]);
  const registerStatisticsSectionRef = useCallback((element: HTMLElement | null) => registerSectionRef('statistics', element), [registerSectionRef]);
  const registerRankHistorySectionRef = useCallback((element: HTMLElement | null) => registerSectionRef('rank-history', element), [registerSectionRef]);
  const registerSongsSectionRef = useCallback((element: HTMLElement | null) => registerSectionRef('songs', element), [registerSectionRef]);
  const useInlineMobileHeader = isMobileChrome && !isStatisticsBandMode && selectBandProfileVisible;
  const suppressMobileStatisticsHeader = isMobileChrome && isStatisticsBandMode;
  const headerTitle = isMobileChrome && (isStatisticsBandMode || useInlineMobileHeader) ? undefined : title;
  const portalHeader = useInlineMobileHeader || suppressMobileStatisticsHeader ? undefined : (
    <PageHeader title={headerTitle} subtitle={subtitle} reserveSubtitleSpace={loading} titleAccessory={bandFilterHeaderIcons} actions={bandProfileAction} />
  );
  const inlineMobileHeader = useInlineMobileHeader ? (
    <PageHeader title={title} subtitle={subtitle} reserveSubtitleSpace={loading} titleAccessory={bandFilterHeaderIcons} style={INLINE_MOBILE_PAGE_HEADER_STYLE} />
  ) : null;

  return (
    <Page
      scrollRestoreKey={`band:${scopedPageKey}`}
      scrollDeps={[phase, effectiveBandId, activeComboId]}
      loadPhase={phase}
      containerStyle={styles.container}
      quickLinks={pageQuickLinks}
      before={portalHeader}
    >
      {inlineMobileHeader}
      {phase === LoadPhase.ContentIn && error && (
        <EmptyState
          fullPage
          title={t('band.notFound')}
          subtitle={error instanceof Error ? error.message : t('band.notFoundSubtitle')}
          style={stagger(0)}
          onAnimationEnd={clearAnim}
        />
      )}

      {phase === LoadPhase.ContentIn && !error && payload && (
        <div style={styles.content}>
          <MembersSection band={payload.band} activeFilterInstrumentByAccountId={activeFilterInstrumentByAccountId} sectionRef={registerMembersSectionRef} style={stagger(0)} onAnimationEnd={clearAnim} />
          <BandSummarySection band={payload.band} sectionRef={registerSummarySectionRef} style={stagger(1)} onAnimationEnd={clearAnim} />
          <BandStatisticsSection
            sectionRef={registerStatisticsSectionRef}
            ranking={payload.ranking ?? null}
            bestSongId={bandSongsQuery.data?.best?.[0]?.songId}
            canNavigateToBandSongs={canNavigateToBandSongs}
            onNavigateToBandSongs={navigateToBandSongs}
            onNavigateToBandLeaderboard={navigateToBandLeaderboard}
            onNavigateToSongDetail={navigateToSongDetail}
            style={stagger(2)}
            onAnimationEnd={clearAnim}
          />
          <BandRankHistorySection band={payload.band} ranking={payload.ranking ?? null} comboId={activeComboId} sectionRef={registerRankHistorySectionRef} style={stagger(3)} onAnimationEnd={clearAnim} />
          <BandSongsSection bandType={payload.band.bandType} teamKey={payload.band.teamKey} displayName={title} comboId={activeComboId} sectionRef={registerSongsSectionRef} style={stagger(4)} onAnimationEnd={clearAnim} />
        </div>
      )}
    </Page>
  );
}

function buildBandEntryFromRanking(ranking: BandRankingDto, routeNames?: string): PlayerBandEntry {
  const routeNameFallbacks = splitRouteNames(routeNames);
  const memberNameByAccountId = new Map(ranking.teamMembers.map(member => [member.accountId, member.displayName?.trim() || undefined]));
  const members: PlayerBandMember[] = (ranking.members?.length ? ranking.members : ranking.teamMembers.map(member => ({
    accountId: member.accountId,
    displayName: member.displayName,
    instruments: [] as ServerInstrumentKey[],
  }))).map((member, index) => ({
    accountId: member.accountId,
    displayName: member.displayName?.trim() || memberNameByAccountId.get(member.accountId) || routeNameFallbacks[index] || null,
    instruments: member.instruments ?? [],
  }));

  return {
    bandId: ranking.bandId,
    bandType: ranking.bandType,
    teamKey: ranking.teamKey,
    appearanceCount: ranking.songsPlayed,
    members,
  };
}

function buildBandEntryFromSelectedProfile(profile: SelectedBandProfile): PlayerBandEntry {
  return {
    bandId: profile.bandId,
    bandType: profile.bandType,
    teamKey: profile.teamKey,
    appearanceCount: 0,
    members: profile.members.map(member => ({
      accountId: member.accountId,
      displayName: member.displayName,
      instruments: [] as ServerInstrumentKey[],
    })),
  };
}

function splitRouteNames(routeNames?: string): string[] {
  if (!routeNames) return [];
  return routeNames
    .split(/\s+\+\s+|\s*,\s*/)
    .map(name => name.trim())
    .filter(Boolean);
}

function MembersSection({ band, activeFilterInstrumentByAccountId, sectionRef, style, onAnimationEnd }: { band: PlayerBandEntry; activeFilterInstrumentByAccountId?: MemberInstrumentFilter | null; sectionRef?: (element: HTMLElement | null) => void; style?: CSSProperties; onAnimationEnd: (e: AnimationEvent<HTMLElement>) => void }) {
  const { t } = useTranslation();
  const styles = useStyles();
  return (
    <section ref={sectionRef} data-testid="band-section-members" style={{ ...styles.section, ...style }} onAnimationEnd={onAnimationEnd} aria-label={t('band.members')}>
      <PlayerSectionHeading title={t('band.members')} />
      <div style={styles.memberGrid}>
        {band.members.map(member => <BandMemberCard key={member.accountId} member={member} activeFilterInstrumentByAccountId={activeFilterInstrumentByAccountId} fallbackName={t('common.unknownUser')} />)}
      </div>
    </section>
  );
}

function BandSummarySection({ band, sectionRef, style, onAnimationEnd }: { band: PlayerBandEntry; sectionRef?: (element: HTMLElement | null) => void; style?: CSSProperties; onAnimationEnd: (e: AnimationEvent<HTMLElement>) => void }) {
  const { t } = useTranslation();
  const styles = useStyles();
  return (
    <section ref={sectionRef} data-testid="band-section-summary" style={{ ...styles.section, ...style }} onAnimationEnd={onAnimationEnd} aria-label={t('band.summary')}>
      <PlayerSectionHeading title={t('band.summary')} />
      <div style={styles.statsGrid}>
        <StatCard label={t('band.type')} value={formatBandType(band.bandType)} />
        <StatCard label={t('band.appearances')} value={(band.appearanceCount ?? 0).toLocaleString()} />
        <StatCard label={t('band.members')} value={band.members.length.toLocaleString()} />
      </div>
    </section>
  );
}

function BandMemberCard({ member, activeFilterInstrumentByAccountId, fallbackName }: { member: PlayerBandMember; activeFilterInstrumentByAccountId?: MemberInstrumentFilter | null; fallbackName: string }) {
  const styles = useStyles();
  const displayName = formatMemberName(member, fallbackName);
  const route = Routes.player(member.accountId);
  const linkPress = useNavLinkPress<HTMLAnchorElement>({ to: route });
  const assignedInstrument = activeFilterInstrumentByAccountId?.get(member.accountId);
  const instruments = activeFilterInstrumentByAccountId
    ? (assignedInstrument ? [assignedInstrument] : [])
    : Array.from(new Set(member.instruments));

  return (
    <Link
      data-testid="band-member-card"
      to={route}
      aria-label={`View ${displayName}`}
      style={{ ...styles.memberCard, ...(linkPress.isPressed ? styles.memberCardPressed : undefined) }}
      data-pressed={linkPress.isPressed ? 'true' : undefined}
      {...linkPress.linkPressHandlers}
    >
      <span style={styles.memberContent}>
        <span style={styles.memberName}>{displayName}</span>
        {instruments.length > 0 && (
          <span style={styles.instrumentRow}>
            {instruments.map(instrument => (
              <InstrumentIcon key={`${member.accountId}:${instrument}`} instrument={instrument as ServerInstrumentKey} size={32} />
            ))}
          </span>
        )}
      </span>
      <IoChevronForward data-testid="band-member-chevron" aria-hidden="true" size={18} style={styles.memberChevron} />
    </Link>
  );
}

function BandStatisticsSection({
  sectionRef,
  ranking,
  bestSongId,
  canNavigateToBandSongs,
  onNavigateToBandSongs,
  onNavigateToBandLeaderboard,
  onNavigateToSongDetail,
  style,
  onAnimationEnd,
}: {
  sectionRef?: (element: HTMLElement | null) => void;
  ranking: BandRankingDto | null;
  bestSongId?: string;
  canNavigateToBandSongs: boolean;
  onNavigateToBandSongs: (settingsUpdater: (settings: SongSettings) => SongSettings) => void;
  onNavigateToBandLeaderboard: (metric: BandRankingMetric, rank: number) => void;
  onNavigateToSongDetail: (songId: string) => void;
  style?: CSSProperties;
  onAnimationEnd: (e: AnimationEvent<HTMLElement>) => void;
}) {
  const { t } = useTranslation();
  const styles = useStyles();

  const songsPlayedClick = canNavigateToBandSongs && ranking && ranking.songsPlayed > 0
    ? () => onNavigateToBandSongs(bandSongsPlayedUpdater)
    : undefined;
  const fullCombosClick = canNavigateToBandSongs && ranking && ranking.fullComboCount > 0
    ? () => onNavigateToBandSongs(bandFullCombosUpdater)
    : undefined;

  return (
    <section ref={sectionRef} data-testid="band-section-statistics" style={{ ...styles.section, ...style }} onAnimationEnd={onAnimationEnd} aria-label={t('band.statistics')}>
      <PlayerSectionHeading title={t('band.statistics')} />
      {!ranking ? (
        <div style={styles.emptyCard}><span style={styles.emptyText}>{t('band.noRanking')}</span></div>
      ) : (
        <div style={styles.statsGrid}>
          <StatCard label={t('band.adjustedRank')} value={formatRank(ranking.adjustedSkillRank)} onClick={rankClick(ranking.adjustedSkillRank, 'adjusted', onNavigateToBandLeaderboard)} />
          <StatCard label={t('band.weightedRank')} value={formatRank(ranking.weightedRank)} onClick={rankClick(ranking.weightedRank, 'weighted', onNavigateToBandLeaderboard)} />
          <StatCard label={t('band.fcRateRank')} value={formatRank(ranking.fcRateRank)} onClick={rankClick(ranking.fcRateRank, 'fcrate', onNavigateToBandLeaderboard)} />
          <StatCard label={t('band.totalScoreRank')} value={formatRank(ranking.totalScoreRank)} onClick={rankClick(ranking.totalScoreRank, 'totalscore', onNavigateToBandLeaderboard)} />
          <StatCard label={t('band.songsPlayed')} value={`${ranking.songsPlayed.toLocaleString()} / ${ranking.totalChartedSongs.toLocaleString()}`} onClick={songsPlayedClick} />
          <StatCard label={t('band.fullCombos')} value={`${ranking.fullComboCount.toLocaleString()} / ${ranking.totalChartedSongs.toLocaleString()}`} onClick={fullCombosClick} />
          <StatCard label={t('band.totalScore')} value={ranking.totalScore.toLocaleString()} />
          <StatCard label={t('band.fcRate')} value={`${(ranking.fcRate * 100).toFixed(1)}%`} />
          <StatCard label={t('band.avgAccuracy')} value={formatAccuracy(ranking.avgAccuracy)} />
          <StatCard label={t('band.avgStars')} value={formatStars(ranking.avgStars)} />
          <StatCard label={t('band.bestSongRank')} value={formatRank(ranking.bestRank)} onClick={bestSongId && ranking.bestRank > 0 ? () => onNavigateToSongDetail(bestSongId) : undefined} />
          <StatCard label={t('band.avgRank')} value={formatAverageRank(ranking.avgRank)} />
        </div>
      )}
    </section>
  );
}

function BandRankHistorySection({ band, ranking, comboId, sectionRef, style, onAnimationEnd }: { band: PlayerBandEntry; ranking: BandRankingDto | null; comboId?: string; sectionRef?: (element: HTMLElement | null) => void; style?: CSSProperties; onAnimationEnd: (e: AnimationEvent<HTMLElement>) => void }) {
  const styles = useStyles();
  return (
    <section ref={sectionRef} data-testid="band-section-rank-history" style={{ ...styles.section, ...style }} onAnimationEnd={onAnimationEnd}>
      <BandRankHistoryChart bandType={band.bandType} teamKey={band.teamKey} comboId={comboId} totalRankedTeams={ranking?.totalRankedTeams} />
    </section>
  );
}

function StatCard({ label, value, onClick }: { label: string; value: ReactNode; onClick?: () => void }) {
  const styles = useStyles();
  return (
    <div data-testid="band-stat-card" style={styles.statCard}>
      <StatBox label={label} value={value} onClick={onClick} />
    </div>
  );
}

function getBandRanking(bandType: BandType, teamKey: string, comboId?: string): Promise<BandRankingDto> {
  return comboId ? api.getBandRanking(bandType, teamKey, comboId) : api.getBandRanking(bandType, teamKey);
}

function rankClick(rank: number, metric: BandRankingMetric, onNavigate: (metric: BandRankingMetric, rank: number) => void): (() => void) | undefined {
  return rank > 0 ? () => onNavigate(metric, rank) : undefined;
}

function bandSongsPlayedUpdater(settings: SongSettings): SongSettings {
  return {
    ...settings,
    instrument: null,
    sortMode: 'title',
    sortAscending: true,
    filters: { ...defaultSongFilters(), hasScores: { [DEFAULT_INSTRUMENT]: true } },
  };
}

function bandFullCombosUpdater(settings: SongSettings): SongSettings {
  return {
    ...settings,
    instrument: null,
    sortMode: 'title',
    sortAscending: true,
    filters: { ...defaultSongFilters(), hasFCs: { [DEFAULT_INSTRUMENT]: true } },
  };
}

function isBandType(value: string | undefined): value is BandType {
  return !!value && VALID_BAND_TYPES.includes(value as BandType);
}

function formatBandType(bandType: BandType): string {
  switch (bandType) {
    case 'Band_Duets': return 'Duos';
    case 'Band_Trios': return 'Trios';
    case 'Band_Quad': return 'Quads';
  }
}

function formatBandTitle(band: PlayerBandEntry, fallbackName: string, fallbackTitle: string): string {
  const names = band.members.map(member => formatMemberName(member, fallbackName));
  return names.length > 0 ? names.join(' + ') : fallbackTitle;
}

function formatMemberName(member: PlayerBandMember, fallbackName: string): string {
  return member.displayName?.trim() || fallbackName;
}

function formatRank(rank: number): string {
  return rank > 0 ? `#${rank.toLocaleString()}` : '—';
}

function formatAccuracy(accuracy: number): string {
  return accuracy > 0 ? `${(accuracy / ACCURACY_SCALE).toFixed(1)}%` : '—';
}

function formatStars(stars: number): ReactNode {
  if (stars === 6) return <GoldStars />;
  return stars > 0 ? stars.toFixed(1) : '—';
}

function formatAverageRank(rank: number): string {
  return rank > 0 ? `#${rank.toFixed(1)}` : '—';
}

function useStyles() {
  return useMemo(() => ({
    container: {
      paddingBottom: Layout.fabPaddingBottom,
    } as CSSProperties,
    content: {
      ...flexColumn,
      gap: Gap.md,
    } as CSSProperties,
    section: {
      ...flexColumn,
      gap: Gap.md,
    } as CSSProperties,
    statsGrid: {
      display: 'grid',
      gridTemplateColumns: GridTemplate.autoFitDetailCards,
      gap: Gap.md,
    } as CSSProperties,
    statCard: {
      ...frostedCard,
      minWidth: 0,
      borderRadius: Radius.md,
      height: '100%',
      overflow: 'hidden',
    } as CSSProperties,
    memberGrid: {
      display: 'grid',
      gridTemplateColumns: GridTemplate.autoFitDetailCards,
      gap: Gap.md,
    } as CSSProperties,
    memberCard: {
      ...frostedCard,
      ...flexRow,
      alignItems: 'center',
      gap: Gap.sm,
      minWidth: 0,
      padding: Gap.md,
      borderRadius: Radius.md,
      color: Colors.textPrimary,
      textDecoration: 'none',
      height: '100%',
      boxSizing: 'border-box',
    } as CSSProperties,
    memberCardPressed: {
      backgroundColor: 'rgba(255, 255, 255, 0.06)',
    } as CSSProperties,
    memberContent: {
      ...flexColumn,
      gap: Gap.sm,
      minWidth: 0,
      flex: 1,
    } as CSSProperties,
    memberName: {
      color: Colors.textPrimary,
      fontSize: Font.lg,
      fontWeight: Weight.bold,
      overflow: 'hidden',
      textOverflow: 'ellipsis',
      whiteSpace: 'nowrap',
    } as CSSProperties,
    instrumentRow: {
      ...flexRow,
      alignItems: 'center',
      gap: Gap.xs,
      flexWrap: 'wrap',
    } as CSSProperties,
    memberChevron: {
      flexShrink: 0,
      color: Colors.textSubtle,
    } as CSSProperties,
    headerFilterInstruments: {
      ...flexRow,
      alignItems: 'center',
      justifyContent: 'flex-end',
      gap: Gap.xs,
      flexShrink: 0,
      lineHeight: 0,
    } as CSSProperties,
    headerFilterInstrumentIcon: {
      display: 'block',
      flexShrink: 0,
    } as CSSProperties,
    emptyCard: {
      ...frostedCard,
      borderRadius: Radius.md,
      padding: Gap.container,
    } as CSSProperties,
    emptyText: {
      color: Colors.textSecondary,
      fontSize: Font.md,
    } as CSSProperties,
  }), []);
}
