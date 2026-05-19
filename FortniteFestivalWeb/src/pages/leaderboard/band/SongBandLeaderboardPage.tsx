/* eslint-disable react/forbid-dom-props -- page-level dynamic styles use inline style objects */
import { useCallback, useEffect, useMemo, useState, type AnimationEvent, type CSSProperties } from 'react';
import { Link, useParams, useSearchParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { LoadPhase } from '@festival/core';
import type { ServerInstrumentKey, SongBandLeaderboardEntry } from '@festival/core/api/serverTypes';
import { Display, Gap, flexColumn } from '@festival/theme';
import { api } from '../../../api/client';
import { queryKeys } from '../../../api/queryKeys';
import EmptyState from '../../../components/common/EmptyState';
import BandFilterPill from '../../../components/common/BandFilterPill';
import { FixedLeaderboardPagination, FixedLeaderboardPlayerFooter, useLeaderboardFooterScrollMargin } from '../../../components/leaderboard/LeaderboardPaginationFooter';
import SongInfoHeader from '../../../components/songs/headers/SongInfoHeader';
import SongBandScoreFooter, { SongBandMemberMetadata, formatSongBandAccuracy, getSongBandMemberScoreWidth, getSongBandScoreWidth, hasSongBandMemberAccuracy, hasSongBandMemberStars } from '../../../components/bands/SongBandScoreFooter';
import { useFestival } from '../../../contexts/FestivalContext';
import { useIsMobile, useIsMobileChrome } from '../../../hooks/ui/useIsMobile';
import { usePageTransition } from '../../../hooks/ui/usePageTransition';
import { useStagger } from '../../../hooks/ui/useStagger';
import { useNavigateToSongDetail } from '../../../hooks/navigation/useNavigateToSongDetail';
import { useScrollContainer } from '../../../contexts/ScrollContainerContext';
import { useAppliedBandComboFilter } from '../../../contexts/BandFilterActionContext';
import { useFabSearch } from '../../../contexts/FabSearchContext';
import { useSelectedProfile } from '../../../hooks/data/useSelectedProfile';
import { parseApiError } from '../../../utils/apiError';
import { isBandFilterForSelectedProfile } from '../../../state/bandFilter';
import { formatPageBandComboLabel, getPageBandComboInstruments, PAGE_BAND_COMBO_ALL_VALUE, resolvePageBandComboState } from '../../../utils/pageBandComboFilter';
import { coerceSongBandType, songBandToPlayerBandEntry, songBandTypeLabel } from '../../../utils/songBandLeaderboards';
import Page, { PageBackground } from '../../Page';
import { PageMessage } from '../../PageMessage';
import PlayerBandCard, { formatPlayerBandNames } from '../../player/components/PlayerBandCard';
import { LeaderboardEntry } from '../global/components/LeaderboardEntry';
import { computeRankWidth } from '../../leaderboards/helpers/rankingHelpers';
import { formatBandTeamName } from '../../leaderboards/helpers/bandRankingHelpers';
import BandComboFilterModal from '../../leaderboards/modals/BandComboFilterModal';
import { getBandProfileRoute } from '../../../utils/profileNavigation';

const PAGE_SIZE = 25;
const COMBO_CATALOG_STALE_TIME_MS = 10 * 60_000;
const SONG_BAND_LEADERBOARD_STALE_TIME_MS = 5 * 60_000;

export default function SongBandLeaderboardPage() {
  const { t } = useTranslation();
  const { songId = '', bandType: rawBandType } = useParams<{ songId: string; bandType: string }>();
  const [searchParams, setSearchParams] = useSearchParams();
  const scrollContainerRef = useScrollContainer();
  const isMobile = useIsMobile();
  const isMobileChrome = useIsMobileChrome();
  const { registerLeaderboardActions } = useFabSearch();
  const hasFab = isMobileChrome;
  const reserveFabSpace = false;
  const {
    state: { songs },
  } = useFestival();

  const song = songs.find((s) => s.songId === songId);
  const bandType = coerceSongBandType(rawBandType);
  const { profile } = useSelectedProfile();
  const selectedBand = profile?.type === 'band' ? profile : null;
  const appliedBandComboFilter = useAppliedBandComboFilter();
  const pageBandComboState = useMemo(
    () => resolvePageBandComboState(bandType, searchParams, appliedBandComboFilter),
    [appliedBandComboFilter, bandType, searchParams],
  );
  const activeComboId = pageBandComboState.comboId;
  const selectedBandTeamKey = profile?.type === 'band' && profile.bandType === bandType ? profile.teamKey : undefined;
  const selectedBandHasGlobalFilter = isBandFilterForSelectedProfile(appliedBandComboFilter, profile);
  const selectedBandMatchesView = selectedBand?.bandType === bandType;
  const bandLabel = bandType ? songBandTypeLabel(bandType, t) : '';
  const page = Math.max(1, Number(searchParams.get('page')) || 1);
  const goToSongDetail = useNavigateToSongDetail(songId);
  const [comboModalOpen, setComboModalOpen] = useState(false);

  const comboCatalogQuery = useQuery({
    queryKey: queryKeys.bandRankingCombos(bandType ?? 'unknown'),
    queryFn: () => api.getBandRankingCombos(bandType!),
    enabled: !!bandType,
    staleTime: COMBO_CATALOG_STALE_TIME_MS,
  });

  const activeComboInstruments = useMemo(
    () => getPageBandComboInstruments(pageBandComboState, bandType, appliedBandComboFilter, comboCatalogQuery.data?.combos),
    [appliedBandComboFilter, bandType, comboCatalogQuery.data?.combos, pageBandComboState],
  );
  const comboFilterLabel = activeComboInstruments.length > 0
    ? formatPageBandComboLabel(activeComboInstruments)
    : t('bandComboFilter.actionLabel');
  const openComboModal = useCallback(() => setComboModalOpen(true), []);
  const closeComboModal = useCallback(() => setComboModalOpen(false), []);
  const applyComboFilter = useCallback((comboId: string) => {
    if (!bandType) return;
    const params = new URLSearchParams(searchParams);
    params.set('combo', comboId);
    params.set('page', '1');
    scrollContainerRef.current?.scrollTo(0, 0);
    setSearchParams(params, { replace: true });
    setComboModalOpen(false);
  }, [bandType, scrollContainerRef, searchParams, setSearchParams]);
  const clearComboFilter = useCallback(() => {
    if (!bandType) return;
    const params = new URLSearchParams(searchParams);
    if (appliedBandComboFilter?.bandType === bandType) params.set('combo', PAGE_BAND_COMBO_ALL_VALUE);
    else params.delete('combo');
    params.set('page', '1');
    scrollContainerRef.current?.scrollTo(0, 0);
    setSearchParams(params, { replace: true });
    setComboModalOpen(false);
  }, [appliedBandComboFilter?.bandType, bandType, scrollContainerRef, searchParams, setSearchParams]);

  useEffect(() => {
    registerLeaderboardActions({
      openBandCombo: openComboModal,
      bandComboActive: !!activeComboId,
      bandComboInstruments: activeComboInstruments,
      bandComboLabel: comboFilterLabel,
    });
    return () => registerLeaderboardActions(null);
  }, [activeComboId, activeComboInstruments, comboFilterLabel, openComboModal, registerLeaderboardActions]);

  const leaderboardQuery = useQuery({
    queryKey: queryKeys.songBandLeaderboard(songId, bandType ?? 'unknown', PAGE_SIZE, (page - 1) * PAGE_SIZE, undefined, selectedBandTeamKey, activeComboId),
    queryFn: () => api.getSongBandLeaderboard(songId, bandType!, PAGE_SIZE, (page - 1) * PAGE_SIZE, undefined, selectedBandTeamKey, activeComboId),
    enabled: !!songId && !!bandType,
    staleTime: SONG_BAND_LEADERBOARD_STALE_TIME_MS,
  });

  const data = leaderboardQuery.data;
  const loading = leaderboardQuery.isFetching && !data;
  const entries = data?.entries ?? [];
  const selectedBandEntry = data?.selectedBandEntry ?? null;
  const hasSelectedBandFooter = !!selectedBandEntry;
  const widthEntries = hasSelectedBandFooter && selectedBandEntry ? [...entries, selectedBandEntry] : entries;
  const scoreWidth = useMemo(() => getSongBandScoreWidth(widthEntries), [widthEntries]);
  const memberScoreWidth = useMemo(() => getSongBandMemberScoreWidth(widthEntries), [widthEntries]);
  const showMemberStars = useMemo(() => hasSongBandMemberStars(widthEntries), [widthEntries]);
  const showMemberAccuracy = useMemo(() => hasSongBandMemberAccuracy(widthEntries), [widthEntries]);
  const selectedBandFooterName = useMemo(() => {
    if (!selectedBandEntry) return undefined;
    return formatBandTeamName(selectedBandEntry.members, profile?.type === 'band' ? profile.displayName : selectedBandEntry.teamKey);
  }, [profile, selectedBandEntry]);
  const selectedBandFooterRoute = useMemo(() => {
    if (!selectedBandEntry || !selectedBandFooterName) return undefined;
    return getBandProfileRoute(selectedBandEntry.bandId, {
      bandType: selectedBandEntry.bandType,
      teamKey: selectedBandEntry.teamKey,
      names: selectedBandFooterName,
    }, profile);
  }, [profile, selectedBandEntry, selectedBandFooterName]);
  const selectedBandFooterRankWidth = useMemo(() => {
    if (!selectedBandEntry) return undefined;
    return computeRankWidth([selectedBandEntry.rank]);
  }, [selectedBandEntry]);
  const totalEntries = data?.totalEntries ?? 0;
  const localEntries = data?.localEntries ?? totalEntries;
  const totalPages = Math.max(1, Math.ceil(localEntries / PAGE_SIZE));
  const hasPagination = !!data && !leaderboardQuery.error && totalPages > 1;

  const goToPage = useCallback((nextPage: number) => {
    if (nextPage < 1 || nextPage > totalPages) return;
    const params = new URLSearchParams(searchParams);
    params.set('page', String(nextPage));
    scrollContainerRef.current?.scrollTo(0, 0);
    setSearchParams(params, { replace: true });
  }, [searchParams, scrollContainerRef, setSearchParams, totalPages]);

  useEffect(() => {
    if (data && localEntries > 0 && page > totalPages) {
      goToPage(totalPages);
    }
  }, [data, goToPage, localEntries, page, totalPages]);

  const { phase, shouldStagger } = usePageTransition(`songBandLeaderboard:${songId}:${bandType}:${activeComboId ?? 'all'}:${selectedBandTeamKey ?? 'none'}:${page}`, !loading);
  const { forIndex: stagger, clearAnim } = useStagger(shouldStagger);
  useLeaderboardFooterScrollMargin({ hasFab, hasPagination, hasPlayerFooter: hasSelectedBandFooter });
  const styles = useStyles();

  if (!songId || !bandType) {
    return <PageMessage>{t('songBandLeaderboard.notFound')}</PageMessage>;
  }

  const subtitle = data?.showLeaderboardEntryTotals === true
    ? t('songBandLeaderboard.subtitle', {
        type: bandLabel,
        count: totalEntries,
        formattedCount: totalEntries.toLocaleString(),
      })
    : bandLabel;

  return (
    <Page
      scrollRestoreKey={`songBandLeaderboard:${songId}:${bandType}:${activeComboId ?? 'all'}:${page}`}
      scrollDeps={[phase, entries.length, page, bandType, activeComboId]}
      loadPhase={phase}
      fabSpacer="none"
      background={<PageBackground src={song?.albumArt} />}
      before={(
        <SongInfoHeader
          song={song}
          songId={songId}
          collapsed
          hideBackground
          onTitleClick={goToSongDetail}
          subtitle2={subtitle}
          actions={!isMobileChrome ? <BandFilterPill label={comboFilterLabel} selectedInstruments={activeComboInstruments} bandType={activeComboId ? bandType : null} onClick={openComboModal} /> : undefined}
        />
      )}
      after={<BandComboFilterModal
        visible={comboModalOpen}
        bandType={bandType}
        activeInstruments={activeComboInstruments}
        selectedBandName={selectedBandMatchesView ? selectedBand?.displayName : undefined}
        selectedBandHasGlobalFilter={selectedBandMatchesView && selectedBandHasGlobalFilter}
        onCancel={closeComboModal}
        onApplyCombo={applyComboFilter}
        onClearCombo={clearComboFilter}
      />}
    >
      {phase === LoadPhase.ContentIn && leaderboardQuery.error && (
        <EmptyState
          fullPage
          title={t('songBandLeaderboard.failedToLoad')}
          subtitle={leaderboardQuery.error instanceof Error ? parseApiError(leaderboardQuery.error.message).title : undefined}
          style={stagger(0)}
          onAnimationEnd={clearAnim}
        />
      )}

      {phase === LoadPhase.ContentIn && !leaderboardQuery.error && data && (
        <div style={styles.content}>
          {entries.length === 0 ? (
            <EmptyState
              title={t('songBandLeaderboard.emptyTitle')}
              subtitle={t('songBandLeaderboard.emptySubtitle', { type: bandLabel })}
              style={stagger(0)}
              onAnimationEnd={clearAnim}
            />
          ) : (
            <div data-testid="song-band-leaderboard-list" style={styles.cardGrid}>
              {entries.map((entry, index) => (
                <SongBandLeaderboardRow
                  key={`${entry.bandType}:${entry.teamKey}:${entry.rank}`}
                  entry={entry}
                  isMobile={isMobile}
                  scoreWidth={scoreWidth}
                  memberScoreWidth={memberScoreWidth}
                  showMemberStars={showMemberStars}
                  showMemberAccuracy={showMemberAccuracy}
                  activeFilterInstruments={activeComboInstruments}
                  style={stagger(index)}
                  onAnimationEnd={clearAnim}
                />
              ))}
            </div>
          )}

          {hasPagination && (
            <FixedLeaderboardPagination
              page={page}
              totalPages={totalPages}
              onGoToPage={goToPage}
              isMobile={isMobile}
              hasFab={hasFab}
              reserveFabSpace={reserveFabSpace}
              hasPlayerFooter={hasSelectedBandFooter}
            />
          )}
          {hasSelectedBandFooter && selectedBandEntry && selectedBandFooterName && selectedBandFooterRoute && (
            <FixedLeaderboardPlayerFooter hasFab={hasFab} reserveFabSpace={reserveFabSpace}>
              {({ className, style }) => (
                <Link to={selectedBandFooterRoute} className={className} style={style}>
                  <LeaderboardEntry
                    rank={selectedBandEntry.rank}
                    displayName={selectedBandFooterName}
                    score={selectedBandEntry.score}
                    season={selectedBandEntry.season}
                    accuracy={selectedBandEntry.accuracy}
                    isFullCombo={!!selectedBandEntry.isFullCombo}
                    stars={selectedBandEntry.stars}
                    difficulty={normalizeSoloDifficulty(selectedBandEntry.difficulty)}
                    showDifficulty={!isMobile}
                    showSeason={!isMobile}
                    showAccuracy={!isMobile}
                    showStars={!isMobile}
                    starsAfterScore
                    scoreWidth={scoreWidth}
                    rankWidth={selectedBandFooterRankWidth}
                    isPlayer
                  />
                </Link>
              )}
            </FixedLeaderboardPlayerFooter>
          )}
        </div>
      )}
    </Page>
  );
}

function normalizeSoloDifficulty(difficulty: number | null | undefined): number | undefined {
  return difficulty != null && difficulty >= 0 && difficulty <= 3 ? difficulty : undefined;
}

function SongBandLeaderboardRow({
  entry,
  isMobile,
  scoreWidth,
  memberScoreWidth,
  showMemberStars,
  showMemberAccuracy,
  activeFilterInstruments,
  style,
  onAnimationEnd,
}: {
  entry: SongBandLeaderboardEntry;
  isMobile: boolean;
  scoreWidth?: string;
  memberScoreWidth?: string;
  showMemberStars: boolean;
  showMemberAccuracy: boolean;
  activeFilterInstruments?: readonly ServerInstrumentKey[];
  style?: CSSProperties;
  onAnimationEnd: (e: AnimationEvent<HTMLElement>) => void;
}) {
  const { t } = useTranslation();
  const playerBandEntry = songBandToPlayerBandEntry(entry, activeFilterInstruments);
  const names = formatPlayerBandNames(playerBandEntry);

  return (
    <PlayerBandCard
      entry={playerBandEntry}
      rank={entry.rank}
      style={style}
      onAnimationEnd={onAnimationEnd}
      ariaLabel={names ? t('bandList.viewBand', { names }) : t('band.title')}
      renderMemberMetadata={(member) => <SongBandMemberMetadata member={member} scoreWidth={memberScoreWidth} showDifficulty={!isMobile} showSeason={!isMobile} showStars={!isMobile && showMemberStars} showAccuracy={!isMobile && showMemberAccuracy} />}
      scoreFooter={<SongBandScoreFooter entry={entry} scoreWidth={scoreWidth} />}
      scoreFooterAriaLabel={t('songDetail.bandScoreFooter', {
        rank: entry.rank,
        score: entry.score.toLocaleString(),
        season: entry.season ?? '-',
        stars: entry.stars ?? '-',
        accuracy: formatSongBandAccuracy(entry.accuracy),
      })}
    />
  );
}

function useStyles() {
  return useMemo(() => ({
    content: {
      ...flexColumn,
      gap: Gap.section,
    } as CSSProperties,
    cardGrid: {
      display: Display.flex,
      flexDirection: 'column',
      gap: Gap.md,
    } as CSSProperties,
  }), []);
}
