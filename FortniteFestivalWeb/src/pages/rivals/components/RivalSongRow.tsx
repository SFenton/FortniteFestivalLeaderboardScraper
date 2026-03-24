import { memo } from 'react';
import { useTranslation } from 'react-i18next';
import type { RivalSongComparison, ServerInstrumentKey } from '@festival/core/api/serverTypes';
import { InstrumentIcon } from '../../../components/display/InstrumentIcons';
import s from './RivalSongRow.module.css';

interface RivalSongRowProps {
  song: RivalSongComparison;
  albumArt?: string;
  year?: number;
  playerName?: string;
  rivalName?: string;
  onClick: () => void;
  /** Render as a standalone frosted card row (like SongsPage) instead of a borderless row inside a card. */
  standalone?: boolean;
  /** Pre-computed width for the score diff pill so all rows align. */
  scoreDeltaWidth?: string;
  style?: React.CSSProperties;
  onAnimationEnd?: (e: React.AnimationEvent<HTMLElement>) => void;
}

const RivalSongRow = memo(function RivalSongRow({ song, albumArt, year, playerName, rivalName, onClick, standalone, scoreDeltaWidth, style, onAnimationEnd }: RivalSongRowProps) {
  const { t } = useTranslation();
  const delta = song.rankDelta;
  const deltaClass = delta > 0 ? s.deltaPositive : delta < 0 ? s.deltaNegative : s.deltaNeutral;
  const deltaSign = delta > 0 ? '+' : '';
  const userWins = delta > 0;
  const rivalWins = delta < 0;
  const scoreDiff = (song.userScore ?? 0) - (song.rivalScore ?? 0);
  const scoreDiffText = `${scoreDiff >= 0 ? '+' : '\u2212'}${Math.abs(scoreDiff).toLocaleString()}`;
  const scoreDiffClass = scoreDiff > 0 ? s.deltaPositive : scoreDiff < 0 ? s.deltaNegative : s.deltaNeutral;

  if (standalone) {
    const tintClass = userWins ? s.rowWinning : rivalWins ? s.rowLosing : '';
    return (
      <div
        className={`${s.rowStandalone} ${tintClass}`}
        onClick={onClick}
        role="button"
        tabIndex={0}
        onKeyDown={e => { if (e.key === 'Enter') onClick(); }}
        style={style}
        onAnimationEnd={onAnimationEnd}
      >
        <div className={s.topRow}>
          {albumArt ? (
            <img className={s.art} src={albumArt} alt="" loading="lazy" />
          ) : (
            <div className={s.artPlaceholder} />
          )}
          <div className={s.songInfo}>
            <div className={s.songTitle}>{song.title ?? song.songId}</div>
            <div className={s.songArtist}>{song.artist ?? ''}{year ? ` \u00b7 ${year}` : ''}</div>
          </div>
          <span className={deltaClass}>
            {deltaSign}{delta}
          </span>
          <span className={scoreDiffClass} style={scoreDeltaWidth ? { minWidth: scoreDeltaWidth } : undefined}>
            {scoreDiffText}
          </span>
          <InstrumentIcon instrument={song.instrument as ServerInstrumentKey} size={36} />
        </div>
        <div className={s.compareRow}>
          <div className={`${s.entry} ${userWins ? s.entryWin : ''}`}>
            <span className={s.entryName}>{playerName ?? t('rivals.detail.you')}</span>
            <span className={s.entryRank}>#{song.userRank.toLocaleString()}</span>
            <span className={s.entryScore}>{song.userScore != null ? song.userScore.toLocaleString() : ''}</span>
          </div>
          <div className={`${s.entryRight} ${rivalWins ? s.entryWin : ''}`}>
            <span className={s.entryName}>{rivalName ?? t('rivals.detail.them')}</span>
            <span className={s.entryRank}>#{song.rivalRank.toLocaleString()}</span>
            <span className={s.entryScore}>{song.rivalScore != null ? song.rivalScore.toLocaleString() : ''}</span>
          </div>
        </div>
      </div>
    );
  }

  // Inline row inside a card (no second row)
  return (
    <div
      className={s.row}
      onClick={onClick}
      role="button"
      tabIndex={0}
      onKeyDown={e => { if (e.key === 'Enter') onClick(); }}
      style={style}
      onAnimationEnd={onAnimationEnd}
    >
      {albumArt ? (
        <img className={s.art} src={albumArt} alt="" loading="lazy" />
      ) : (
        <div className={s.artPlaceholder} />
      )}
      <div className={s.songInfo}>
        <div className={s.songTitle}>{song.title ?? song.songId}</div>
        <div className={s.songArtist}>{song.artist ?? ''}{year ? ` \u00b7 ${year}` : ''}</div>
      </div>
      <div className={s.scores}>
        <div className={s.scoreColumn}>
          <span className={s.scoreLabel}>{t('rivals.detail.you')}</span>
          <span className={s.scoreRank}>#{song.userRank}</span>
          {song.userScore != null && (
            <span className={s.scoreValue}>{song.userScore.toLocaleString()}</span>
          )}
        </div>
        <div className={s.scoreColumn}>
          <span className={s.scoreLabel}>{t('rivals.detail.them')}</span>
          <span className={s.scoreRank}>#{song.rivalRank}</span>
          {song.rivalScore != null && (
            <span className={s.scoreValue}>{song.rivalScore.toLocaleString()}</span>
          )}
        </div>
        <span className={deltaClass}>
          {deltaSign}{delta}
        </span>
      </div>
      <InstrumentIcon instrument={song.instrument as ServerInstrumentKey} size={36} />
    </div>
  );
});

export default RivalSongRow;
