/**
 * Top/bottom songs per instrument section.
 * Returns Item[] for ONE instrument (top 5 + optional bottom 5).
 */
import { type ServerInstrumentKey as InstrumentKey, type ServerSong as Song, type PlayerScore, serverInstrumentLabel as instrumentLabel } from '@festival/core/api/serverTypes';
import { Gap } from '@festival/theme';
import PlayerSectionHeading from '../sections/PlayerSectionHeading';
import PlayerSongRow from './PlayerSongRow';
import type { PlayerItem, NavigateToSongDetail } from '../helpers/playerPageTypes';

export function buildTopSongsItems(
  t: (key: string, opts?: Record<string, unknown>) => string,
  inst: InstrumentKey,
  scores: PlayerScore[],
  songMap: Map<string, Song>,
  displayName: string,
  navigateToSongDetail: NavigateToSongDetail,
): PlayerItem[] {
  const withPct = scores.filter((s) => s.rank > 0 && (s.totalEntries ?? 0) > 0);
  if (withPct.length === 0) return [];

  const items: PlayerItem[] = [];
  const sorted = withPct.slice().sort((a, b) => a.rank / a.totalEntries! - b.rank / b.totalEntries!);
  const topScores = sorted.slice(0, 5);
  const bottomScores = sorted.length > 5 ? sorted.slice(-5).reverse() : [];

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
          /* v8 ignore start */
          e.preventDefault();
          navigateToSongDetail(sc.songId, inst, { autoScroll: true });
          /* v8 ignore stop */
        }}
      />
    );
  };

  // Top songs header
  items.push({
    key: `top-hdr-${inst}`,
    span: true,
    heightEstimate: 64,
    node: (
      <PlayerSectionHeading
        title={t('player.topFiveSongs')}
        description={t('player.topSongsDesc', { name: displayName, instrument: instrumentLabel(inst) })}
        instrument={inst}
      />
    ),
  });

  // Top songs table
  items.push({
    key: `top-songs-${inst}`,
    span: true,
    heightEstimate: topScores.length * 72,
    node: (
      <div style={{ display: 'flex', flexDirection: 'column', gap: Gap.sm, marginBottom: Gap.section }}>
        {topScores.map((sc) => renderSongRow(sc))}
      </div>
    ),
  });

  if (bottomScores.length > 0) {
    // Bottom songs header
    items.push({
      key: `bot-hdr-${inst}`,
      span: true,
      heightEstimate: 64,
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
      heightEstimate: bottomScores.length * 72,
      node: (
        <div style={{ display: 'flex', flexDirection: 'column', gap: Gap.sm, marginBottom: Gap.section }}>
          {bottomScores.map((sc) => renderSongRow(sc))}
        </div>
      ),
    });
  }

  return items;
}
