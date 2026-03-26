/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
/**
 * Shared song + instrument header bar used by LeaderboardPage, PlayerHistoryPage,
 * and SongDetailPage. Shows album art, song title/artist, and optionally an
 * instrument icon. Supports collapsed/expanded sizing for scroll-driven transitions.
 */
import { type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Colors, Font, Gap, Radius, Layout, Weight, ObjectFit, Size, flexRow, flexCenter } from '@festival/theme';
import type { CSSProperties } from 'react';
import { type ServerSong as Song, type ServerInstrumentKey } from '@festival/core/api/serverTypes';
import { InstrumentIcon } from '../../display/InstrumentIcons';
import BackgroundImage from '../../page/BackgroundImage';
import PageHeader from '../../common/PageHeader';

const s = {
  headerLeft: { ...flexRow, gap: Gap.section, minWidth: 0 } as CSSProperties,
  headerRight: { ...flexRow, gap: Gap.md, flexShrink: 0, paddingRight: Gap.md } as CSSProperties,
  headerArt: { borderRadius: Radius.md, objectFit: ObjectFit.cover, flexShrink: 0 } as CSSProperties,
  artPlaceholder: { backgroundColor: Colors.accentPurpleDark, flexShrink: 0 } as CSSProperties,
  songTitle: { fontSize: Font.title, fontWeight: Weight.bold, margin: 0 } as CSSProperties,
  songArtist: { fontSize: Font.md, color: Colors.textSubtle, margin: 0 } as CSSProperties,
  instIconWrap: { ...flexCenter, width: Size.iconXl, height: Size.iconXl } as CSSProperties,
};

export interface SongInfoHeaderProps {
  /** Song data (may be undefined while loading). */
  song: Song | undefined;
  /** Fallback song ID for title when song is not yet loaded. */
  songId: string;
  /** Whether the header is in collapsed (mobile/scrolled) state. */
  collapsed: boolean;
  /** Instrument to show on the right side. Omit to hide the instrument section. */
  instrument?: ServerInstrumentKey;
  /** Extra controls rendered in the right section (e.g. sort button). */
  actions?: ReactNode;
  /** Enable smooth CSS transitions for collapse animation. */
  animate?: boolean;
}

export default function SongInfoHeader({ song,
  songId,
  collapsed,
  instrument,
  actions,
  animate,
}: SongInfoHeaderProps) {
  const { t } = useTranslation();
  const artSize = collapsed ? 80 : 120;
  const borderRadius = collapsed ? Radius.md : Radius.lg;
  const transition = animate ? 'all 300ms cubic-bezier(0.4, 0, 0.2, 1)' : undefined;

  return (
    <>
      <BackgroundImage src={song?.albumArt} />
      <PageHeader
        style={{
          paddingTop: collapsed ? Gap.md : Layout.paddingTop,
          paddingBottom: Gap.section,
          transition: animate ? 'padding 300ms cubic-bezier(0.4, 0, 0.2, 1)' : undefined,
          position: 'relative',
          zIndex: 'var(--z-dropdown)',
          flexShrink: 0,
        } as React.CSSProperties}
        title={
          <div style={s.headerLeft}>
            {song?.albumArt ? (
              <img
                src={song.albumArt}
                alt=""
                style={{ ...s.headerArt, width: artSize, height: artSize, borderRadius, transition }}
              />
            ) : (
              <div
                style={{ ...s.artPlaceholder, width: artSize, height: artSize, borderRadius, transition }}
              />
            )}
            <div>
              <h1 style={{ ...s.songTitle, marginBottom: collapsed ? Gap.xs : Gap.sm, transition }}>
                {song?.title ?? songId}
              </h1>
              <p style={{ ...s.songArtist, fontSize: collapsed ? Font.md : Font.lg, transition }}>
                {song?.artist ?? t('common.unknownArtist')}
                {song?.year ? ` \u00b7 ${song.year}` : ''}
              </p>
            </div>
          </div>
        }
        actions={(instrument || actions) ? (
          <>
            {instrument && (
              <div style={s.instIconWrap}>
                <InstrumentIcon instrument={instrument} size={48} />
              </div>
            )}
            {actions}
          </>
        ) : undefined}
      />
    </>
  );
}
