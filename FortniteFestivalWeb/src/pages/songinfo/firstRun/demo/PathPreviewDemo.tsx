/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useState, useMemo, useEffect, useRef, useCallback, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import type { ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { ImagePhase } from '@festival/core';
import { Colors, Font, TRANSITION_MS, MIN_SPINNER_MS, STAGGER_INTERVAL, Opacity } from '@festival/theme';
import { Layout, Gap, Radius, Weight, Display, Align, Justify, Cursor, CssValue, Position, Overflow, TextAlign, Border, Shadow, ObjectFit, frostedCard, flexColumn, transition, CssProp, BorderStyle, padding, border } from '@festival/theme';
import { useFestival } from '../../../../contexts/FestivalContext';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import { InstrumentSelector, type InstrumentSelectorItem } from '../../../../components/common/InstrumentSelector';
import ArcSpinner, { SpinnerSize } from '../../../../components/common/ArcSpinner';
import FadeIn from '../../../../components/page/FadeIn';
import pathCss from '../../../songinfo/components/path/PathsModal.module.css';
import css from './PathPreviewDemo.module.css';

const FADE_MS = TRANSITION_MS;
const INSTRUMENTS: InstrumentKey[] = ['Solo_Guitar', 'Solo_Bass', 'Solo_Drums', 'Solo_Vocals'];
const DIFFICULTIES = ['easy', 'medium', 'hard', 'expert'] as const;
type Difficulty = typeof DIFFICULTIES[number];
const INSTRUMENT_ROW_HEIGHT = 56;
const DIFF_ROW_1x4 = 52;   // single row of 4 buttons
const NARROW_BREAKPOINT = 420; // matches CSS media query for 2x2 collapse
const GAP = 12;

export default function PathPreviewDemo() {
  const { t } = useTranslation();
  const { state: { songs } } = useFestival();
  const h = useSlideHeight();
  const [selectedInst, setSelectedInst] = useState<InstrumentKey>('Solo_Guitar');
  const [selectedDiff, setSelectedDiff] = useState<Difficulty>('expert');

  // Pick a stable random demo song
  const songId = useMemo(() => {
    const demoSongs = songs.filter(s => s.artist?.includes('Epic Games') && s.songId);
    const pool = demoSongs.length > 0 ? demoSongs : songs;
    if (!pool.length) return null;
    return pool[Math.floor(Math.random() * pool.length)]!.songId;
  }, [songs]);

  // Exact production ImagePhase state machine (mirrors PathImage.tsx)
  const [phase, setPhase] = useState<ImagePhase>(ImagePhase.Spinner);
  const [displaySrc, setDisplaySrc] = useState('');
  const [error, setError] = useState(false);
  const pendingRef = useRef('');
  const timerRef = useRef<ReturnType<typeof setTimeout>>(undefined);

  const targetSrc = songId ? `/api/paths/${songId}/${selectedInst}/${selectedDiff}` : '';

  // Phase 1: when targetSrc changes, start fade-out or spinner
  useEffect(() => {
    if (!targetSrc) return;
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

  // Phase 2: react to phase transitions
  /* v8 ignore start -- Phase state machine transitions triggered by async image loading */
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
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [phase]);
  /* v8 ignore stop */

  // Image preloader with MIN_SPINNER_MS guarantee
  const loadImage = useCallback((src: string) => {
    const spinnerStart = Date.now();
    const img = new Image();
    img.src = src;
    const onReady = (success: boolean) => {
      /* v8 ignore start */
      if (pendingRef.current !== src) return;
      /* v8 ignore stop */
      const elapsed = Date.now() - spinnerStart;
      const remaining = Math.max(0, MIN_SPINNER_MS - elapsed);
      setTimeout(() => {
        /* v8 ignore start */
        if (pendingRef.current !== src) return;
        /* v8 ignore stop */
        setPhase(ImagePhase.FadeOutSpinner);
        setTimeout(() => {
          /* v8 ignore start */
          if (pendingRef.current !== src) return;
          if (success) { setDisplaySrc(src); setError(false); }
          /* v8 ignore stop */
          else { setError(true); }
          setPhase(ImagePhase.ImageReady);
        }, FADE_MS);
      }, remaining);
    };
    img.onload = () => onReady(true);
    img.onerror = () => onReady(false);
  }, []);

  // Phase booleans (same as production PathImage)
  const spinnerVisible = phase === ImagePhase.Spinner;
  const spinnerMounted = phase === ImagePhase.Spinner || phase === ImagePhase.FadeOutSpinner;
  const imageMounted = displaySrc && (phase === ImagePhase.ImageReady || phase === ImagePhase.FadeInImage || phase === ImagePhase.Idle || phase === ImagePhase.FadeOutImage);
  const imageVisible = phase === ImagePhase.FadeInImage || phase === ImagePhase.Idle;

  const selectorItems = useMemo<InstrumentSelectorItem[]>(
    () => INSTRUMENTS.map((key) => ({ key })),
    [],
  );
  const selectorClassNames = useMemo(() => ({
    row: css.iconRow,
    button: css.iconButton,
    buttonActive: css.iconButtonActive,
    arrowButton: css.arrowButton,
  }), []);

  const s = usePathStyles(spinnerVisible, imageVisible);

  // Track wrapper width for compact mode + difficulty visibility
  const wrapperRef = useRef<HTMLDivElement>(null);
  const [wrapperWidth, setWrapperWidth] = useState(0);
  /* v8 ignore start -- ResizeObserver DOM measurement */
  useEffect(() => {
    const el = wrapperRef.current;
    if (!el) return;
    const ro = new ResizeObserver((entries) => {
      setWrapperWidth(entries[0]?.contentRect.width ?? 0);
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, []);
  /* v8 ignore stop */

  /* v8 ignore start -- wrapperWidth requires real ResizeObserver measurements */
  // Compact instrument selector when icons don't fit
  const needed = INSTRUMENTS.length * Layout.demoInstrumentBtn + (INSTRUMENTS.length - 1) * Gap.lg;
  const compact = wrapperWidth > 0 && wrapperWidth < needed;

  const isNarrow = wrapperWidth > 0 && wrapperWidth <= NARROW_BREAKPOINT;
  /* v8 ignore stop */

  // Progressive: hide difficulty when it would wrap to 2x2 or when height is tight; then instruments
  const showDifficulty = !isNarrow && (!h || h >= INSTRUMENT_ROW_HEIGHT + DIFF_ROW_1x4 + GAP * 2 + 80);
  const showInstruments = !h || h >= INSTRUMENT_ROW_HEIGHT + GAP + 80;

  return (
    <div ref={wrapperRef} style={h ? { ...s.wrapper, height: h } : s.wrapper}>
      {/* v8 ignore start -- showInstruments/showDifficulty depend on wrapperWidth (ResizeObserver); phase booleans depend on async image loading state machine */}
      {showInstruments && (
        <FadeIn delay={0}>
          <InstrumentSelector
            instruments={selectorItems}
            selected={selectedInst}
            onSelect={(key) => { /* v8 ignore next -- guard for null key from InstrumentSelector */ if (key) setSelectedInst(key); }}
            required
            compact={compact}
            classNames={selectorClassNames}
          />
        </FadeIn>
      )}

      {showDifficulty && (
        <FadeIn delay={showInstruments ? STAGGER_INTERVAL : 0}>
          <div className={css.diffGrid}>
            {DIFFICULTIES.map((diff) => (
              <button
                key={diff}
                style={diff === selectedDiff ? s.diffBtnActive : s.diffBtn}
                onClick={() => setSelectedDiff(diff)}
              >
                {t(`difficulty.${diff}`, diff.charAt(0).toUpperCase() + diff.slice(1))}
              </button>
            ))}
          </div>
        </FadeIn>
      )}

      <FadeIn delay={(showInstruments ? STAGGER_INTERVAL : 0) + (showDifficulty ? STAGGER_INTERVAL : 0)} style={{ flex: 1, minHeight: 0 }}>
        <div className={css.imageArea}>
          {/* Spinner — matches production .spinnerWrap + fade */}
          {spinnerMounted && (
            <div className={pathCss.spinnerWrap} style={s.spinnerFade}>
              <ArcSpinner size={SpinnerSize.MD} />
            </div>
          )}
          {/* Error — matches production */}
          {error && phase === ImagePhase.Idle && (
            <div className={pathCss.spinnerWrap}>
              <p style={s.errorText}>{t('paths.notAvailable')}</p>
            </div>
          )}
          {/* Image — fades in/out matching production timing */}
          {imageMounted && (
            <img
              src={displaySrc}
              alt={`${selectedInst} ${selectedDiff} path`}
              style={s.pathImgFade}
            />
          )}
        </div>
      </FadeIn>
      {/* v8 ignore stop */}
    </div>
  );
}

function usePathStyles(spinnerVisible: boolean, imageVisible: boolean) {
  return useMemo(() => {
    const diffBtnBase: CSSProperties = {
      padding: padding(Gap.xl, Gap.md),
      borderRadius: Radius.md,
      fontSize: Font.md,
      fontWeight: Weight.semibold,
      cursor: Cursor.pointer,
      textAlign: TextAlign.center,
      transition: transition(CssProp.backgroundColor, FADE_MS),
    };
    return {
      wrapper: { width: CssValue.full, ...flexColumn, gap: Gap.md } as CSSProperties,
      diffBtn: {
        ...frostedCard,
        ...diffBtnBase,
        color: Colors.textSecondary,
      } as CSSProperties,
      diffBtnActive: {
        ...diffBtnBase,
        backgroundColor: Colors.purpleHighlight,
        backgroundImage: CssValue.none,
        border: border(Border.thin, Colors.purpleHighlightBorder),
        boxShadow: Shadow.frostedActive,
        color: Colors.textPrimary,
      } as CSSProperties,
      pathImg: {
        width: CssValue.full,
        display: Display.block,
        objectFit: ObjectFit.cover,
        objectPosition: 'top left',
      } as CSSProperties,
      spinnerFade: {
        opacity: spinnerVisible ? 1 : Opacity.none,
        transition: transition(CssProp.opacity, FADE_MS),
      } as CSSProperties,
      errorText: {
        color: Colors.textMuted,
        fontSize: Font.md,
      } as CSSProperties,
      pathImgFade: {
        width: CssValue.full,
        display: Display.block,
        objectFit: ObjectFit.cover,
        objectPosition: 'top left',
        opacity: imageVisible ? 1 : Opacity.none,
        transition: transition(CssProp.opacity, FADE_MS),
      } as CSSProperties,
    };
  }, [spinnerVisible, imageVisible]);
}
