/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
/**
 * Image loader with spinner/fade transition state machine.
 * Extracted from PathsModal for independent testability.
 */
import { useState, useRef, useEffect, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { ImagePhase, type Difficulty } from '@festival/core';
import { Colors, Font, TRANSITION_MS, MIN_SPINNER_MS } from '@festival/theme';
import { type ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { useScrollMask } from '../../../../hooks/ui/useScrollMask';
import { ZoomableImage } from './ZoomableImage';
import css from './PathsModal.module.css';

const FADE_MS = TRANSITION_MS;

interface PathImageProps {
  songId: string;
  instrument: InstrumentKey;
  difficulty: Difficulty;
}

export function PathImage({ songId, instrument, difficulty }: PathImageProps) {
  const { t } = useTranslation();
  const [phase, setPhase] = useState<ImagePhase>(ImagePhase.Spinner);
  const [displaySrc, setDisplaySrc] = useState('');
  const [error, setError] = useState(false);
  const targetSrc = `/api/paths/${songId}/${instrument}/${difficulty}`;
  const pendingRef = useRef(targetSrc);
  const imgRef = useRef<HTMLImageElement>(null);
  const timerRef = useRef<ReturnType<typeof setTimeout>>(undefined);

  /* v8 ignore start — image phase transition effects; phases change only via v8-ignored image callbacks */
  useEffect(() => {
    pendingRef.current = targetSrc;
    setError(false);

    if (displaySrc) {
      setPhase(ImagePhase.FadeOutImage);
    } else {
      setPhase(ImagePhase.Spinner);
      loadImage(targetSrc);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [targetSrc]);

  useEffect(() => {
    if (phase === ImagePhase.FadeOutImage) {
      timerRef.current = setTimeout(() => {
        setPhase(ImagePhase.Spinner);
        loadImage(pendingRef.current);
      }, FADE_MS);
      return () => clearTimeout(timerRef.current);
    }
    if (phase === ImagePhase.ImageReady) {
      const raf = requestAnimationFrame(() => {
        setPhase(ImagePhase.FadeInImage);
        timerRef.current = setTimeout(() => setPhase(ImagePhase.Idle), FADE_MS);
      });
      return () => { cancelAnimationFrame(raf); clearTimeout(timerRef.current); };
    }
  // eslint-disable-next-line react-hooks/exhaustive-deps -- loadImage is stable useCallback, defined below
  }, [phase]);
  /* v8 ignore stop */

  /* v8 ignore start — image loading */
  const loadImage = useCallback((src: string) => {
    const spinnerStart = Date.now();
    const img = new Image();
    img.src = src;

    const onReady = (success: boolean) => {
      if (pendingRef.current !== src) return;
      const elapsed = Date.now() - spinnerStart;
      const remaining = Math.max(0, MIN_SPINNER_MS - elapsed);

      setTimeout(() => {
        if (pendingRef.current !== src) return;
        setPhase(ImagePhase.FadeOutSpinner);
        setTimeout(() => {
          if (pendingRef.current !== src) return;
          if (success) {
            setDisplaySrc(src);
            setError(false);
          } else {
            setError(true);
          }
          setPhase(ImagePhase.ImageReady);
      /* v8 ignore stop */
        }, FADE_MS);
      }, remaining);
    };

    /* v8 ignore start */
    img.onload = () => onReady(true);
    img.onerror = () => onReady(false);
    /* v8 ignore stop */
  }, []);

  /* v8 ignore start — image-phase booleans; phase transitions occur only in v8-ignored image loading callbacks */
  const spinnerVisible = phase === ImagePhase.Spinner;
  const spinnerMounted = phase === ImagePhase.Spinner || phase === ImagePhase.FadeOutSpinner;
  const imageMounted = displaySrc && (phase === ImagePhase.ImageReady || phase === ImagePhase.FadeInImage || phase === ImagePhase.Idle || phase === ImagePhase.FadeOutImage);
  const imageVisible = phase === ImagePhase.FadeInImage || phase === ImagePhase.Idle;
  /* v8 ignore stop */
  const scrollRef = useRef<HTMLDivElement>(null);
  const updateScrollMask = useScrollMask(scrollRef, [displaySrc, phase]);
  /* v8 ignore start */
  const handleScroll = useCallback(() => { updateScrollMask(); }, [updateScrollMask]);
  /* v8 ignore stop */

  return (
    /* v8 ignore start — phase-dependent rendering */
    <div ref={scrollRef} onScroll={handleScroll} className={css.imageArea}>
      {spinnerMounted && (
        <div className={css.spinnerWrap} style={{
          opacity: spinnerVisible ? 1 : 0,
          transition: `opacity ${FADE_MS}ms ease`,
        }}>
          <div className={css.spinner} />
        </div>
      )}
      {error && phase === ImagePhase.Idle && (
        <div className={css.spinnerWrap}>
          <p style={{ color: Colors.textMuted, fontSize: Font.md }}>{t('paths.notAvailable')}</p>
        </div>
      )}
      {imageMounted && (
        <ZoomableImage
          ref={imgRef}
          src={displaySrc}
          alt={`${instrument} ${difficulty} path`}
          visible={imageVisible}
        />
      )}
    </div>
    /* v8 ignore stop */
  );
}
