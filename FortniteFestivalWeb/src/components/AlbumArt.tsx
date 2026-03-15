import { useState, useCallback, memo, type CSSProperties } from 'react';
import { Colors, Radius } from '../theme';

const spinnerSize = 24;

export default memo(function AlbumArt({ src, size, style }: { src?: string; size: number; style?: CSSProperties }) {
  const [loaded, setLoaded] = useState(false);
  const [failed, setFailed] = useState(false);
  const handleLoad = useCallback(() => setLoaded(true), []);
  const handleError = useCallback(() => { setFailed(true); setLoaded(true); }, []);

  const base: CSSProperties = {
    width: size,
    height: size,
    borderRadius: Radius.xs,
    flexShrink: 0,
    position: 'relative',
    overflow: 'hidden',
    backgroundColor: 'transparent',
    ...style,
  };

  if (!src || failed) {
    return <div style={base} />;
  }

  return (
    <div style={base}>
      {/* Spinner — removed from DOM once loaded */}
      {!loaded && (
        <div style={{
          position: 'absolute',
          inset: 0,
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'center',
          pointerEvents: 'none',
        }}>
          <div style={{
            width: spinnerSize,
            height: spinnerSize,
            border: '2px solid rgba(255,255,255,0.10)',
            borderTopColor: Colors.accentPurple,
            borderRadius: '50%',
            animation: 'spin 0.8s linear infinite',
          }} />
        </div>
      )}
      {/* Image — fades in when loaded */}
      <img
        src={src}
        alt=""
        loading="lazy"
        onLoad={handleLoad}
        onError={handleError}
        style={{
          width: size,
          height: size,
          objectFit: 'cover',
          borderRadius: Radius.xs,
          opacity: loaded ? 1 : 0,
          transition: 'opacity 300ms ease',
        }}
      />
    </div>
  );
});
