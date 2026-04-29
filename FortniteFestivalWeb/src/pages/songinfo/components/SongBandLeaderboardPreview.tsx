/* eslint-disable react/forbid-dom-props -- song detail cards use inline theme styles */
import { useMemo, type CSSProperties } from 'react';
import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { IoChevronForward } from 'react-icons/io5';
import type { PlayerBandType, SongBandLeaderboardEntry } from '@festival/core/api/serverTypes';
import type { SongBandData } from '../../../api/pageCache';
import { Align, Border, Colors, Cursor, Display, Font, Gap, Justify, Layout, Opacity, Radius, STAGGER_ENTRY_OFFSET, STAGGER_ROW_MS, TRANSITION_MS, Weight, border, flexColumn, flexRow, frostedCard, padding } from '@festival/theme';
import PlayerBandCard, { formatPlayerBandNames } from '../../player/components/PlayerBandCard';
import SongBandScoreFooter, { SongBandMemberMetadata, formatSongBandAccuracy, getSongBandMemberScoreWidth, getSongBandScoreWidth, hasSongBandMemberAccuracy, hasSongBandMemberStars } from '../../../components/bands/SongBandScoreFooter';
import { Routes } from '../../../routes';
import { parseApiError } from '../../../utils/apiError';
import { songBandToPlayerBandEntry, songBandTypeLabel } from '../../../utils/songBandLeaderboards';

type SongBandLeaderboardPreviewProps = {
  songId: string;
  bandType: PlayerBandType;
  data: SongBandData;
  selectedAccountId?: string;
  baseDelay: number;
  skipAnimation?: boolean;
};

export default function SongBandLeaderboardPreview({
  songId,
  bandType,
  data,
  selectedAccountId,
  baseDelay,
  skipAnimation,
}: SongBandLeaderboardPreviewProps) {
  const { t } = useTranslation();
  const styles = useStyles();
  const title = songBandTypeLabel(bandType, t);
  const selectedEntry = data.selectedBandEntry ?? data.selectedPlayerEntry ?? null;
  const selectedInTop = !!selectedEntry && data.entries.some(entry => isSameSongBandEntry(entry, selectedEntry));
  const showSelectedRow = !!selectedEntry && !selectedInTop;
  const measuredEntries = useMemo(() => selectedEntry ? [...data.entries, selectedEntry] : data.entries, [data.entries, selectedEntry]);
  const hasEntries = data.entries.length > 0 || showSelectedRow;
  const scoreWidth = useMemo(() => getSongBandScoreWidth(measuredEntries), [measuredEntries]);
  const memberScoreWidth = useMemo(() => getSongBandMemberScoreWidth(measuredEntries), [measuredEntries]);
  const showMemberStars = useMemo(() => hasSongBandMemberStars(measuredEntries), [measuredEntries]);
  const showMemberAccuracy = useMemo(() => hasSongBandMemberAccuracy(measuredEntries), [measuredEntries]);
  const hasCounts = data.totalEntries != null && data.localEntries != null && data.totalEntries > 0;
  const viewAllLabel = hasCounts
    ? t('leaderboard.viewFullWithCounts', {
        local: data.localEntries!.toLocaleString(),
        total: data.totalEntries!.toLocaleString(),
      })
    : t('leaderboard.viewPlain');

  const anim = (delayMs: number): CSSProperties => skipAnimation ? {} : ({
    opacity: Opacity.none,
    animation: `fadeInUp ${TRANSITION_MS}ms ease-out ${delayMs}ms forwards`,
  });
  const clearAnim = (ev: React.AnimationEvent<HTMLElement>) => {
    ev.currentTarget.style.opacity = '';
    ev.currentTarget.style.animation = '';
  };

  return (
    <section style={styles.section} data-testid={`song-band-preview-${bandType}`}>
      <div style={{ ...styles.header, ...anim(baseDelay) }} onAnimationEnd={clearAnim}>
        <h2 style={styles.title}>{title}</h2>
      </div>

      {data.error && (
        <div style={{ ...styles.messageCard, ...anim(baseDelay + STAGGER_ENTRY_OFFSET) }} onAnimationEnd={clearAnim}>
          {parseApiError(data.error).title}
        </div>
      )}

      {!data.error && !hasEntries && (
        <div style={{ ...styles.messageCard, ...anim(baseDelay + STAGGER_ENTRY_OFFSET) }} onAnimationEnd={clearAnim}>
          {t('songDetail.noBandScores')}
        </div>
      )}

      {!data.error && hasEntries && (
        <div data-testid={`song-band-preview-list-${bandType}`} style={styles.cardGrid}>
          {data.entries.map((entry, index) => {
            const names = formatPlayerBandNames(songBandToPlayerBandEntry(entry));
            return (
              <PlayerBandCard
                key={`${entry.bandType}:${entry.teamKey}:${entry.rank}`}
                entry={songBandToPlayerBandEntry(entry)}
                sourceAccountId={entryHasAccount(entry, selectedAccountId) ? selectedAccountId : undefined}
                rank={entry.rank}
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
                style={anim(baseDelay + STAGGER_ENTRY_OFFSET + index * STAGGER_ROW_MS)}
                onAnimationEnd={clearAnim}
              />
            );
          })}
          {showSelectedRow && selectedEntry && (() => {
            const playerBandEntry = songBandToPlayerBandEntry(selectedEntry);
            const names = formatPlayerBandNames(playerBandEntry);
            return (
              <PlayerBandCard
                key={`${selectedEntry.bandType}:${selectedEntry.teamKey}:selected`}
                testId={`song-band-selected-entry-${bandType}`}
                entry={playerBandEntry}
                sourceAccountId={selectedAccountId}
                rank={selectedEntry.rank}
                ariaLabel={names ? t('bandList.viewBand', { names }) : t('band.title')}
                renderMemberMetadata={(member) => <SongBandMemberMetadata member={member} scoreWidth={memberScoreWidth} showStars={showMemberStars} showAccuracy={showMemberAccuracy} />}
                scoreFooter={<SongBandScoreFooter entry={selectedEntry} scoreWidth={scoreWidth} />}
                scoreFooterAriaLabel={t('songDetail.bandScoreFooter', {
                  rank: selectedEntry.rank,
                  score: selectedEntry.score.toLocaleString(),
                  season: selectedEntry.season ?? '-',
                  stars: selectedEntry.stars ?? '-',
                  accuracy: formatSongBandAccuracy(selectedEntry.accuracy),
                })}
                style={{ ...styles.selectedCard, ...anim(baseDelay + STAGGER_ENTRY_OFFSET + data.entries.length * STAGGER_ROW_MS) }}
                onAnimationEnd={clearAnim}
              />
            );
          })()}
          <Link
            to={Routes.songBandLeaderboard(songId, bandType)}
            style={{ ...styles.viewAllCard, ...anim(baseDelay + STAGGER_ENTRY_OFFSET + (data.entries.length + (showSelectedRow ? 1 : 0)) * STAGGER_ROW_MS) }}
            onAnimationEnd={clearAnim}
          >
            <span>{viewAllLabel}</span>
            <IoChevronForward aria-hidden="true" size={18} style={styles.entryChevron} />
          </Link>
        </div>
      )}
    </section>
  );
}

function isSameSongBandEntry(a: SongBandLeaderboardEntry, b: SongBandLeaderboardEntry): boolean {
  return (!!a.bandId && a.bandId === b.bandId) || (a.bandType === b.bandType && a.teamKey === b.teamKey);
}

function entryHasAccount(entry: SongBandLeaderboardEntry, accountId: string | undefined): boolean {
  return !!accountId && entry.members.some(member => member.accountId === accountId);
}

function useStyles() {
  return useMemo(() => ({
    section: {
      ...flexColumn,
      gap: Gap.md,
    } as CSSProperties,
    header: {
      ...flexRow,
      alignItems: Align.center,
      minHeight: Layout.entryRowHeight,
    } as CSSProperties,
    title: {
      margin: 0,
      color: Colors.textPrimary,
      fontSize: Font.xl,
      fontWeight: Weight.bold,
    } as CSSProperties,
    cardGrid: {
      ...flexColumn,
      gap: Gap.md,
    } as CSSProperties,
    messageCard: {
      ...frostedCard,
      borderRadius: Radius.md,
      minHeight: Layout.entryRowHeight,
      padding: padding(Gap.sm, Gap.lg),
      color: Colors.textSecondary,
      fontSize: Font.md,
      display: Display.flex,
      alignItems: Align.center,
      justifyContent: Justify.center,
      textAlign: 'center',
    } as CSSProperties,
    viewAllCard: {
      ...frostedCard,
      ...flexRow,
      alignItems: Align.center,
      justifyContent: Justify.center,
      gap: Gap.sm,
      minHeight: Layout.entryRowHeight,
      padding: padding(Gap.sm, Gap.md),
      borderRadius: Radius.md,
      color: Colors.textPrimary,
      fontSize: Font.md,
      fontWeight: Weight.semibold,
      textDecoration: 'none',
      cursor: Cursor.pointer,
    } as CSSProperties,
    selectedCard: {
      backgroundColor: Colors.purpleHighlight,
      border: border(Border.thin, Colors.purpleHighlightBorder),
    } as CSSProperties,
    entryChevron: {
      flexShrink: 0,
      color: Colors.textSubtle,
    } as CSSProperties,
  }), []);
}
