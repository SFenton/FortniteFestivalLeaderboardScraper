/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useEffect, useRef, useState, useCallback, useMemo, type CSSProperties } from 'react';
import { Colors, ZIndex, Overflow, PointerEvents, fixedFill, absoluteFill } from '@festival/theme';
import { type ServerSong as Song } from '@festival/core/api/serverTypes';

const BG_DURATION = 1000;
const abStyles = {
  container: { ...fixedFill, overflow: Overflow.hidden, zIndex: ZIndex.background, pointerEvents: PointerEvents.none } as CSSProperties,
  layer: { ...absoluteFill, backgroundSize: 'cover', backgroundPosition: 'center', backgroundRepeat: 'no-repeat', willChange: 'transform, opacity' } as CSSProperties,
  dim: { ...absoluteFill, backgroundColor: Colors.backgroundBlack } as CSSProperties,
};

const FADE_DURATION = 1000; // 1s crossfade
const DISPLAY_DURATION = 5000; // 5s per image
const MOTION_DURATION = DISPLAY_DURATION + FADE_DURATION; // 6s total motion
// Motion presets – each defines start→end for scale / translateX / translateY.
// Pan presets use a slight scale-up so edges are never exposed.
type MotionPreset = {
  scale: [number, number];
  translateX: [number, number];
  translateY: [number, number];
};

/* eslint-disable no-magic-numbers -- motion animation parameters */
const MOTION_PRESETS: MotionPreset[] = [
  { scale: [1.0, 1.12], translateX: [0, 0], translateY: [0, 0] },   // Zoom in
  { scale: [1.12, 1.0], translateX: [0, 0], translateY: [0, 0] },   // Zoom out
  { scale: [1.18, 1.18], translateX: [18, -18], translateY: [0, 0] }, // Pan left
  { scale: [1.18, 1.18], translateX: [-18, 18], translateY: [0, 0] }, // Pan right
  { scale: [1.18, 1.18], translateX: [0, 0], translateY: [18, -18] }, // Pan up
  { scale: [1.18, 1.18], translateX: [0, 0], translateY: [-18, 18] }, // Pan down
  { scale: [1.18, 1.18], translateX: [-14, 14], translateY: [-14, 14] }, // Diagonal ↘
  { scale: [1.18, 1.18], translateX: [14, -14], translateY: [-14, 14] }, // Diagonal ↙
  { scale: [1.18, 1.18], translateX: [-14, 14], translateY: [14, -14] }, // Diagonal ↗
  { scale: [1.18, 1.18], translateX: [14, -14], translateY: [14, -14] }, // Diagonal ↖
];
/* eslint-enable no-magic-numbers */

/* v8 ignore start — animation DOM code */
function randomMotion(): MotionPreset {
  return MOTION_PRESETS[Math.floor(Math.random() * MOTION_PRESETS.length)]!;
}

/** Start a Ken Burns motion on an HTML element using Web Animations API. */
function startMotion(el: HTMLElement | null) {
  if (!el) return;
  const p = randomMotion();
  el.getAnimations().forEach((a) => a.cancel());
  el.animate(
    [
      { transform: `scale(${p.scale[0]}) translate(${p.translateX[0]}px, ${p.translateY[0]}px)` },
      { transform: `scale(${p.scale[1]}) translate(${p.translateX[1]}px, ${p.translateY[1]}px)` },
    ],
    { duration: MOTION_DURATION, easing: 'linear', fill: 'forwards' },
  );
}
// Component
export function AnimatedBackground({
  songs,
  dimOpacity = 0.7,
}: {
  songs: Song[];
  dimOpacity?: number;
}) {
  // Build shuffled image list — only rebuild when pool size changes.
  const candidateCount = useMemo(
    () => songs.filter((s) => !!s.albumArt).length,
    [songs],
  );
  const imageUris = useMemo(() => {
    const candidates = songs
      .map((s) => s.albumArt)
      .filter((url): url is string => !!url);
    if (candidates.length === 0) return [];
    // Fisher-Yates shuffle (unbiased, unlike .sort(() => Math.random() - 0.5))
    for (let i = candidates.length - 1; i > 0; i--) {
      const j = Math.floor(Math.random() * (i + 1));
      [candidates[i], candidates[j]] = [candidates[j]!, candidates[i]!];
    }
    return candidates.slice(0, Math.min(100, candidates.length));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [candidateCount]);

  const imgCursor = useRef(2);
  const activeRef = useRef<'A' | 'B'>('A');
  const layerARef = useRef<HTMLDivElement>(null);
  const layerBRef = useRef<HTMLDivElement>(null);

  const [layerAIdx, setLayerAIdx] = useState(0);
  const [layerBIdx, setLayerBIdx] = useState(1);
  const [opacityA, setOpacityA] = useState(1);
  const [opacityB, setOpacityB] = useState(0);
  const [containerVisible, setContainerVisible] = useState(false);

  // Reset layers when image URIs change
  useEffect(() => {
    if (imageUris.length === 0) return;
    setLayerAIdx(0);
    setLayerBIdx(imageUris.length > 1 ? 1 : 0);
    setOpacityA(1);
    setOpacityB(0);
    activeRef.current = 'A';
    imgCursor.current = 2;
  }, [imageUris]);

  // Start initial motion on layer A once images load
  useEffect(() => {
    if (imageUris.length === 0) return;
    startMotion(layerARef.current);
  }, [imageUris]);

  const doTransition = useCallback(() => {
    if (imageUris.length < 2) return;

    if (activeRef.current === 'A') {
      // Start standby (B) motion, then crossfade
      startMotion(layerBRef.current);
      setOpacityA(0);
      setOpacityB(1);
      activeRef.current = 'B';
      // After fade, preload next image on the now-hidden A
      setTimeout(() => {
        const nextIdx = imgCursor.current % imageUris.length;
        imgCursor.current = nextIdx + 1;
        setLayerAIdx(nextIdx);
      }, FADE_DURATION);
    } else {
      startMotion(layerARef.current);
      setOpacityB(0);
      setOpacityA(1);
      activeRef.current = 'A';
      setTimeout(() => {
        const nextIdx = imgCursor.current % imageUris.length;
        imgCursor.current = nextIdx + 1;
        setLayerBIdx(nextIdx);
      }, FADE_DURATION);
    }
  }, [imageUris]);

  // Transition timer — fires every DISPLAY_DURATION, paused when tab is hidden
  useEffect(() => {
    if (imageUris.length < 2) return;
    let timer: ReturnType<typeof setInterval> | null = null;

    const start = () => {
      if (!timer) timer = setInterval(doTransition, DISPLAY_DURATION);
    };
    const stop = () => {
      if (timer) { clearInterval(timer); timer = null; }
      // Pause all running Web Animations on both layers to save GPU
      layerARef.current?.getAnimations().forEach((a) => a.pause());
      layerBRef.current?.getAnimations().forEach((a) => a.pause());
    };
    const onVisibility = () => {
      if (document.hidden) {
        stop();
      } else {
        // Resume paused animations
        layerARef.current?.getAnimations().forEach((a) => a.play());
        layerBRef.current?.getAnimations().forEach((a) => a.play());
        start();
      }
    };

    if (!document.hidden) start();
    document.addEventListener('visibilitychange', onVisibility);

    return () => {
      stop();
      document.removeEventListener('visibilitychange', onVisibility);
    };
  }, [imageUris.length, doTransition]);

  // Fade in the container once images are available
  useEffect(() => {
    if (imageUris.length > 0 && !containerVisible) {
      requestAnimationFrame(() => requestAnimationFrame(() => setContainerVisible(true)));
    }
  }, [imageUris.length, containerVisible]);
  /* v8 ignore stop */

  if (imageUris.length === 0) return null;
  const uriA = imageUris[layerAIdx];
  const uriB = imageUris[layerBIdx];
  /* v8 ignore start -- defensive guard; imageUris.length > 0 checked above */
  if (!uriA) return null;
  /* v8 ignore stop */

  return (
    <div style={{ ...abStyles.container, transition: `opacity ${BG_DURATION}ms ease`, opacity: containerVisible ? 1 : 0 }}>
      <div
        ref={layerARef}
        style={{ ...abStyles.layer, opacity: opacityA, backgroundImage: `url(${uriA})`, transition: `opacity ${FADE_DURATION}ms ease` }}
      />
      {uriB && imageUris.length > 1 && (
        <div
          ref={layerBRef}
          style={{ ...abStyles.layer, opacity: opacityB, backgroundImage: `url(${uriB})`, transition: `opacity ${FADE_DURATION}ms ease` }}
        />
      )}
      <div style={{ ...abStyles.dim, opacity: dimOpacity }} />
    </div>
  );
}
