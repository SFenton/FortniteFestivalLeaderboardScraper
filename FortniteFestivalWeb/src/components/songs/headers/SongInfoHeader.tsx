/**
 * Shared song + instrument header bar used by LeaderboardPage, PlayerHistoryPage,
 * and SongDetailPage. Shows album art, song title/artist, and optionally an
 * instrument icon. Supports collapsed/expanded sizing for scroll-driven transitions.
 */
import { type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Font, Gap, Radius, Layout } from '@festival/theme';
import { type ServerSong as Song, type ServerInstrumentKey, serverInstrumentLabel } from '@festival/core/api/serverTypes';
import { InstrumentIcon } from '../../display/InstrumentIcons';
import BackgroundImage from '../../page/BackgroundImage';
import css from './SongInfoHeader.module.css';

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
  const instLabel = instrument ? serverInstrumentLabel(instrument) : undefined;
  const transition = animate ? 'all 300ms cubic-bezier(0.4, 0, 0.2, 1)' : undefined;

  return (
    <>
      <BackgroundImage src={song?.albumArt} />
      <div
        className={css.headerBar}
        style={{
          paddingTop: collapsed ? Gap.md : Layout.paddingTop,
          paddingBottom: Gap.section,
          transition: animate ? 'padding 300ms cubic-bezier(0.4, 0, 0.2, 1)' : undefined,
        }}
      >
      <div className={css.headerContent} style={{ transition: animate ? 'margin 300ms cubic-bezier(0.4, 0, 0.2, 1)' : undefined }}>
        <div className={css.headerLeft}>
          {song?.albumArt ? (
            <img
              src={song.albumArt}
              alt=""
              className={css.headerArt}
              style={{ width: artSize, height: artSize, borderRadius, transition }}
            />
          ) : (
            <div
              className={css.artPlaceholder}
              style={{ width: artSize, height: artSize, borderRadius, transition }}
            />
          )}
          <div>
            <h1 className={css.songTitle} style={{ marginBottom: collapsed ? Gap.xs : Gap.sm, transition }}>
              {song?.title ?? songId}
            </h1>
            <p className={css.songArtist} style={{ fontSize: collapsed ? Font.md : Font.lg, transition }}>
              {song?.artist ?? t('common.unknownArtist')}
              {song?.year ? ` \u00b7 ${song.year}` : ''}
            </p>
          </div>
        </div>
        {(instrument || actions) && (
          <div className={css.headerRight}>
            {actions}
            {instrument && (
              <>
                <div
                  className={css.instIconWrap}
                  style={{ transform: collapsed ? 'scale(0.857)' : 'scale(1)', transition: animate ? 'transform 300ms cubic-bezier(0.4, 0, 0.2, 1)' : undefined }}
                >
                  <InstrumentIcon instrument={instrument} size={56} />
                </div>
                <span className={css.instLabel}>{instLabel}</span>
              </>
            )}
          </div>
        )}
      </div>
    </div>
    </>
  );
}
