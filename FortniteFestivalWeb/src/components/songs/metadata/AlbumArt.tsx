import { useState, useCallback, memo, type CSSProperties } from 'react';
import css from './AlbumArt.module.css';

const spinnerSize = 24;

// Track URLs that have loaded successfully so virtualized rows that remount
// can skip the spinner / opacity-0 → 1 transition entirely.
const loadedSrcs = new Set<string>();

/** @internal — exposed for tests only */
export function _resetLoadedSrcs() { loadedSrcs.clear(); }
/** @internal — exposed for tests only */
export function _markLoaded(src: string) { loadedSrcs.add(src); }

export default memo(function AlbumArt({ src, size, style, priority }: { src?: string; size: number; style?: CSSProperties; priority?: boolean }) {
  const alreadyKnown = !!(src && loadedSrcs.has(src));
  const [loaded, setLoaded] = useState(alreadyKnown);
  const [failed, setFailed] = useState(false);
  /* v8 ignore start */
  const handleLoad = useCallback(() => {
    if (src) loadedSrcs.add(src);
    setLoaded(true);
  }, [src]);
  const handleError = useCallback(() => { setFailed(true); setLoaded(true); }, []);
  const imgRef = useCallback((img: HTMLImageElement | null) => {
    if (img?.complete && img.naturalWidth > 0) {
      if (src) loadedSrcs.add(src);
      setLoaded(true);
    }
  }, [src]);
  /* v8 ignore stop */

  const sizeVars = { '--album-size': `${size}px`, ...style } as CSSProperties;

  if (!src || failed) {
    return <div className={css.root} style={sizeVars} />;
  }

  return (
    <div className={css.root} style={sizeVars}>
      {!loaded && (
        <div className={css.spinnerWrap}>
          <div className={css.spinnerCircle} style={{ width: spinnerSize, height: spinnerSize }} />
        </div>
      )}
      <img
        ref={imgRef}
        src={src}
        alt=""
        loading={priority ? 'eager' : 'lazy'}
        fetchPriority={priority ? 'high' : undefined}
        onLoad={handleLoad}
        onError={handleError}
        className={css.image}
        /* v8 ignore start — loaded state depends on image onLoad (not available in jsdom) */
        style={{ width: size, height: size, borderRadius: 'var(--radius-xs)', opacity: loaded ? 1 : 0 }}
        /* v8 ignore stop */
      />
    </div>
  );
});
