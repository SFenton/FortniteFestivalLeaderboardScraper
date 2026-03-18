import { useState, useCallback, memo, type CSSProperties } from 'react';
import css from './AlbumArt.module.css';

const spinnerSize = 24;

export default memo(function AlbumArt({ src, size, style, priority }: { src?: string; size: number; style?: CSSProperties; priority?: boolean }) {
  const [loaded, setLoaded] = useState(false);
  const [failed, setFailed] = useState(false);
  /* v8 ignore start */
  const handleLoad = useCallback(() => setLoaded(true), []);
  const handleError = useCallback(() => { setFailed(true); setLoaded(true); }, []);
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
