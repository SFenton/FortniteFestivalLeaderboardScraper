/**
 * Full-screen background image with dim overlay that fades in once the image loads.
 * Used by SongDetailPage, LeaderboardPage, PlayerPage, etc.
 */
import { useState, useCallback, memo, type CSSProperties } from 'react';
import css from './BackgroundImage.module.css';

interface BackgroundImageProps {
  src: string | undefined;
  /** Dim overlay opacity (0–1). Default: uses CSS variable. */
  dimOpacity?: number;
}

const BackgroundImage = memo(function BackgroundImage({ src, dimOpacity }: BackgroundImageProps) {
  const [loaded, setLoaded] = useState(false);

  /* v8 ignore start — image load callback */
  const handleLoad = useCallback(() => setLoaded(true), []);

  const imgRef = useCallback((img: HTMLImageElement | null) => {
    if (img?.complete && img.naturalWidth > 0) setLoaded(true);
  }, []);
  /* v8 ignore stop */

  if (!src) return null;

  const dimStyle: CSSProperties | undefined = dimOpacity != null ? { opacity: dimOpacity } : undefined;

  return (
    <>
      {/* Hidden img to detect load */}
      {/* v8 ignore start */}
      <img ref={imgRef} src={src} alt="" onLoad={handleLoad} className={css.probe} />
      {/* v8 ignore stop */}
      <div
        className={css.bg}
        style={{ backgroundImage: `url(${src})`, opacity: loaded ? 0.9 : 0 }}
      />
      <div className={css.dim} style={dimStyle} />
    </>
  );
});

export default BackgroundImage;
