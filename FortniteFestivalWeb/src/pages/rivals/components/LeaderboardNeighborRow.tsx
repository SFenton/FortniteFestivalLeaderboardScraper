/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { memo, useMemo, type CSSProperties, type AnimationEvent } from 'react';
import { Link } from 'react-router-dom';
import {
  Colors, Font, FontVariant, Layout, TextAlign, Weight,
  Display, Align, Gap, Radius, CssValue, CssProp,
  Border, frostedCard, padding, truncate, transition, border,
} from '@festival/theme';
import { useNavLinkPress } from '../../../hooks/navigation/useNavLinkPress';
import { useSelectedProfile } from '../../../hooks/data/useSelectedProfile';
import { getPlayerProfileRoute } from '../../../utils/profileNavigation';

const FAST_FADE_MS = 150;

export interface LeaderboardNeighborRowProps {
  rank: number;
  displayName: string;
  score: number;
  songsPlayed: number;
  accountId: string;
  isPlayer?: boolean;
  /** Pixel width for the rank column. Computed from the longest rank in the section. */
  rankWidth?: number;
  style?: CSSProperties;
  onAnimationEnd?: (e: AnimationEvent<HTMLElement>) => void;
}

export const LeaderboardNeighborRow = memo(function LeaderboardNeighborRow({
  rank,
  displayName,
  score,
  songsPlayed,
  accountId,
  isPlayer,
  rankWidth,
  style,
  onAnimationEnd,
}: LeaderboardNeighborRowProps) {
  const s = useStyles(isPlayer, rankWidth);
  const { profile } = useSelectedProfile();
  const route = getPlayerProfileRoute(accountId, profile);
  const linkPress = useNavLinkPress<HTMLAnchorElement>({ to: route });

  return (
    <Link
      to={route}
      style={{ ...s.row, ...style, ...(linkPress.isPressed ? s.rowPressed : undefined) }}
      onAnimationEnd={onAnimationEnd}
      data-pressed={linkPress.isPressed ? 'true' : undefined}
      {...linkPress.linkPressHandlers}
    >
      <span style={s.colRank}>#{rank.toLocaleString()}</span>
      <span style={s.colName}>{displayName}</span>
      <span style={s.colSongs}>{songsPlayed}</span>
      <span style={s.colScore}>{score.toLocaleString()}</span>
    </Link>
  );
});

function useStyles(isPlayer?: boolean, rankWidth?: number) {
  return useMemo(() => {
    const base: CSSProperties = {
      ...frostedCard,
      display: Display.flex,
      alignItems: Align.center,
      gap: Gap.xl,
      padding: padding(0, Gap.xl),
      height: Layout.entryRowHeight,
      borderRadius: Radius.md,
      textDecoration: CssValue.none,
      color: CssValue.inherit,
      transition: transition(CssProp.backgroundColor, FAST_FADE_MS),
      fontSize: Font.md,
    };

    return {
      row: isPlayer
        ? { ...base, backgroundColor: Colors.purpleHighlight, border: border(Border.thin, Colors.purpleHighlightBorder) }
        : base,
      rowPressed: {
        backgroundColor: 'rgba(255, 255, 255, 0.06)',
      } as CSSProperties,
      colRank: {
        width: rankWidth ?? Layout.rankColumnWidth,
        flexShrink: 0,
        color: Colors.textPrimary,
        fontSize: Font.md,
        fontVariantNumeric: FontVariant.tabularNums,
        ...(isPlayer ? { fontWeight: Weight.bold } : undefined),
      } as CSSProperties,
      colName: {
        ...truncate,
        flex: 1,
        minWidth: 0,
        ...(isPlayer ? { fontWeight: Weight.bold } : undefined),
      } as CSSProperties,
      colSongs: {
        flexShrink: 0,
        fontSize: Font.sm,
        color: Colors.textSecondary,
        fontVariantNumeric: FontVariant.tabularNums,
        textAlign: TextAlign.right,
      } as CSSProperties,
      colScore: {
        flexShrink: 0,
        fontWeight: Weight.semibold,
        fontSize: Font.md,
        color: Colors.accentBlueBright,
        fontVariantNumeric: FontVariant.tabularNums,
        textAlign: TextAlign.right,
        minWidth: '5ch',
      } as CSSProperties,
    };
  }, [isPlayer, rankWidth]);
}
