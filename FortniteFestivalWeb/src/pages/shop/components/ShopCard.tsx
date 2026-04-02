/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { memo, useRef, useCallback, useMemo, type CSSProperties } from 'react';
import type { ShopSong } from '@festival/core/api/serverTypes';
import { Colors, Font, Weight, Gap, Radius, Display, Position, Overflow, ObjectFit, Opacity, CssValue, FADE_DURATION, frostedCard } from '@festival/theme';
import { truncate } from '@festival/theme';
import anim from '../../../styles/animations.module.css';

interface ShopCardProps {
  song: ShopSong;
  leavingTomorrow?: boolean;
  staggerDelay?: number;
}

/* v8 ignore start -- visual component tested via ShopPage integration */
export default memo(function ShopCard({ song, leavingTomorrow, staggerDelay }: ShopCardProps) {
  const href = song.shopUrl ?? `/songs/${song.songId}`;
  const isExternal = !!song.shopUrl;
  const ref = useRef<HTMLAnchorElement>(null);

  /* v8 ignore start — animation cleanup */
  const handleAnimEnd = useCallback(() => {
    const el = ref.current;
    if (!el) return;
    el.style.opacity = '';
    el.style.animation = '';
  }, []);
  /* v8 ignore stop */

  const animStyle: CSSProperties | undefined = staggerDelay != null
    ? { opacity: Opacity.none, animation: `fadeInUp ${FADE_DURATION}ms ease-out ${staggerDelay}ms forwards` }
    : undefined;

  const s = useStyles();

  return (
    <a
      ref={ref}
      href={isExternal ? href : `#${href}`}
      {...(isExternal ? { target: '_blank', rel: 'noopener noreferrer' } : {})}
      style={{ ...s.card, ...animStyle }}
      className={leavingTomorrow ? anim.shopHighlightRed : undefined}
      onAnimationEnd={handleAnimEnd}
    >
      {song.albumArt ? (
        <img src={song.albumArt} alt="" style={s.art} loading="lazy" />
      ) : (
        <div style={s.artPlaceholder} />
      )}
      <div style={s.scrim}>
        <div style={s.title}>{song.title}</div>
        <div style={s.artist}>{song.artist}</div>
      </div>
    </a>
  );
});
/* v8 ignore stop */

function useStyles() {
  return useMemo(() => ({
    card: {
      ...frostedCard,
      display: Display.block,
      position: Position.relative,
      aspectRatio: '1',
      borderRadius: Radius.md,
      overflow: Overflow.hidden,
      textDecoration: CssValue.none,
      color: CssValue.inherit,
    } as CSSProperties,
    art: {
      width: CssValue.full,
      height: CssValue.full,
      objectFit: ObjectFit.cover,
    } as CSSProperties,
    artPlaceholder: {
      width: CssValue.full,
      height: CssValue.full,
      backgroundColor: Colors.accentPurpleDark,
    } as CSSProperties,
    scrim: {
      position: Position.absolute,
      bottom: Gap.none,
      left: Gap.none,
      right: Gap.none,
      padding: Gap.lg,
      background: Colors.scrimGradient,
    } as CSSProperties,
    title: {
      fontSize: Font.md,
      fontWeight: Weight.semibold,
      color: Colors.textPrimary,
      ...truncate,
    } as CSSProperties,
    artist: {
      fontSize: Font.sm,
      color: Colors.textSubtle,
      ...truncate,
      marginTop: Gap.xs,
    } as CSSProperties,
  }), []);
}
