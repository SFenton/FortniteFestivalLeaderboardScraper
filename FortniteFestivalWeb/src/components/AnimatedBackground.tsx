import { useEffect, useRef, useState, useCallback, useMemo, type CSSProperties } from 'react';
import type { Song } from '../models';
import css from './AnimatedBackground.module.css';

const FADE_DURATION = 1000; // 1s crossfade
const DISPLAY_DURATION = 5000; // 5s per image
const MOTION_DURATION = DISPLAY_DURATION + FADE_DURATION; // 6s total motion

// ---------------------------------------------------------------------------
// Motion presets – each defines start→end for scale / translateX / translateY.
// Pan presets use a slight scale-up so edges are never exposed.
// ---------------------------------------------------------------------------

type MotionPreset = {
  scale: [number, number];
  translateX: [number, number];
  translateY: [number, number];
};

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

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

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
    const shuffled = [...candidates].sort(() => Math.random() - 0.5);
    return shuffled.slice(0, Math.min(100, shuffled.length));
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

  if (imageUris.length === 0) return null;
  const uriA = imageUris[layerAIdx];
  const uriB = imageUris[layerBIdx];
  if (!uriA) return null;

  return (
    <div className={css.container} style={{ '--fade-ms': `${FADE_DURATION}ms`, opacity: containerVisible ? 1 : 0 } as CSSProperties}>
      <div
        ref={layerARef}
        className={css.layer}
        style={{ opacity: opacityA, backgroundImage: `url(${uriA})` }}
      />
      {uriB && imageUris.length > 1 && (
        <div
          ref={layerBRef}
          className={css.layer}
          style={{ opacity: opacityB, backgroundImage: `url(${uriB})` }}
        />
      )}
      <div className={css.dim} style={{ opacity: dimOpacity }} />
    </div>
  );
}
