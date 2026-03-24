/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { memo, useRef, useCallback, type CSSProperties } from 'react';
import type { ServerSong as Song } from '@festival/core/api/serverTypes';
import css from './ShopCard.module.css';

interface ShopCardProps {
  song: Song;
  staggerDelay?: number;
}

/* v8 ignore start -- visual component tested via ShopPage integration */
export default memo(function ShopCard({ song, staggerDelay }: ShopCardProps) {
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
    ? { opacity: 0, animation: `fadeInUp 400ms ease-out ${staggerDelay}ms forwards` }
    : undefined;

  return (
    <a
      ref={ref}
      href={isExternal ? href : `#${href}`}
      {...(isExternal ? { target: '_blank', rel: 'noopener noreferrer' } : {})}
      className={css.card}
      style={animStyle}
      onAnimationEnd={handleAnimEnd}
    >
      {song.albumArt ? (
        <img src={song.albumArt} alt="" className={css.art} loading="lazy" />
      ) : (
        <div className={css.artPlaceholder} />
      )}
      <div className={css.scrim}>
        <div className={css.title}>{song.title}</div>
        <div className={css.artist}>{song.artist}</div>
      </div>
    </a>
  );
});
/* v8 ignore stop */
