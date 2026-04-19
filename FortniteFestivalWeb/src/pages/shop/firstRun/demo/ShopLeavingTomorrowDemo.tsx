/* eslint-disable react/forbid-dom-props -- useStyles pattern */
/**
 * First-run demo: Song rows showing the "leaving tomorrow" red pulse contrast.
 * Alternates between red-pulsing (leaving), blue-pulsing (shop), and no highlight.
 */
import { useMemo, type CSSProperties } from 'react';
import AlbumArt from '../../../../components/songs/metadata/AlbumArt';
import MarqueeText from '../../../../components/common/MarqueeText';
import FadeIn from '../../../../components/page/FadeIn';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import { useItemShopDemoSongs } from '../../../../hooks/data/useItemShopDemoSongs';
import { useMarqueeSync } from '../../../../hooks/ui/useMarqueeSync';
import anim from '../../../../styles/animations.module.css';
import {
  Font, Weight, Gap, Radius, Layout, Display, Align,
  CssValue, PointerEvents, frostedCard, flexColumn, padding,
  Colors,
} from '@festival/theme';

const ROW_HEIGHT = Layout.demoRowHeight;
const ART_SIZE = 40;
const MAX_ROWS = 6;

function ShopRowText({ title, artist, styles }: { title: string; artist: string; styles: ReturnType<typeof useStyles> }) {
  const { reporters, syncDistance } = useMarqueeSync(2);

  return (
    <div style={styles.info}>
      <MarqueeText text={title} as="p" style={styles.title} onMeasure={reporters[0]} syncDistance={syncDistance} />
      <MarqueeText text={artist} as="p" style={styles.artist} onMeasure={reporters[1]} syncDistance={syncDistance} />
    </div>
  );
}

export default function ShopLeavingTomorrowDemo() {
  const h = useSlideHeight();
  const s = useStyles();

  const budget = h || 280;
  const maxRows = Math.max(1, Math.floor(budget / (ROW_HEIGHT + Gap.sm)));
  const count = Math.min(maxRows, MAX_ROWS);

  const { songs } = useItemShopDemoSongs(count);

  return (
    <div style={s.wrapper}>
      {songs.map((song, i) => {
        // Pattern: leaving (red), shop (blue), none, leaving, shop, none...
        const phase = i % 3;
        const isLeaving = phase === 0;
        const isShop = phase === 1;
        const className = isLeaving ? anim.shopHighlightRed
          : isShop ? anim.shopHighlight
          : undefined;
        return (
          <FadeIn key={song.songId} delay={i * 80} style={s.row} className={className}>
            <AlbumArt src={song.albumArt} size={ART_SIZE} />
            <ShopRowText title={song.title} artist={song.artist} styles={s} />
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
    } as CSSProperties,
    artist: {
      fontSize: Font.sm,
      color: Colors.textSubtle,
    } as CSSProperties,
  }), []);
}
