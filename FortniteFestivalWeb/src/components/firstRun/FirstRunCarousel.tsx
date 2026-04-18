/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useEffect, useLayoutEffect, useState, useRef, useCallback, useMemo, type CSSProperties } from 'react';
import { createPortal } from 'react-dom';
import { useTranslation } from 'react-i18next';
import { IoClose, IoChevronBack, IoChevronForward } from 'react-icons/io5';
import { TRANSITION_MS, FAST_FADE_MS, SWIPE_THRESHOLD, STAGGER_INTERVAL, Size, Colors, Gap, Radius, Font, Weight, Layout, MetadataSize, modalOverlay, modalCard, flexColumn, flexCenter, padding, transition, transitions } from '@festival/theme';
import type { FirstRunSlideDef } from '../../firstRun/types';
import { SlideHeightContext } from '../../firstRun/SlideHeightContext';
import FadeIn from '../page/FadeIn';
import { useIsMobile } from '../../hooks/ui/useIsMobile';

type FirstRunCarouselProps = {
  slides: FirstRunSlideDef[];
  onDismiss: () => void;
  /** Called after the exit animation completes. When provided, enables exit animation. */
  onExitComplete?: () => void;
};

export default function FirstRunCarousel({ slides, onDismiss, onExitComplete }: FirstRunCarouselProps) {
  const { t } = useTranslation();
  const isMobile = useIsMobile();
  const S = useCarouselStyles();
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

  const cardBase = isMobile ? { ...S.card, ...S.cardMobile } : S.card;
  /* v8 ignore start -- animation style branches depend on requestAnimationFrame timing */
  const overlayOpacity = animOut ? 0 : (animIn ? 1 : 0);
  const cardStyle = animOut
    ? { opacity: 0, transform: 'scale(0.95) translateY(10px)', transition: `opacity ${TRANSITION_MS}ms ease, transform ${TRANSITION_MS}ms ease` }
    : entranceDone
      ? undefined
      : { opacity: animIn ? 1 : 0, transform: animIn ? 'scale(1) translateY(0)' : 'scale(0.95) translateY(10px)', transition: `opacity ${TRANSITION_MS}ms ease, transform ${TRANSITION_MS}ms ease` };
  /* v8 ignore stop */

  return createPortal(
    <div
      style={{ ...S.overlay, opacity: overlayOpacity, transition: `opacity ${TRANSITION_MS}ms ease`, pointerEvents: animOut ? 'none' : undefined }}
      onClick={handleDismiss}
      data-glow-scope=""
      data-testid="fre-overlay"
    >
      <div
        style={{ ...cardBase, ...cardStyle }}
        onClick={e => e.stopPropagation()}
        onTouchStart={handleTouchStart}
        onTouchEnd={handleTouchEnd}
        data-testid="fre-card"
      >
        {/* Close button — own flex row so content never overlaps */}
        <div style={S.closeRow}>
          <button style={S.closeBtn} onClick={handleDismiss} aria-label={t('common.close')} data-testid="fre-close">
            <IoClose size={Size.iconFab} />
          </button>
        </div>

        {/* Slide content — ResizeObserver measures height, context provides it to children */}
        <div ref={slideAreaRef} style={S.slideArea} data-testid="fre-slide-area">
          <SlideHeightContext.Provider value={slideHeight}>
            <div key={slideKey} style={fading ? { ...S.slideContent, ...S.fadeOut } : S.slideContent}>
              {entranceDone && slide.render()}
            </div>
          </SlideHeightContext.Provider>
        </div>

        {/* Title + description */}
        <div style={fading ? { ...S.textArea, ...S.fadeOut } : S.textArea}>
          {entranceDone && (
            <>
              <FadeIn
                as="h2"
                key={`title-${slideKey}`}
                style={S.slideTitle}
                delay={titleDelay}
                data-testid="fre-title"
              >
                {t(slide.title)}
              </FadeIn>
              <FadeIn
                as="p"
                key={`desc-${slideKey}`}
                style={S.slideDescription}
                delay={descDelay}
                data-testid="fre-description"
              >
                {t(slide.description)}
              </FadeIn>
            </>
          )}
        </div>

        {/* Pagination */}
        <div style={S.paginationRow}>
          <button
            style={isFirst ? S.arrowBtnDisabled : S.arrowBtn}
            onClick={goBack}
            disabled={isFirst}
            aria-label={t('aria.backOneEntry')}
            data-testid="fre-prev"
          >
            <IoChevronBack size={Size.iconFab} />
          </button>

          <div style={S.dotsWrap} data-testid="fre-dots">
            {slides.map((_, i) => (
              <button
                key={i}
                style={i === currentIndex ? S.dotActive : S.dot}
                onClick={() => { if (i !== currentIndex) navigateTo(i); }}
                aria-label={`Slide ${i + 1}`}
              />
            ))}
          </div>

          <button
            style={isLast ? S.arrowBtnDisabled : S.arrowBtn}
            onClick={goForward}
            disabled={isLast}
            aria-label={t('aria.forwardOneEntry')}
            data-testid="fre-next"
          >
            <IoChevronForward size={Size.iconFab} />
          </button>
        </div>
      </div>
    </div>,
    document.body,
  );
}

function useCarouselStyles() {
  return useMemo(() => ({
    overlay: { ...modalOverlay, zIndex: 1300, padding: Gap.section } as CSSProperties,
    card: {
      ...modalCard, borderRadius: Radius.lg, width: '100%',
      maxWidth: Layout.carouselMaxWidth, height: Layout.carouselHeight,
      maxHeight: Layout.carouselMaxHeight, minHeight: Layout.carouselMinHeight,
      display: 'flex', flexDirection: 'column' as const,
      overflow: 'hidden', position: 'relative' as const,
    } as CSSProperties,
    cardMobile: { height: Layout.carouselHeightMobile, maxHeight: Layout.carouselMaxHeightMobile } as CSSProperties,
    closeRow: { display: 'flex', justifyContent: 'flex-end', padding: padding(Gap.xl, Gap.xl, 0), flexShrink: 0 } as CSSProperties,
    closeBtn: {
      width: Layout.buttonCloseSize, height: Layout.buttonCloseSize,
      borderRadius: '50%', background: Colors.surfaceElevated,
      border: `1px solid ${Colors.borderPrimary}`, color: Colors.textSecondary,
      ...flexCenter, cursor: 'pointer', flexShrink: 0, lineHeight: 0, padding: 0,
    } as CSSProperties,
    slideArea: { flex: 2, ...flexColumn, alignItems: 'center', justifyContent: 'center', padding: padding(Gap.md, Gap.section, 0), minHeight: 0 } as CSSProperties,
    slideContent: { width: '100%', ...flexColumn, alignItems: 'center' } as CSSProperties,
    fadeOut: { opacity: 0, transition: transition('opacity', FAST_FADE_MS, 'ease-in') } as CSSProperties,
    textArea: { flex: 1, ...flexColumn, alignItems: 'center', justifyContent: 'center', gap: Gap.lg, padding: padding(0, Gap.section, Gap.md), textAlign: 'center' as const, minHeight: 0 } as CSSProperties,
    slideTitle: { fontSize: Font.xl, fontWeight: Weight.bold, margin: 0, color: Colors.textPrimary } as CSSProperties,
    slideDescription: { fontSize: Font.md, color: Colors.textSecondary, lineHeight: 1.5, margin: 0 } as CSSProperties,
    paginationRow: { ...flexCenter, gap: Gap.lg, padding: padding(Gap.xl, Gap.section), flexShrink: 0 } as CSSProperties,
    arrowBtn: {
      width: Layout.buttonNavSize, height: Layout.buttonNavSize,
      borderRadius: '50%', background: Colors.surfaceElevated,
      border: `1px solid ${Colors.borderPrimary}`, color: Colors.textSecondary,
      ...flexCenter, cursor: 'pointer', flexShrink: 0,
      transition: transition('opacity', FAST_FADE_MS), lineHeight: 0, padding: 0,
    } as CSSProperties,
    arrowBtnDisabled: {
      width: Layout.buttonNavSize, height: Layout.buttonNavSize,
      borderRadius: '50%', background: Colors.surfaceElevated,
      border: `1px solid ${Colors.borderPrimary}`, color: Colors.textSecondary,
      ...flexCenter, cursor: 'default', flexShrink: 0,
      transition: transition('opacity', FAST_FADE_MS), lineHeight: 0, padding: 0,
      opacity: 0.3, pointerEvents: 'none' as const,
    } as CSSProperties,
    dotsWrap: { display: 'flex', alignItems: 'center', gap: Gap.sm } as CSSProperties,
    dot: {
      width: MetadataSize.dotSize, height: MetadataSize.dotSize,
      borderRadius: '50%', backgroundColor: Colors.surfaceMuted,
      transition: transitions(transition('background-color', FAST_FADE_MS), transition('transform', FAST_FADE_MS)),
      border: 'none', padding: 0, cursor: 'pointer',
    } as CSSProperties,
    dotActive: {
      width: MetadataSize.dotSize, height: MetadataSize.dotSize,
      borderRadius: '50%', backgroundColor: Colors.accentBlue,
      transition: transitions(transition('background-color', FAST_FADE_MS), transition('transform', FAST_FADE_MS)),
      border: 'none', padding: 0, cursor: 'pointer',
      transform: `scale(${MetadataSize.dotActiveScale})`,
    } as CSSProperties,
  }), []);
}
