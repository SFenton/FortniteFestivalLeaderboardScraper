import { memo, useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import type { RivalSummary } from '@festival/core/api/serverTypes';
import { Colors, Font, FontVariant, Weight, Gap, Radius, Display, Align, Justify, Position, Cursor, Overflow, WhiteSpace, Border, frostedCard, flexRow, truncate, padding, border } from '@festival/theme';
import s from '../../../styles/rivals.module.css';

/** Minimal shape shared by RivalSummary and LeaderboardRivalSummary. */
type RivalLike = Pick<RivalSummary, 'displayName' | 'sharedSongCount' | 'aheadCount' | 'behindCount'>;

interface RivalRowProps {
  rival: RivalLike;
  /** "above" = rival is ahead of you; "below" = you are ahead */
  direction: 'above' | 'below';
  onClick: () => void;
  style?: React.CSSProperties;
  onAnimationEnd?: (e: React.AnimationEvent<HTMLElement>) => void;
}

const RivalRow = memo(function RivalRow({ rival, direction, onClick, style, onAnimationEnd }: RivalRowProps) {
  const { t } = useTranslation();
  const name = rival.displayName ?? 'Unknown Player';
  const st = useRivalRowStyles();

  const tintClass = direction === 'below' ? s.rivalRowWinning : s.rivalRowLosing;

  return (
    <div
      className={`${s.rivalRow} ${tintClass}`}
      style={{ ...st.row, ...style }}
      onClick={onClick}
      role="button"
      tabIndex={0}
      onKeyDown={e => { if (e.key === 'Enter') onClick(); }}
      onAnimationEnd={onAnimationEnd}
    >
      <div className={s.rivalRowContent} style={st.content}>
        <span style={st.name}>
          {name}
        </span>
        <span className={s.rivalRowShared} style={st.shared}>{t('rivals.sharedSongs', { count: rival.sharedSongCount })}</span>
        <div className={s.rivalRowPillRow} style={st.pillRow}>
          <span style={st.pillAhead}>{rival.behindCount} {t('rivals.songsAhead', 'songs ahead')}</span>
          <span style={st.pillBehind}>{rival.aheadCount} {t('rivals.songsBehind', 'songs behind')}</span>
        </div>
      </div>
    </div>
  );
});

export default RivalRow;

function useRivalRowStyles() {
  return useMemo(() => {
    const pillBase: CSSProperties = {
      display: Display.inlineBlock,
      padding: padding(Gap.xs, Gap.sm),
      borderRadius: Radius.xs,
      fontSize: Font.md,
      fontWeight: Weight.semibold,
      border: border(Border.thick, Colors.borderSubtle),
      whiteSpace: WhiteSpace.nowrap,
    };
    return {
      row: {
        ...frostedCard,
        ...flexRow,
        gap: Gap.xl,
        padding: padding(Gap.lg, Gap.xl),
        borderRadius: Radius.md,
        textDecoration: 'none',
        color: 'inherit',
        cursor: Cursor.pointer,
        position: Position.relative,
        overflow: Overflow.hidden,
      } as CSSProperties,
      content: {
        flex: 1,
        minWidth: 0,
        display: Display.grid,
        gap: padding(Gap.md, Gap.xl),
        position: Position.relative,
        zIndex: 1,
      } as CSSProperties,
      name: {
        fontSize: Font.lg,
        fontWeight: Weight.semibold,
        ...truncate,
        minWidth: 'var(--rival-name-width, 120px)',
        gridColumn: '1',
        gridRow: '1',
        padding: padding(0, Gap.xs),
      } as CSSProperties,
      shared: {
        fontSize: Font.lg,
        fontWeight: Weight.semibold,
        color: Colors.textPrimary,
        whiteSpace: WhiteSpace.nowrap,
        alignSelf: Align.baseline,
        padding: padding(0, Gap.xs),
      } as CSSProperties,
      pillRow: {
        display: Display.flex,
        justifyContent: Justify.between,
        gap: Gap.sm,
      } as CSSProperties,
      pillAhead: {
        ...pillBase,
        backgroundColor: Colors.rivalGreenBg,
        color: Colors.statusGreen,
        borderColor: Colors.rivalGreenBorder,
      } as CSSProperties,
      pillBehind: {
        ...pillBase,
        backgroundColor: Colors.rivalRedBg,
        color: Colors.statusRed,
        borderColor: Colors.rivalRedBorder,
      } as CSSProperties,

    };
  }, []);
}
