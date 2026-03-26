/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useState, useCallback, memo, useMemo, type CSSProperties } from 'react';
import { Colors, Opacity, TRANSITION_MS, fixedFill, Display, PointerEvents, transition } from '@festival/theme';

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

  return (
    <>
      <img ref={imgRef} src={src} alt="" onLoad={handleLoad} style={s.probe} />
      <div style={{ ...s.bg, backgroundImage: `url(${src})` }} />
      <div style={dimStyle} />
    </>
  );
});

export default BackgroundImage;

function useStyles(loaded: boolean) {
  return useMemo(() => ({
    probe: { display: Display.none } as CSSProperties,
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
