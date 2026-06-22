/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useState, useCallback, memo, useMemo, type CSSProperties } from 'react';
import { Colors, Opacity, TRANSITION_MS, fixedFill, Display, PointerEvents, transition } from '@festival/theme';
import { SAFE_AREA_TOP_RAW_VAR } from '../../utils/safeAreaStyles';

const STATUS_BAR_LAYER_Z_INDEX = 2;

interface BackgroundImageProps {
  src: string | undefined;
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

  const s = useStyles(loaded);

  if (!src) return null;

  const dimStyle: CSSProperties | undefined = dimOpacity != null ? { ...s.dim, opacity: dimOpacity } : s.dim;
  const statusDimStyle: CSSProperties | undefined = dimOpacity != null ? { ...s.statusDim, opacity: dimOpacity } : s.statusDim;

  return (
    <>
      <img ref={imgRef} src={src} alt="" onLoad={handleLoad} style={s.probe} />
      <div style={s.statusBar}>
        <div style={{ ...s.statusArt, backgroundImage: `url(${src})` }} />
        <div style={statusDimStyle} />
      </div>
      <div style={{ ...s.bg, backgroundImage: `url(${src})` }} />
      <div style={dimStyle} />
    </>
  );
});

export default BackgroundImage;

function useStyles(loaded: boolean) {
  return useMemo(() => ({
    probe: { display: Display.none } as CSSProperties,
    statusBar: {
      position: 'fixed',
      top: 0,
      left: 0,
      right: 0,
      height: SAFE_AREA_TOP_RAW_VAR,
      overflow: 'hidden',
      zIndex: STATUS_BAR_LAYER_Z_INDEX,
      pointerEvents: PointerEvents.none,
      transition: transition('opacity', TRANSITION_MS),
      opacity: loaded ? Opacity.backgroundImage : Opacity.none,
    } as CSSProperties,
    statusArt: {
      ...fixedFill,
      position: 'absolute',
      backgroundSize: 'cover',
      backgroundPosition: 'center',
      pointerEvents: PointerEvents.none,
    } as CSSProperties,
    statusDim: {
      ...fixedFill,
      position: 'absolute',
      backgroundColor: Colors.overlayDark,
      pointerEvents: PointerEvents.none,
    } as CSSProperties,
    bg: {
      ...fixedFill,
      backgroundSize: 'cover',
      backgroundPosition: 'center',
      pointerEvents: PointerEvents.none,
      transition: transition('opacity', TRANSITION_MS),
      opacity: loaded ? Opacity.backgroundImage : Opacity.none,
    } as CSSProperties,
    dim: {
      ...fixedFill,
      backgroundColor: Colors.overlayDark,
      pointerEvents: PointerEvents.none,
    } as CSSProperties,
  }), [loaded]);
}
