/* eslint-disable react/forbid-dom-props -- page-level dynamic styles use inline style objects */
import { useCallback, useEffect, useMemo, type AnimationEvent, type CSSProperties } from 'react';
import { useParams, useSearchParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { LoadPhase } from '@festival/core';
import type { SongBandLeaderboardEntry } from '@festival/core/api/serverTypes';
import { Display, Gap, flexColumn } from '@festival/theme';
import { api } from '../../../api/client';
import { queryKeys } from '../../../api/queryKeys';
import EmptyState from '../../../components/common/EmptyState';
import { FixedLeaderboardPagination, useLeaderboardFooterScrollMargin } from '../../../components/leaderboard/LeaderboardPaginationFooter';
import SongInfoHeader from '../../../components/songs/headers/SongInfoHeader';
import SongBandScoreFooter, { SongBandMemberMetadata, formatSongBandAccuracy, getSongBandMemberScoreWidth, getSongBandScoreWidth, hasSongBandMemberAccuracy, hasSongBandMemberStars } from '../../../components/bands/SongBandScoreFooter';
import { useFestival } from '../../../contexts/FestivalContext';
import { useIsMobile, useIsMobileChrome } from '../../../hooks/ui/useIsMobile';
import { usePageTransition } from '../../../hooks/ui/usePageTransition';
import { useStagger } from '../../../hooks/ui/useStagger';
import { useNavigateToSongDetail } from '../../../hooks/navigation/useNavigateToSongDetail';
import { useScrollContainer } from '../../../contexts/ScrollContainerContext';
import { useAppliedBandComboFilter } from '../../../contexts/BandFilterActionContext';
import { parseApiError } from '../../../utils/apiError';
import { coerceSongBandType, songBandToPlayerBandEntry, songBandTypeLabel } from '../../../utils/songBandLeaderboards';
import Page, { PageBackground } from '../../Page';
import { PageMessage } from '../../PageMessage';
import PlayerBandCard, { formatPlayerBandNames } from '../../player/components/PlayerBandCard';

const PAGE_SIZE = 25;

export default function SongBandLeaderboardPage() {
  const { t } = useTranslation();
  const { songId = '', bandType: rawBandType } = useParams<{ songId: string; bandType: string }>();
  const [searchParams, setSearchParams] = useSearchParams();
  const scrollContainerRef = useScrollContainer();
  const isMobile = useIsMobile();
  const hasFab = useIsMobileChrome();
  const {
    state: { songs },
  } = useFestival();

  const song = songs.find((s) => s.songId === songId);
  const bandType = coerceSongBandType(rawBandType);
  const appliedBandComboFilter = useAppliedBandComboFilter();
  const activeComboId = appliedBandComboFilter && appliedBandComboFilter.bandType === bandType ? appliedBandComboFilter.comboId : undefined;
  const bandLabel = bandType ? songBandTypeLabel(bandType, t) : '';
  const page = Math.max(1, Number(searchParams.get('page')) || 1);
  const goToSongDetail = useNavigateToSongDetail(songId);

  const leaderboardQuery = useQuery({
    queryKey: queryKeys.songBandLeaderboard(songId, bandType ?? 'unknown', PAGE_SIZE, (page - 1) * PAGE_SIZE, undefined, undefined, activeComboId),
    queryFn: () => api.getSongBandLeaderboard(songId, bandType!, PAGE_SIZE, (page - 1) * PAGE_SIZE, undefined, undefined, activeComboId),
    enabled: !!songId && !!bandType,
    staleTime: 5 * 60_000,
  });

  const data = leaderboardQuery.data;
  const loading = leaderboardQuery.isFetching && !data;
  const entries = data?.entries ?? [];
  const scoreWidth = useMemo(() => getSongBandScoreWidth(entries), [entries]);
  const memberScoreWidth = useMemo(() => getSongBandMemberScoreWidth(entries), [entries]);
  const showMemberStars = useMemo(() => hasSongBandMemberStars(entries), [entries]);
  const showMemberAccuracy = useMemo(() => hasSongBandMemberAccuracy(entries), [entries]);
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

  const { phase, shouldStagger } = usePageTransition(`songBandLeaderboard:${songId}:${bandType}:${activeComboId ?? 'all'}:${page}`, !loading);
  const { forIndex: stagger, clearAnim } = useStagger(shouldStagger);
  useLeaderboardFooterScrollMargin({ hasFab, hasPagination });
  const styles = useStyles();

  if (!songId || !bandType) {
    return <PageMessage>{t('songBandLeaderboard.notFound')}</PageMessage>;
  }

  const subtitle = data
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
          collapsed={isMobile}
          animate={!isMobile}
          hideBackground
          onTitleClick={goToSongDetail}
          subtitle2={subtitle}
        />
      )}
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
                  scoreWidth={scoreWidth}
                  memberScoreWidth={memberScoreWidth}
                  showMemberStars={showMemberStars}
                  showMemberAccuracy={showMemberAccuracy}
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
            />
          )}
        </div>
      )}
    </Page>
  );
}

function SongBandLeaderboardRow({
  entry,
  scoreWidth,
  memberScoreWidth,
  showMemberStars,
  showMemberAccuracy,
  style,
  onAnimationEnd,
}: {
  entry: SongBandLeaderboardEntry;
  scoreWidth?: string;
  memberScoreWidth?: string;
  showMemberStars: boolean;
  showMemberAccuracy: boolean;
  style?: CSSProperties;
  onAnimationEnd: (e: AnimationEvent<HTMLElement>) => void;
}) {
  const { t } = useTranslation();
  const playerBandEntry = songBandToPlayerBandEntry(entry);
  const names = formatPlayerBandNames(playerBandEntry);

  return (
    <PlayerBandCard
      entry={playerBandEntry}
      rank={entry.rank}
      style={style}
      onAnimationEnd={onAnimationEnd}
      ariaLabel={names ? t('bandList.viewBand', { names }) : t('band.title')}
      renderMemberMetadata={(member) => <SongBandMemberMetadata member={member} scoreWidth={memberScoreWidth} showStars={showMemberStars} showAccuracy={showMemberAccuracy} />}
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
