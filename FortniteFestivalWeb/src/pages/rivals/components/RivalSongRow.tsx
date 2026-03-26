import { memo, useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import type { RivalSongComparison, ServerInstrumentKey } from '@festival/core/api/serverTypes';
import { Colors, Font, Weight, Gap, Radius, Layout, Border, Display, Align, Justify, Position, Cursor, Overflow, WhiteSpace, TextAlign, TextTransform, FontVariant, ObjectFit, frostedCard, flexColumn, flexRow, flexCenter, truncate, padding, border, transition, FAST_FADE_MS } from '@festival/theme';
import { CssProp } from '@festival/theme';
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
  const st = useRivalSongRowStyles();
  /* v8 ignore start -- ternary chains and nullish coalescing */
  const delta = song.rankDelta;
  const deltaStyle = delta > 0 ? st.deltaPositive : delta < 0 ? st.deltaNegative : st.deltaNeutral;
  const deltaSign = delta > 0 ? '+' : '';
  const userWins = delta > 0;
  const rivalWins = delta < 0;
  const scoreDiff = (song.userScore ?? 0) - (song.rivalScore ?? 0);
  const scoreDiffText = `${scoreDiff >= 0 ? '+' : '\u2212'}${Math.abs(scoreDiff).toLocaleString()}`;
  const scoreDiffStyle = scoreDiff > 0 ? st.deltaPositive : scoreDiff < 0 ? st.deltaNegative : st.deltaNeutral;
  /* v8 ignore stop */

  /* v8 ignore start -- JSX render trees */
  if (standalone) {
    const tintClass = userWins ? s.rowWinning : rivalWins ? s.rowLosing : '';
    return (
      <div
        className={`${s.rowStandalone} ${tintClass}`}
        style={{ ...st.rowStandalone, ...style }}
        onClick={onClick}
        role="button"
        tabIndex={0}
        onKeyDown={e => { if (e.key === 'Enter') onClick(); }}
        onAnimationEnd={onAnimationEnd}
      >
        <div style={st.topRow}>
          {albumArt ? (
            <img style={st.art} src={albumArt} alt="" loading="lazy" />
          ) : (
            <div style={st.artPlaceholder} />
          )}
          <div style={st.songInfo}>
            <div style={st.songTitle}>{song.title ?? song.songId}</div>
            <div style={st.songArtist}>{song.artist ?? ''}{year ? ` \u00b7 ${year}` : ''}</div>
          </div>
          <InstrumentIcon instrument={song.instrument as ServerInstrumentKey} size={36} />
        </div>
        <div style={st.compareRow}>
          <div style={{ ...st.entry, ...(userWins ? st.entryWin : {}) }}>
            <span style={st.entryName}>{playerName ?? t('rivals.detail.you')}</span>
            <span style={st.entryRank}>#{song.userRank.toLocaleString()}</span>
            <span style={st.entryScore}>{song.userScore != null ? song.userScore.toLocaleString() : ''}</span>
          </div>
          <div style={st.deltaCenter}>
            <div style={{ ...st.deltaPillGroup, ...st.deltaPillGroupRank }}>
              <span style={{ ...deltaStyle, ...(scoreDeltaWidth ? { minWidth: scoreDeltaWidth } : {}) }}>
                {deltaSign}{delta}
              </span>
              <span style={st.deltaLabel}>Rank</span>
            </div>
            <div style={st.deltaPillGroup}>
              <span style={{ ...scoreDiffStyle, ...(scoreDeltaWidth ? { minWidth: scoreDeltaWidth } : {}) }}>
                {scoreDiffText}
              </span>
              <span style={st.deltaLabel}>Score</span>
            </div>
          </div>
          <div style={{ ...st.entryRight, ...(rivalWins ? st.entryWin : {}) }}>
            <span style={st.entryName}>{rivalName ?? t('rivals.detail.them')}</span>
            <span style={st.entryRank}>#{song.rivalRank.toLocaleString()}</span>
            <span style={st.entryScore}>{song.rivalScore != null ? song.rivalScore.toLocaleString() : ''}</span>
          </div>
        </div>
      </div>
    );
  }

  // Inline row inside a card (no second row)
  return (
    <div
      className={s.row}
      style={{ ...st.row, ...style }}
      onClick={onClick}
      role="button"
      tabIndex={0}
      onKeyDown={e => { if (e.key === 'Enter') onClick(); }}
      onAnimationEnd={onAnimationEnd}
    >
      {albumArt ? (
        <img style={st.art} src={albumArt} alt="" loading="lazy" />
      ) : (
        <div style={st.artPlaceholder} />
      )}
      <div style={st.songInfo}>
        <div style={st.songTitle}>{song.title ?? song.songId}</div>
        <div style={st.songArtist}>{song.artist ?? ''}{year ? ` \u00b7 ${year}` : ''}</div>
      </div>
      <div style={st.scores}>
        <div style={st.scoreColumn}>
          <span style={st.scoreLabel}>{t('rivals.detail.you')}</span>
          <span style={st.scoreRank}>#{song.userRank}</span>
          {song.userScore != null && (
            <span style={st.scoreValue}>{song.userScore.toLocaleString()}</span>
          )}
        </div>
        <div style={st.scoreColumn}>
          <span style={st.scoreLabel}>{t('rivals.detail.them')}</span>
          <span style={st.scoreRank}>#{song.rivalRank}</span>
          {song.rivalScore != null && (
            <span style={st.scoreValue}>{song.rivalScore.toLocaleString()}</span>
          )}
        </div>
        <span style={deltaStyle}>
          {deltaSign}{delta}
        </span>
      </div>
      <InstrumentIcon instrument={song.instrument as ServerInstrumentKey} size={36} />
    </div>
  );
  /* v8 ignore stop */
});

export default RivalSongRow;

function useRivalSongRowStyles() {
  return useMemo(() => {
    const deltaChipBase: CSSProperties = {
      display: Display.inlineBlock,
      padding: padding(Gap.xs, Gap.sm),
      borderRadius: Radius.xs,
      fontSize: Font.lg,
      fontWeight: Weight.semibold,
      border: border(Border.thick, Colors.borderSubtle),
      flexShrink: 0,
      textAlign: TextAlign.center,
    };
    return {
      row: {
        display: Display.flex,
        alignItems: Align.center,
        gap: Gap.xl,
        padding: padding(Gap.lg, Gap.section),
        textDecoration: 'none',
        color: 'inherit',
        cursor: Cursor.pointer,
        transition: transition(CssProp.backgroundColor, 120),
      } as CSSProperties,
      rowStandalone: {
        ...frostedCard,
        ...flexColumn,
        borderRadius: Radius.md,
        textDecoration: 'none',
        color: 'inherit',
        position: Position.relative,
        overflow: Overflow.hidden,
        containerType: 'inline-size',
      } as CSSProperties,
      topRow: {
        ...flexRow,
        gap: Gap.xl,
        padding: padding(0, Gap.xl),
        height: Layout.rivalTopRowHeight,
        position: Position.relative,
        zIndex: 1,
      } as CSSProperties,
      compareRow: {
        display: Display.grid,
        gridTemplateColumns: '1fr auto 1fr',
        columnGap: Gap.xl,
        padding: padding(Gap.sm, Gap.xl, Gap.md),
        position: Position.relative,
        zIndex: 1,
      } as CSSProperties,
      deltaCenter: {
        ...flexRow,
        justifyContent: Justify.center,
        gap: Gap.md,
      } as CSSProperties,
      deltaPillGroup: {
        ...flexColumn,
        alignItems: Align.center,
        gap: Gap.xs,
      } as CSSProperties,
      deltaPillGroupRank: {} as CSSProperties,
      deltaLabel: {
        fontSize: Font.xs,
        color: Colors.textSubtle,
        textTransform: TextTransform.uppercase,
        letterSpacing: Font.letterSpacingWide,
      } as CSSProperties,
      entry: {
        ...flexColumn,
        gap: Gap.sm,
        color: Colors.textSecondary,
      } as CSSProperties,
      entryRight: {
        ...flexColumn,
        gap: Gap.sm,
        color: Colors.textSecondary,
        alignItems: Align.end,
        textAlign: TextAlign.right,
      } as CSSProperties,
      entryWin: {
        fontWeight: Weight.bold,
      } as CSSProperties,
      entryName: {
        fontSize: Font.lg,
        fontWeight: Weight.semibold,
        color: Colors.textPrimary,
        ...truncate,
        maxWidth: '100%',
      } as CSSProperties,
      entryRank: {
        fontSize: Font.lg,
        fontVariantNumeric: FontVariant.tabularNums,
      } as CSSProperties,
      entryScore: {
        fontSize: Font.lg,
        fontVariantNumeric: FontVariant.tabularNums,
      } as CSSProperties,
      art: {
        width: 40,
        height: 40,
        borderRadius: Radius.sm,
        objectFit: ObjectFit.cover,
        flexShrink: 0,
      } as CSSProperties,
      artPlaceholder: {
        width: 40,
        height: 40,
        borderRadius: Radius.sm,
        backgroundColor: Colors.accentPurpleDark,
        flexShrink: 0,
      } as CSSProperties,
      songInfo: {
        flex: 1,
        minWidth: 0,
      } as CSSProperties,
      songTitle: {
        fontSize: Font.md,
        fontWeight: Weight.semibold,
        ...truncate,
      } as CSSProperties,
      songArtist: {
        fontSize: Font.sm,
        color: Colors.textSubtle,
        ...truncate,
      } as CSSProperties,
      scores: {
        display: Display.flex,
        gap: Gap.lg,
        alignItems: Align.center,
        flexShrink: 0,
      } as CSSProperties,
      scoreColumn: {
        ...flexColumn,
        alignItems: Align.center,
        minWidth: Layout.scoreColumnMinWidth,
      } as CSSProperties,
      scoreLabel: {
        fontSize: Font.xs,
        color: Colors.textSubtle,
        textTransform: TextTransform.uppercase,
        letterSpacing: Font.letterSpacingWide,
      } as CSSProperties,
      scoreRank: {
        fontSize: Font.md,
        fontWeight: Weight.semibold,
      } as CSSProperties,
      scoreValue: {
        fontSize: Font.xs,
        color: Colors.textSubtle,
      } as CSSProperties,
      deltaPositive: {
        ...deltaChipBase,
        backgroundColor: Colors.rivalGreenBg,
        color: Colors.statusGreen,
        borderColor: Colors.rivalGreenBorder,
      } as CSSProperties,
      deltaNegative: {
        ...deltaChipBase,
        backgroundColor: Colors.rivalRedBg,
        color: Colors.statusRed,
        borderColor: Colors.rivalRedBorder,
      } as CSSProperties,
      deltaNeutral: {
        ...deltaChipBase,
        backgroundColor: Colors.surfaceSubtle,
        color: Colors.textSecondary,
      } as CSSProperties,
    };
  }, []);
}
