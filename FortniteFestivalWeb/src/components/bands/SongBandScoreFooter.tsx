/* eslint-disable react/forbid-dom-props -- shared card footer uses inline theme styles */
import { useMemo, type CSSProperties } from 'react';
import { ACCURACY_SCALE } from '@festival/core';
import type { PlayerBandMember, SongBandLeaderboardEntry } from '@festival/core/api/serverTypes';
import { Align, Colors, Display, Font, FontVariant, Gap, Justify, MetadataSize, StarSize, TextAlign } from '@festival/theme';
import SeasonPill from '../songs/metadata/SeasonPill';
import ScorePill from '../songs/metadata/ScorePill';
import AccuracyDisplay, { formatAccuracyText } from '../songs/metadata/AccuracyDisplay';
import MiniStars from '../songs/metadata/MiniStars';
import DifficultyPill from '../songs/metadata/DifficultyPill';

type SongBandScoreFooterProps = {
  entry: Pick<SongBandLeaderboardEntry, 'season' | 'score' | 'stars' | 'accuracy' | 'isFullCombo'>;
  scoreWidth?: string;
};

export default function SongBandScoreFooter({ entry, scoreWidth }: SongBandScoreFooterProps) {
  const styles = useStyles();

  return (
    <>
      {entry.season != null && <SeasonPill season={entry.season} />}
      <span data-testid="song-band-score-container" style={{ ...styles.score, width: scoreWidth }}>
        <ScorePill score={entry.score} width="100%" />
      </span>
      <span style={styles.stars}>
        {entry.stars != null && entry.stars > 0
          ? <MiniStars starsCount={entry.stars} isFullCombo={!!entry.isFullCombo} />
          : '-'}
      </span>
      <span style={styles.accuracy}>
        <AccuracyDisplay accuracy={entry.accuracy ?? null} isFullCombo={entry.isFullCombo} />
      </span>
    </>
  );
}

export function formatSongBandAccuracy(accuracy: number | null | undefined): string {
  return accuracy != null ? formatAccuracyText(accuracy / ACCURACY_SCALE) : '-';
}

export function getSongBandScoreWidth(entries: readonly Pick<SongBandLeaderboardEntry, 'score'>[]): string | undefined {
  if (entries.length === 0) return undefined;
  const maxScoreLength = entries.reduce((max, entry) => Math.max(max, entry.score.toLocaleString().length), 1);
  return `${maxScoreLength}ch`;
}

export function getSongBandMemberScoreWidth(entries: readonly Pick<SongBandLeaderboardEntry, 'members'>[]): string | undefined {
  const maxScoreLength = entries.reduce((max, entry) => {
    const entryMax = entry.members.reduce((memberMax, member) => (
      member.score != null ? Math.max(memberMax, member.score.toLocaleString().length) : memberMax
    ), 0);
    return Math.max(max, entryMax);
  }, 0);
  return maxScoreLength > 0 ? `${maxScoreLength}ch` : undefined;
}

export function hasSongBandMemberStars(entries: readonly Pick<SongBandLeaderboardEntry, 'members'>[]): boolean {
  return entries.some(entry => entry.members.some(member => member.stars != null));
}

export function hasSongBandMemberAccuracy(entries: readonly Pick<SongBandLeaderboardEntry, 'members'>[]): boolean {
  return entries.some(entry => entry.members.some(member => member.accuracy != null));
}

type SongBandMemberMetadataProps = {
  member: Pick<PlayerBandMember, 'difficulty' | 'season' | 'score' | 'stars' | 'accuracy' | 'isFullCombo'>;
  scoreWidth?: string;
  showStars?: boolean;
  showAccuracy?: boolean;
};

export function SongBandMemberMetadata({ member, scoreWidth, showStars, showAccuracy }: SongBandMemberMetadataProps) {
  const styles = useStyles();
  const showScore = scoreWidth != null;
  const hasMetadata = member.difficulty != null || member.season != null || showScore || showStars || showAccuracy;
  if (!hasMetadata) return null;

  return (
    <span data-testid="song-band-member-metadata" style={styles.memberMetadata}>
      {member.difficulty != null && member.difficulty >= 0 && <DifficultyPill difficulty={member.difficulty} />}
      {member.season != null && <SeasonPill season={member.season} />}
      {showScore && (
        <span data-testid="song-band-member-score-container" style={{ ...styles.score, width: scoreWidth }}>
          {member.score != null ? <ScorePill score={member.score} width="100%" /> : '-'}
        </span>
      )}
      {showStars && (
        <span data-testid="song-band-member-stars-container" style={styles.stars}>
          {member.stars != null && member.stars > 0
            ? <MiniStars starsCount={member.stars} isFullCombo={!!member.isFullCombo} />
            : '-'}
        </span>
      )}
      {showAccuracy && (
        <span data-testid="song-band-member-accuracy-container" style={styles.memberAccuracy}>
          {member.accuracy != null
            ? <AccuracyDisplay accuracy={member.accuracy} isFullCombo={member.isFullCombo} />
            : '-'}
        </span>
      )}
    </span>
  );
}

function useStyles() {
  return useMemo(() => ({
    stars: {
      flexShrink: 0,
      width: StarSize.rowWidth,
      display: Display.inlineFlex,
      alignItems: Align.center,
      justifyContent: Justify.center,
    } as CSSProperties,
    score: {
      display: Display.inlineFlex,
      alignItems: Align.center,
      justifyContent: Justify.end,
      flexShrink: 0,
      padding: `0 ${Gap.sm}px`,
      boxSizing: 'content-box',
    } as CSSProperties,
    memberMetadata: {
      display: Display.inlineFlex,
      alignItems: Align.center,
      justifyContent: Justify.end,
      gap: Gap.md,
      flexShrink: 0,
    } as CSSProperties,
    accuracy: {
      width: '5.5ch',
      flexShrink: 0,
      textAlign: TextAlign.center,
      fontWeight: 600,
      fontSize: Font.md,
      color: Colors.accentBlueBright,
      fontVariantNumeric: FontVariant.tabularNums,
    } as CSSProperties,
    memberAccuracy: {
      width: MetadataSize.accuracyPillMinWidth,
      flexShrink: 0,
      display: Display.inlineFlex,
      alignItems: Align.center,
      justifyContent: Justify.center,
    } as CSSProperties,
  }), []);
}
