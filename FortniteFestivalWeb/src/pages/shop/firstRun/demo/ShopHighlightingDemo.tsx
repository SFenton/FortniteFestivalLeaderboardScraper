/* eslint-disable react/forbid-dom-props -- useStyles pattern */
/**
 * First-run demo: Song rows with pulsing album art to show shop highlighting.
 * Alternates highlighted and non-highlighted rows to demonstrate the contrast.
 */
import { useMemo, type CSSProperties } from 'react';
import AlbumArt from '../../../../components/songs/metadata/AlbumArt';
import FadeIn from '../../../../components/page/FadeIn';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import { useItemShopDemoSongs } from '../../../../hooks/data/useItemShopDemoSongs';
import anim from '../../../../styles/animations.module.css';
import {
  Font, Weight, Gap, Radius, Layout, Display, Align,
  CssValue, PointerEvents, frostedCard, flexColumn, padding, truncate,
  Colors,
} from '@festival/theme';

const ROW_HEIGHT = Layout.demoRowHeight;
const ART_SIZE = 40;
const MAX_ROWS = 5;

export default function ShopHighlightingDemo() {
  const h = useSlideHeight();
  const s = useStyles();

  const budget = h || 280;
  const maxRows = Math.max(1, Math.floor(budget / (ROW_HEIGHT + Gap.sm)));
  const count = Math.min(maxRows, MAX_ROWS);

  const { songs } = useItemShopDemoSongs(count);

  return (
    <div style={s.wrapper}>
      {songs.map((song, i) => {
        // Alternate: even indices are highlighted (pulsing border + album art)
        const pulse = i % 2 === 0;
        return (
          <FadeIn key={song.songId} delay={i * 80} style={s.row} className={pulse ? anim.shopHighlight : undefined}>
            <AlbumArt src={song.albumArt} size={ART_SIZE} pulse={pulse} />
            <div style={s.info}>
              <span style={s.title}>{song.title}</span>
              <span style={s.artist}>{song.artist}</span>
            </div>
          </FadeIn>
        );
      })}
    </div>
  );
}

function useStyles() {
  return useMemo(() => ({
    wrapper: {
      ...flexColumn,
      gap: Gap.sm,
      width: CssValue.full,
      pointerEvents: PointerEvents.none,
    } as CSSProperties,
    row: {
      ...frostedCard,
      display: Display.flex,
      alignItems: Align.center,
      gap: Gap.xl,
      padding: padding(0, Gap.xl),
      height: ROW_HEIGHT,
      borderRadius: Radius.md,
    } as CSSProperties,
    info: {
      ...flexColumn,
      flex: 1,
      minWidth: 0,
      gap: 2,
    } as CSSProperties,
    title: {
      fontSize: Font.md,
      fontWeight: Weight.semibold,
      ...truncate,
    } as CSSProperties,
    artist: {
      fontSize: Font.sm,
      color: Colors.textSubtle,
      ...truncate,
    } as CSSProperties,
  }), []);
}
