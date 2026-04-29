/* eslint-disable react/forbid-dom-props -- page-level dynamic styles use inline style objects */
import { useMemo, type AnimationEvent, type CSSProperties } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useLocation, useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import type { BandSongPerformance, BandType } from '@festival/core/api/serverTypes';
import { Colors, Font, Gap, Radius, flexColumn, frostedCard } from '@festival/theme';
import { api } from '../../../api/client';
import { queryKeys } from '../../../api/queryKeys';
import { useFestival } from '../../../contexts/FestivalContext';
import { Routes } from '../../../routes';
import PlayerSongRow from '../../player/components/PlayerSongRow';
import { topSongsStyles } from '../../player/components/TopSongsSection';
import PlayerSectionHeading from '../../player/sections/PlayerSectionHeading';

export type BandSongsSectionProps = {
  bandType: BandType;
  teamKey: string;
  displayName: string;
  comboId?: string;
  limit?: number;
  style?: CSSProperties;
  onAnimationEnd: (e: AnimationEvent<HTMLElement>) => void;
};

export function useBandSongs(
  bandType: BandType | undefined,
  teamKey: string | undefined,
  limit = 5,
  comboId?: string,
) {
  return useQuery({
    queryKey: queryKeys.bandSongs(bandType ?? '', teamKey ?? '', limit, comboId),
    queryFn: () => api.getBandSongs(bandType!, teamKey!, limit, comboId),
    enabled: !!bandType && !!teamKey,
    staleTime: 5 * 60 * 1000,
  });
}

export default function BandSongsSection({
  bandType,
  teamKey,
  displayName,
  comboId,
  limit = 5,
  style,
  onAnimationEnd,
}: BandSongsSectionProps) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const location = useLocation();
  const styles = useBandSongsStyles();
  const { state } = useFestival();
  const songMap = useMemo(() => new Map(state.songs.map(song => [song.songId, song])), [state.songs]);

  const { data, isLoading } = useBandSongs(bandType, teamKey, limit, comboId);

  const renderSongRow = (performance: BandSongPerformance) => {
    const song = songMap.get(performance.songId);
    return (
      <PlayerSongRow
        key={performance.songId}
        songId={performance.songId}
        href={`#${Routes.songDetail(performance.songId)}`}
        albumArt={song?.albumArt}
        title={song?.title ?? t('band.unknownSong')}
        artist={song?.artist ?? ''}
        year={song?.year}
        percentile={performance.percentile}
        onClick={(e) => {
          e.preventDefault();
          navigate(Routes.songDetail(performance.songId), { state: { backTo: location.pathname } });
        }}
      />
    );
  };

  const renderList = (songs: BandSongPerformance[] | undefined, emptyText: string, isLast = false) => {
    if (isLoading) return <div style={styles.placeholderCard}>{t('common.loading')}</div>;
    if (!songs || songs.length === 0) return <div style={styles.placeholderCard}>{emptyText}</div>;
    return (
      <div style={isLast ? topSongsStyles.songListLast : topSongsStyles.songList}>
        {songs.map(renderSongRow)}
      </div>
    );
  };

  return (
    <section data-testid="band-section-songs" style={{ ...styles.section, ...style }} onAnimationEnd={onAnimationEnd} aria-label={t('band.songs')}>
      <PlayerSectionHeading
        title={t('band.bestSongs')}
        description={t('band.bestSongsDesc', { name: displayName })}
      />
      <div data-testid="band-best-songs">
        {renderList(data?.best, t('band.noBestSongs'))}
      </div>

      <PlayerSectionHeading
        title={t('band.worstSongs')}
        description={t('band.worstSongsDesc', { name: displayName })}
        compact
      />
      <div data-testid="band-worst-songs">
        {renderList(data?.worst, t('band.noWorstSongs'), true)}
      </div>
    </section>
  );
}

function useBandSongsStyles() {
  return useMemo(() => ({
    section: {
      ...flexColumn,
      gap: Gap.md,
    } as CSSProperties,
    placeholderCard: {
      ...frostedCard,
      borderRadius: Radius.md,
      padding: Gap.container,
      color: Colors.textSecondary,
      fontSize: Font.md,
    } as CSSProperties,
  }), []);
}
