/**
 * Top/bottom songs per instrument section.
 * Returns Item[] for ONE instrument (top 5 + optional bottom 5).
 */
import { type ServerInstrumentKey as InstrumentKey, type ServerSong as Song, type PlayerScore, serverInstrumentLabel as instrumentLabel } from '@festival/core/api/serverTypes';
import { Layout, Gap, flexColumn } from '@festival/theme';
import type { CSSProperties } from 'react';
import PlayerSectionHeading from '../sections/PlayerSectionHeading';
import PlayerSongRow from './PlayerSongRow';
import type { PlayerItem, NavigateToSongDetail } from '../helpers/playerPageTypes';
import InstrumentEmptyState from '../sections/InstrumentEmptyState';

export function buildTopSongsItems(
  t: (key: string, opts?: Record<string, unknown>) => string,
  inst: InstrumentKey,
  scores: PlayerScore[],
  songMap: Map<string, Song>,
  displayName: string,
  navigateToSongDetail: NavigateToSongDetail,
  isLast?: boolean,
): PlayerItem[] {
  const withPct = scores.filter((s) => s.rank > 0 && (s.totalEntries ?? 0) > 0);

  const items: PlayerItem[] = [];

  // Top songs header (always rendered so every visible instrument is represented)
  items.push({
    key: `top-hdr-${inst}`,
    span: true,
    heightEstimate: Layout.sectionHeadingHeight,
    node: (
      <PlayerSectionHeading
        title={t('player.topFiveSongs')}
        description={t('player.topSongsDesc', { name: displayName, instrument: instrumentLabel(inst) })}
        instrument={inst}
      />
    ),
  });

  // Empty state — no ranked scores for this instrument
  if (withPct.length === 0) {
    items.push({
      key: `top-empty-${inst}`,
      span: true,
      heightEstimate: 150,
      node: <InstrumentEmptyState instrument={inst} t={t} />,
    });
    return items;
  }

  const sorted = withPct.slice().sort((a, b) => a.rank / a.totalEntries! - b.rank / b.totalEntries!);
  const topScores = sorted.slice(0, 5);
  const bottomScores = sorted.length > 5 ? sorted.slice(-5).reverse() : [];

  /* v8 ignore start — renderSongRow: navigation callbacks + defensive branches */
  const renderSongRow = (sc: PlayerScore) => {
    const song = songMap.get(sc.songId);
    const pct = sc.rank > 0 && (sc.totalEntries ?? 0) > 0
      ? Math.min((sc.rank / sc.totalEntries!) * 100, 100)
      : undefined;
    return (
      <PlayerSongRow
        key={sc.songId}
        songId={sc.songId}
        href={`#/songs/${sc.songId}?instrument=${encodeURIComponent(inst)}`}
        albumArt={song?.albumArt}
        title={song?.title ?? sc.songId.slice(0, 8)}
        artist={song?.artist ?? ''}
        year={song?.year}
        percentile={pct}
        onClick={(e) => {
          e.preventDefault();
          navigateToSongDetail(sc.songId, inst, { autoScroll: true });
        }}
      />
    );
  };
  /* v8 ignore stop */

  const noBottom = bottomScores.length === 0;

  // Top songs table
  items.push({
    key: `top-songs-${inst}`,
    span: true,
    heightEstimate: topScores.length * Layout.songRowHeight,
    node: (
      <div style={(isLast && noBottom) ? topSongsStyles.songListLast : topSongsStyles.songList}>
        {topScores.map((sc) => renderSongRow(sc))}
      </div>
    ),
  });

  if (bottomScores.length > 0) {
    // Bottom songs header
    items.push({
      key: `bot-hdr-${inst}`,
      span: true,
      heightEstimate: Layout.sectionHeadingHeight,
      node: (
        <PlayerSectionHeading
          title={t('player.bottomFiveSongs')}
          description={t('player.bottomSongsDesc', { name: displayName, instrument: instrumentLabel(inst) })}
          instrument={inst}
          compact
        />
      ),
    });

    // Bottom songs table
    items.push({
      key: `bot-songs-${inst}`,
      span: true,
      heightEstimate: bottomScores.length * Layout.songRowHeight,
      node: (
        <div style={isLast ? topSongsStyles.songListLast : topSongsStyles.songList}>
          {bottomScores.map((sc) => renderSongRow(sc))}
        </div>
      ),
    });
  }

  return items;
}

/** Static styles for top-songs list containers. Exported for reuse by TopSongsDemo. */
export const topSongsStyles = {
  songList: {
    ...flexColumn,
    gap: Gap.sm,
    marginBottom: Gap.section,
  } as CSSProperties,
  songListLast: {
    ...flexColumn,
    gap: Gap.sm,
    marginBottom: Gap.none,
  } as CSSProperties,
};
