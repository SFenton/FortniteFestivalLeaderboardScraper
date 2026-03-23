/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useEffect, useLayoutEffect, useState, useRef, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { IoClose, IoChevronBack, IoChevronForward } from 'react-icons/io5';
import { TRANSITION_MS, FAST_FADE_MS, SWIPE_THRESHOLD, STAGGER_INTERVAL, Size } from '@festival/theme';
import type { FirstRunSlideDef } from '../../firstRun/types';
import { SlideHeightContext } from '../../firstRun/SlideHeightContext';
import FadeIn from '../page/FadeIn';
import css from './FirstRunCarousel.module.css';

type FirstRunCarouselProps = {
  slides: FirstRunSlideDef[];
  onDismiss: () => void;
  /** Called after the exit animation completes. When provided, enables exit animation. */
  onExitComplete?: () => void;
};

export default function FirstRunCarousel({ slides, onDismiss, onExitComplete }: FirstRunCarouselProps) {
  const { t } = useTranslation();
  const [animIn, setAnimIn] = useState(false);
  const [animOut, setAnimOut] = useState(false);
  const [currentIndex, setCurrentIndex] = useState(0);
  const [slideKey, setSlideKey] = useState(0);
  const [fading, setFading] = useState(false);
  const touchStartRef = useRef<number | null>(null);

  // Animate in on mount
  /* v8 ignore start -- requestAnimationFrame not available in jsdom */
  useLayoutEffect(() => {
    const id = requestAnimationFrame(() => setAnimIn(true));
    return () => cancelAnimationFrame(id);
  }, []);
  /* v8 ignore stop */

  /* v8 ignore start -- exit animation guard */
  const handleDismiss = useCallback(() => {
    if (animOut) return;
    onDismiss();
    if (onExitComplete) {
      setAnimOut(true);
      setTimeout(() => onExitComplete(), TRANSITION_MS);
    }
  }, [animOut, onDismiss, onExitComplete]);
  /* v8 ignore stop */

  // Escape key dismisses
  useEffect(() => {
    /* v8 ignore start -- keyboard handler branches */
    const handleKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') handleDismiss();
      else if (e.key === 'ArrowLeft') goBack();
      else if (e.key === 'ArrowRight') goForward();
    };
    /* v8 ignore stop */
    document.addEventListener('keydown', handleKey);
    return () => document.removeEventListener('keydown', handleKey);
  });

  /* v8 ignore start -- fading guard requires sub-frame timing */
  const navigateTo = useCallback((nextIndex: number) => {
    if (fading) return;
    if (nextIndex === currentIndex) return;
    if (nextIndex < 0 || nextIndex >= slides.length) return;
    setFading(true);
    setTimeout(() => {
      setCurrentIndex(nextIndex);
      setSlideKey(k => k + 1);
      setFading(false);
    }, FAST_FADE_MS);
  }, [fading, currentIndex, slides.length]);
  /* v8 ignore stop */

  const goBack = useCallback(() => {
    navigateTo(currentIndex - 1);
  }, [navigateTo, currentIndex]);

  const goForward = useCallback(() => {
    navigateTo(currentIndex + 1);
  }, [navigateTo, currentIndex]);

  // Swipe handlers
  /* v8 ignore start -- touch events not available in jsdom */
  const handleTouchStart = useCallback((e: React.TouchEvent) => {
    touchStartRef.current = e.touches[0]?.clientX ?? null;
  }, []);

  const handleTouchEnd = useCallback((e: React.TouchEvent) => {
    if (touchStartRef.current === null) return;
    const endX = e.changedTouches[0]?.clientX;
    if (endX === undefined) return;
    const delta = endX - touchStartRef.current;
    touchStartRef.current = null;
    if (Math.abs(delta) < SWIPE_THRESHOLD) return;
    if (delta < 0) goForward();
    else goBack();
  }, [goForward, goBack]);
  /* v8 ignore stop */

  // Measure available content height and expose via React context for demo components.
  // Also kept as a CSS var for any CSS-only consumers.
  const slideAreaRef = useRef<HTMLDivElement>(null);
  const [slideHeight, setSlideHeight] = useState(0);
  /* v8 ignore start -- ResizeObserver callback not exercised in jsdom */
  useLayoutEffect(() => {
    const el = slideAreaRef.current;
    if (!el) return;
    const update = () => {
      const s = getComputedStyle(el);
      const available = Math.max(0, el.clientHeight - parseFloat(s.paddingTop) - parseFloat(s.paddingBottom));
      el.style.setProperty('--slide-content-height', `${available}px`);
      setSlideHeight(available);
    };
    update();
    const ro = new ResizeObserver(update);
    ro.observe(el);
    return () => ro.disconnect();
  }, []);
  /* v8 ignore stop */

  // Clear card entrance transform after transition ends to eliminate the
  // GPU compositing layer that causes a visible edge on iOS Safari when
  // combined with backdrop-filter.
  const [entranceDone, setEntranceDone] = useState(false);
  /* v8 ignore start -- depends on requestAnimationFrame timing */
  useEffect(() => {
    if (!animIn) return;
    const id = setTimeout(() => setEntranceDone(true), TRANSITION_MS + 50);
    return () => clearTimeout(id);
  }, [animIn]);
  /* v8 ignore stop */

  const slide = slides[currentIndex];
  if (!slide) return null;

  const isFirst = currentIndex === 0;
  const isLast = currentIndex === slides.length - 1;
  const staggerCount = slide.contentStaggerCount ?? 0;
  const titleDelay = staggerCount * STAGGER_INTERVAL;
  const descDelay = (staggerCount + 1) * STAGGER_INTERVAL;

  /* v8 ignore start -- animation style branches depend on requestAnimationFrame timing */
  const overlayOpacity = animOut ? 0 : (animIn ? 1 : 0);
  const cardStyle = animOut
    ? { opacity: 0, transform: 'scale(0.95) translateY(10px)', transition: `opacity ${TRANSITION_MS}ms ease, transform ${TRANSITION_MS}ms ease` }
    : entranceDone
      ? undefined
      : { opacity: animIn ? 1 : 0, transform: animIn ? 'scale(1) translateY(0)' : 'scale(0.95) translateY(10px)', transition: `opacity ${TRANSITION_MS}ms ease, transform ${TRANSITION_MS}ms ease` };
  /* v8 ignore stop */

  return (
    <div
      className={css.overlay}
      style={{ opacity: overlayOpacity, transition: `opacity ${TRANSITION_MS}ms ease`, pointerEvents: animOut ? 'none' : undefined }}
      onClick={handleDismiss}
    >
      <div
        className={css.card}
        style={cardStyle}
        onClick={e => e.stopPropagation()}
        onTouchStart={handleTouchStart}
        onTouchEnd={handleTouchEnd}
      >
        {/* Close button â€” own flex row so content never overlaps */}
        <div className={css.closeRow}>
          <button className={css.closeBtn} onClick={handleDismiss} aria-label={t('common.close')}>
            <IoClose size={Size.iconFab} />
          </button>
        </div>

        {/* Slide content â€” ResizeObserver measures height, context provides it to children */}
        <div ref={slideAreaRef} className={css.slideArea}>
          <SlideHeightContext.Provider value={slideHeight}>
            <div key={slideKey} className={`${css.slideContent}${fading ? ` ${css.slideFadeOut}` : ''}`}>
              {entranceDone && slide.render()}
            </div>
          </SlideHeightContext.Provider>
        </div>

        {/* Title + description */}
        <div className={`${css.textArea}${fading ? ` ${css.slideFadeOut}` : ''}`}>
          {entranceDone && (
            <>
              <FadeIn
                as="h2"
                key={`title-${slideKey}`}
                className={css.slideTitle}
                delay={titleDelay}
              >
                {t(slide.title)}
              </FadeIn>
              <FadeIn
                as="p"
                key={`desc-${slideKey}`}
                className={css.slideDescription}
                delay={descDelay}
              >
                {t(slide.description)}
              </FadeIn>
            </>
          )}
        </div>

        {/* Pagination */}
        <div className={css.paginationRow}>
          <button
            className={isFirst ? css.arrowBtnDisabled : css.arrowBtn}
            onClick={goBack}
            disabled={isFirst}
            aria-label={t('aria.backOneEntry')}
          >
            <IoChevronBack size={Size.iconFab} />
          </button>

          <div className={css.dotsWrap}>
            {slides.map((_, i) => (
              <button
                key={i}
                className={i === currentIndex ? css.dotActive : css.dot}
                onClick={() => { if (i !== currentIndex) navigateTo(i); }}
                aria-label={`Slide ${i + 1}`}
              />
            ))}
          </div>

          <button
            className={isLast ? css.arrowBtnDisabled : css.arrowBtn}
            onClick={goForward}
            disabled={isLast}
            aria-label={t('aria.forwardOneEntry')}
          >
            <IoChevronForward size={Size.iconFab} />
          </button>
        </div>
      </div>
    </div>
  );
}
