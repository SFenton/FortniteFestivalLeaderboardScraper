/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { IoFlash } from 'react-icons/io5';
import { useIsMobile } from '../../../hooks/ui/useIsMobile';
import {
  AlbumArtSize, Align, Border, Colors, CssProp, CssValue, Cursor, Display, Font,
  Gap, IconSize, Justify, Layout, ObjectFit, Radius, TRANSITION_MS, EASE_SMOOTH, Weight,
  border, flexRow, frostedCard, padding, transition,
} from '@festival/theme';
import { type ServerSong as Song } from '@festival/core/api/serverTypes';

export interface SongHeaderProps {
  song: Song | undefined;
  songId: string;
  collapsed: boolean;
  noTransition?: boolean;
  onOpenPaths: () => void;
}

export default function SongHeader({ song,
  songId,
  collapsed,
  noTransition,
  onOpenPaths,
}: SongHeaderProps) {
  const { t } = useTranslation();
  const isMobile = useIsMobile();
  const s = useStyles(collapsed, noTransition);
  return (
    /* v8 ignore start — collapsed ternary styling */
    <div style={s.header}>
      {song?.albumArt ? (
        <img src={song.albumArt} alt="" style={s.headerArt} />
      ) : (
        <div style={s.artPlaceholder} />
      )}
      <div style={s.textWrap}>
        <h1 style={s.songTitle}>{song?.title ?? songId}</h1>
        <p style={s.songArtist}>
          {song?.artist ?? t('common.unknownArtist')}{song?.year ? ` · ${song.year}` : ''}
        </p>
      </div>
      {!isMobile && (
        <button
          onClick={onOpenPaths}
          style={s.viewPathsButton}
        >
          <IoFlash size={IconSize.xs} style={{ marginRight: Gap.md }} />
          {t('common.viewPaths')}
        </button>
      )}
    </div>
    /* v8 ignore stop */
  );
}

function useStyles(collapsed: boolean, noTransition?: boolean) {
  return useMemo(() => {
    const trans = noTransition ? undefined : transition(CssProp.all, TRANSITION_MS, EASE_SMOOTH);
    const artSize = collapsed ? AlbumArtSize.collapsed : AlbumArtSize.expanded;
    return {
      header: {
        ...flexRow,
        gap: Gap.section,
        marginTop: collapsed ? 0 : Gap.xl,
        transition: trans,
      },
      headerArt: {
        width: artSize,
        height: artSize,
        borderRadius: collapsed ? Radius.md : Radius.lg,
        objectFit: ObjectFit.cover,
        flexShrink: 0,
        transition: trans,
      },
      artPlaceholder: {
        width: artSize,
        height: artSize,
        borderRadius: collapsed ? Radius.md : Radius.lg,
        backgroundColor: Colors.accentPurpleDark,
        flexShrink: 0,
        transition: trans,
      },
      textWrap: { flex: 1, minWidth: 0 },
      songTitle: {
        fontSize: Font.title,
        fontWeight: Weight.bold,
        marginBottom: collapsed ? Gap.xs : Gap.sm,
        transition: trans,
      },
      songArtist: {
        fontSize: collapsed ? Font.md : Font.lg,
        color: Colors.textSubtle,
        marginBottom: collapsed ? 0 : Gap.md,
        transition: trans,
      },
      viewPathsButton: {
        ...frostedCard,
        backgroundColor: Colors.accentPurple,
        border: border(Border.thin, Colors.purpleBorderGlass),
        display: Display.inlineFlex,
        alignItems: Align.center,
        justifyContent: Justify.center,
        padding: padding(0, Layout.buttonPaddingH, 0, Gap.section),
        borderRadius: Radius.full,
        color: Colors.textPrimary,
        fontSize: Font.lg,
        fontWeight: Weight.semibold,
        textDecoration: CssValue.none,
        cursor: Cursor.pointer,
        flexShrink: 0,
        alignSelf: Align.center,
        height: Layout.pillButtonHeight,
      },
    };
  }, [collapsed, noTransition]);
}
