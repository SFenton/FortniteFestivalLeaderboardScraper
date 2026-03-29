/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useState, useCallback, memo, useMemo, type CSSProperties } from 'react';
import { Colors, Radius, Position, Overflow, Display, Align, Justify, PointerEvents, ObjectFit, transition, TRANSITION_MS, IconSize } from '@festival/theme';
import anim from '../../../styles/animations.module.css';

const spinnerSize = IconSize.sm;

// Track URLs that have loaded successfully so virtualized rows that remount
// can skip the spinner / opacity-0 → 1 transition entirely.
const loadedSrcs = new Set<string>();

/** @internal — exposed for tests only */
export function _resetLoadedSrcs() { loadedSrcs.clear(); }
/** @internal — exposed for tests only */
export function _markLoaded(src: string) { loadedSrcs.add(src); }

export default memo(function AlbumArt({ src, size, style, priority, pulse, pulseRed }: { src?: string; size: number; style?: CSSProperties; priority?: boolean; pulse?: boolean; pulseRed?: boolean }) {
  const styles = useStyles();
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

  const sizeVars = { ...styles.root, width: size, height: size, ...style } as CSSProperties;

  /* v8 ignore start -- pulse and failed branches */
  const rootClass = pulseRed ? anim.albumPulseRed : pulse ? anim.albumPulse : undefined;

  if (!src || failed) {
    return <div className={rootClass} style={sizeVars} />;
  }
  /* v8 ignore stop */

  return (
    <div className={rootClass} style={sizeVars}>
      {!loaded && (
        <div style={styles.spinnerWrap}>
          <div className={anim.spinnerCircle} style={{ width: spinnerSize, height: spinnerSize }} />
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
        /* v8 ignore start — loaded state depends on image onLoad (not available in jsdom) */
        style={{ width: size, height: size, borderRadius: Radius.xs, opacity: loaded ? 1 : 0, objectFit: ObjectFit.cover, transition: transition('opacity', TRANSITION_MS) } as CSSProperties}
        /* v8 ignore stop */
      />
    </div>
  );
});

function useStyles() {
  return useMemo(() => ({
    root: { borderRadius: Radius.xs, flexShrink: 0, position: Position.relative, overflow: Overflow.hidden, backgroundColor: Colors.transparent } as CSSProperties,
    spinnerWrap: { position: Position.absolute, inset: 0, display: Display.flex, alignItems: Align.center, justifyContent: Justify.center, pointerEvents: PointerEvents.none } as CSSProperties,
  }), []);
}
