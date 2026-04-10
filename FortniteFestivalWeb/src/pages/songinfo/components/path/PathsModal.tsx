/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import React, { useEffect, useLayoutEffect, useRef, useState, useCallback, useMemo, type CSSProperties } from 'react';
import { createPortal } from 'react-dom';
import { IoClose, IoChevronDown, IoImage, IoReaderOutline } from 'react-icons/io5';
import { useTranslation } from 'react-i18next';
import { useIsMobile } from '../../../../hooks/ui/useIsMobile';
import { useVisualViewportHeight, useVisualViewportOffsetTop } from '../../../../hooks/ui/useVisualViewport';
import { useSettings, visibleInstruments } from '../../../../contexts/SettingsContext';
import { INSTRUMENT_LABELS, DEFAULT_INSTRUMENT, type ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { InstrumentIcon } from '../../../../components/display/InstrumentIcons';
import { InstrumentSelector, type InstrumentSelectorItem } from '../../../../components/common/InstrumentSelector';
import ArcSpinner from '../../../../components/common/ArcSpinner';
import {
  Colors, Radius, Font, Gap, Weight, Shadow,
  Display, Overflow, TextAlign, Cursor, CssValue, CssProp,
  frostedCard, border, padding, transition, transitions,
} from '@festival/theme';
import { modalStyles } from '../../../../components/modals/modalStyles';
import anim from '../../../../styles/animations.module.css';
import { ZoomableImage } from './ZoomableImage';
import PathDataTable, { type PathDataResponse, PathDataHeader, type ColumnKey } from './PathDataTable';

const TRANSITION_MS = 300;
const DIFFICULTIES = ['easy', 'medium', 'hard', 'expert'] as const;
type Difficulty = typeof DIFFICULTIES[number];

const CHOPT_DISPLAYS = ['image', 'text'] as const;
type ChoptDisplay = typeof CHOPT_DISPLAYS[number];

const ACCORDION_EASE = 'cubic-bezier(0.4, 0, 0.2, 1)';
const DIFF_TRANSITION = transitions(
  transition(CssProp.backgroundColor, 200),
  transition('border-color', 200),
  transition(CssProp.color, 200),
);

function usePathsModalStyles() {
  return useMemo(() => {
    const selectorPad = padding(Gap.lg, Gap.md + 4);
    const diffBtnBase: CSSProperties = {
      borderRadius: Radius.md,
      fontSize: Font.md,
      fontWeight: Weight.semibold,
      cursor: Cursor.pointer,
      textAlign: TextAlign.center,
      transition: DIFF_TRANSITION,
    };
    return {
      controls: { flexShrink: 0, padding: padding(Gap.xl, Gap.section) } as CSSProperties,
      mobileRow: { display: Display.flex, gap: Gap.md, overflow: Overflow.hidden } as CSSProperties,
      mobileSelector: {
        ...frostedCard, display: Display.flex, alignItems: 'center', justifyContent: 'space-between', gap: Gap.md,
        padding: selectorPad, borderRadius: Radius.md, color: Colors.textPrimary,
        fontSize: Font.md, fontWeight: Weight.semibold, cursor: Cursor.pointer,
      } as CSSProperties,
      mobileSelectorLabel: { flex: 1, textAlign: TextAlign.left } as CSSProperties,
      chevron: { flexShrink: 0, color: Colors.textMuted, transition: transition(CssProp.transform, 250) } as CSSProperties,
      accordion: { overflow: Overflow.hidden, transition: `max-height 300ms ${ACCORDION_EASE}` } as CSSProperties,
      accordionInner: { paddingTop: Gap.md, display: Display.flex, justifyContent: 'center', alignItems: 'center' } as CSSProperties,
      diffGridMobile: { display: 'grid', gridTemplateColumns: '1fr 1fr', gap: Gap.sm, overflow: Overflow.hidden } as CSSProperties,
      diffBtnSmall: { ...frostedCard, ...diffBtnBase, padding: selectorPad, color: Colors.textSecondary } as CSSProperties,
      diffBtnSmallActive: {
        ...diffBtnBase, padding: selectorPad,
        backgroundColor: Colors.purpleHighlight, backgroundImage: CssValue.none,
        border: border(1, Colors.purpleHighlightBorder), boxShadow: Shadow.frostedActive,
        color: Colors.textPrimary,
      } as CSSProperties,
      diffGridDesktop: {
        display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(100px, 1fr))',
        gap: Gap.md, marginTop: Gap.section, overflow: Overflow.hidden,
      } as CSSProperties,
      diffBtn: { ...frostedCard, ...diffBtnBase, padding: padding(Gap.xl, Gap.md), color: Colors.textSecondary } as CSSProperties,
      diffBtnActive: {
        ...diffBtnBase, padding: padding(Gap.xl, Gap.md),
        backgroundColor: Colors.purpleHighlight, backgroundImage: CssValue.none,
        border: border(1, Colors.purpleHighlightBorder), boxShadow: Shadow.frostedActive,
        color: Colors.textPrimary,
      } as CSSProperties,
      desktopRow: { display: 'grid', gridTemplateColumns: '1fr 1fr 1fr', gap: Gap.md, overflow: Overflow.hidden } as CSSProperties,
      desktopSelector: {
        ...frostedCard, display: Display.flex, alignItems: 'center', gap: Gap.md,
        padding: selectorPad, borderRadius: Radius.md, color: Colors.textPrimary,
        fontSize: Font.md, fontWeight: Weight.semibold, cursor: Cursor.pointer,
      } as CSSProperties,
      choptGrid: { display: 'grid', gridTemplateColumns: '1fr 1fr', gap: Gap.sm, overflow: Overflow.hidden } as CSSProperties,
    };
  }, []);
}

type PathsModalProps = {
  visible: boolean;
  songId: string;
  onClose: () => void;
};

export default function PathsModal({ visible, songId, onClose }: PathsModalProps) {
  const { t } = useTranslation();
  const isMobile = useIsMobile();
  const vvHeight = useVisualViewportHeight();
  const vvOffsetTop = useVisualViewportOffsetTop();
  const [mounted, setMounted] = useState(false);
  const [animIn, setAnimIn] = useState(false);
  const panelRef = useRef<HTMLDivElement>(null);
  const { settings, updateSettings } = useSettings();
  const instruments = visibleInstruments(settings);
  const selectorItems = useMemo<InstrumentSelectorItem[]>(
    () => instruments.map(key => ({ key, label: INSTRUMENT_LABELS[key] })),
    [instruments],
  );
  const [selected, setSelected] = useState<InstrumentKey>(DEFAULT_INSTRUMENT);
  const [difficulty, setDifficulty] = useState<Difficulty>('expert');
  const [choptDisplay, setChoptDisplay] = useState<ChoptDisplay>('image');
  const [instOpen, setInstOpen] = useState(false);
  const [diffOpen, setDiffOpen] = useState(false);
  const [choptOpen, setChoptOpen] = useState(false);
  const columnOrder = settings.pathColumnOrder;
  const setColumnOrder = useCallback((order: ColumnKey[]) => updateSettings({ pathColumnOrder: order }), [updateSettings]);
  const accordionTimer = useRef<ReturnType<typeof setTimeout>>(undefined);
  const st = usePathsModalStyles();

  const closeAllAccordions = useCallback(() => {
    setInstOpen(false);
    setDiffOpen(false);
    setChoptOpen(false);
  }, []);

  const toggleInst = useCallback(() => {
    clearTimeout(accordionTimer.current);
    if (instOpen) {
      setInstOpen(false);
    } else if (diffOpen || choptOpen) {
      closeAllAccordions();
      accordionTimer.current = setTimeout(() => setInstOpen(true), 300);
    } else {
      setInstOpen(true);
    }
  }, [instOpen, diffOpen, choptOpen, closeAllAccordions]);

  const toggleDiff = useCallback(() => {
    clearTimeout(accordionTimer.current);
    if (diffOpen) {
      setDiffOpen(false);
    } else if (instOpen || choptOpen) {
      closeAllAccordions();
      accordionTimer.current = setTimeout(() => setDiffOpen(true), 300);
    } else {
      setDiffOpen(true);
    }
  }, [instOpen, diffOpen, choptOpen, closeAllAccordions]);

  const toggleChopt = useCallback(() => {
    clearTimeout(accordionTimer.current);
    if (choptOpen) {
      setChoptOpen(false);
    } else if (instOpen || diffOpen) {
      closeAllAccordions();
      accordionTimer.current = setTimeout(() => setChoptOpen(true), 300);
    } else {
      setChoptOpen(true);
    }
  }, [instOpen, diffOpen, choptOpen, closeAllAccordions]);

  useEffect(() => {
    if (visible) {
      setMounted(true);
    } else {
      setAnimIn(false);
      setSelected(DEFAULT_INSTRUMENT);
      setDifficulty('expert');
      setChoptDisplay('image');
      setInstOpen(false);
      setDiffOpen(false);
      setChoptOpen(false);
      clearTimeout(accordionTimer.current);
    }
  }, [visible]);

  useLayoutEffect(() => {
    if (mounted && visible) {
      panelRef.current?.getBoundingClientRect();
      const id = requestAnimationFrame(() => setAnimIn(true));
      return () => cancelAnimationFrame(id);
    }
  }, [mounted, visible]);

  const handleTransitionEnd = useCallback(() => {
    if (!animIn) setMounted(false);
  }, [animIn]);

  useEffect(() => {
    if (!mounted) return;
    const handleKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    document.addEventListener('keydown', handleKey);
    return () => document.removeEventListener('keydown', handleKey);
  }, [mounted, onClose]);

  if (!mounted) return null;

  const overlayStyle: React.CSSProperties = {
    position: 'fixed',
    inset: 0,
    backgroundColor: Colors.overlayModal,
    zIndex: 1000,
    opacity: animIn ? 1 : 0,
    transition: `opacity ${TRANSITION_MS}ms ease`,
  };

  const panelBase: React.CSSProperties = {
    position: 'fixed',
    zIndex: 1001,
    display: 'flex',
    flexDirection: 'column',
    backgroundColor: Colors.surfaceFrosted,
    backdropFilter: 'blur(18px)',
    WebkitBackdropFilter: 'blur(18px)',
    color: Colors.textPrimary,
  };

  const mobilePanel: React.CSSProperties = {
    ...panelBase,
    left: 0,
    right: 0,
    top: vvOffsetTop + vvHeight * 0.2,
    height: vvHeight * 0.8,
    borderTopLeftRadius: Radius.lg,
    borderTopRightRadius: Radius.lg,
    transition: `transform ${TRANSITION_MS}ms ease`,
    transform: animIn ? 'translateY(0)' : 'translateY(100%)',
  };

  const desktopPanel: React.CSSProperties = {
    ...panelBase,
    top: '50%',
    left: '50%',
    width: '90vw',
    height: '90vh',
    borderRadius: Radius.lg,
    transition: `opacity ${TRANSITION_MS}ms ease, transform ${TRANSITION_MS}ms ease`,
    transform: animIn ? 'translate(-50%, -50%)' : 'translate(-50%, -40%)',
    opacity: animIn ? 1 : 0,
  };

  return createPortal(
    <>
      <div style={overlayStyle} onClick={onClose} />
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-label={t('paths.title')}
        style={isMobile ? mobilePanel : desktopPanel}
        onTransitionEnd={handleTransitionEnd}
      >
        <div style={modalStyles.headerWrap}>
          <h2 style={modalStyles.headerTitle}>{t('paths.title')}</h2>
          <button style={modalStyles.closeBtn} onClick={onClose} aria-label={t('common.close')}><IoClose size={18} /></button>
        </div>
        {isMobile ? (
          <>
            {choptDisplay === 'text' && <PathDataHeader isMobile />}
            <PathImage songId={songId} instrument={selected} difficulty={difficulty} displayMode={choptDisplay} isMobile columnOrder={columnOrder} />
            <div style={st.controls}>
              <div style={{ ...st.accordion, maxHeight: instOpen ? 160 : 0 }}>
                <div style={{ ...st.accordionInner, paddingTop: 0, paddingBottom: Gap.md }}>
                  <InstrumentSelector
                    instruments={selectorItems}
                    selected={selected}
                    onSelect={(key) => { if (key && key === selected) setInstOpen(false); else if (key) setSelected(key); }}
                    required
                  />
                </div>
              </div>
              <div style={{ ...st.accordion, maxHeight: diffOpen ? 120 : 0 }}>
                <div style={{ ...st.diffGridMobile, paddingBottom: Gap.md }}>
                  {DIFFICULTIES.map(d => (
                    <button
                      key={d}
                      style={difficulty === d ? st.diffBtnSmallActive : st.diffBtnSmall}
                      /* v8 ignore start — mobile accordion click */
                      onClick={() => { setDifficulty(d); setDiffOpen(false); }}
                      /* v8 ignore stop */
                    >
                      {t(`paths.${d}`)}
                    </button>
                  ))}
                </div>
              </div>
              <div style={{ ...st.accordion, maxHeight: choptOpen ? 120 : 0 }}>
                <div style={{ ...st.choptGrid, paddingBottom: Gap.md }}>
                  {CHOPT_DISPLAYS.map(d => (
                    <button
                      key={d}
                      style={choptDisplay === d ? st.diffBtnSmallActive : st.diffBtnSmall}
                      /* v8 ignore start — mobile accordion click */
                      onClick={() => { setChoptDisplay(d); setChoptOpen(false); }}
                      /* v8 ignore stop */
                    >
                      {t(`paths.chopt_${d}`)}
                    </button>
                  ))}
                </div>
              </div>
              <div style={st.mobileRow}>
                <button style={{ ...st.mobileSelector, flexShrink: 0 }} onClick={toggleInst}>
                  <InstrumentIcon instrument={selected} size={28} />
                  <IoChevronDown size={16} style={{ ...st.chevron, transform: instOpen ? 'rotate(0)' : 'rotate(180deg)' }} />
                </button>
                <button style={{ ...st.mobileSelector, flex: 1 }} onClick={toggleDiff}>
                  <span style={st.mobileSelectorLabel}>{t(`paths.${difficulty}`)}</span>
                  <IoChevronDown size={16} style={{ ...st.chevron, transform: diffOpen ? 'rotate(0)' : 'rotate(180deg)' }} />
                </button>
                <button style={{ ...st.mobileSelector, flexShrink: 0 }} onClick={toggleChopt}>
                  {choptDisplay === 'image' ? <IoImage size={20} /> : <IoReaderOutline size={20} />}
                  <IoChevronDown size={16} style={{ ...st.chevron, transform: choptOpen ? 'rotate(0)' : 'rotate(180deg)' }} />
                </button>
              </div>
            </div>
          </>
        ) : (
          <div style={st.controls}>
            <div style={st.desktopRow}>
              <button style={st.desktopSelector} onClick={toggleInst}>
                <InstrumentIcon instrument={selected} size={28} />
                <span style={st.mobileSelectorLabel}>{INSTRUMENT_LABELS[selected]}</span>
                <IoChevronDown size={16} style={{ ...st.chevron, transform: instOpen ? 'rotate(180deg)' : 'rotate(0)' }} />
              </button>
              <button style={st.desktopSelector} onClick={toggleDiff}>
                <span style={st.mobileSelectorLabel}>{t(`paths.${difficulty}`)}</span>
                <IoChevronDown size={16} style={{ ...st.chevron, transform: diffOpen ? 'rotate(180deg)' : 'rotate(0)' }} />
              </button>
              <button style={st.desktopSelector} onClick={toggleChopt}>
                {choptDisplay === 'image' ? <IoImage size={20} /> : <IoReaderOutline size={20} />}
                <span style={st.mobileSelectorLabel}>{t(`paths.chopt_${choptDisplay}`)}</span>
                <IoChevronDown size={16} style={{ ...st.chevron, transform: choptOpen ? 'rotate(180deg)' : 'rotate(0)' }} />
              </button>
            </div>
            <div style={{ ...st.accordion, maxHeight: instOpen ? 160 : 0 }}>
              <div style={st.accordionInner}>
                <InstrumentSelector
                  instruments={selectorItems}
                  selected={selected}
                  onSelect={(key) => { if (key && key === selected) setInstOpen(false); else if (key) setSelected(key); }}
                  required
                />
              </div>
            </div>
            <div style={{ ...st.accordion, maxHeight: diffOpen ? 120 : 0 }}>
              <div style={{ ...st.diffGridMobile, paddingTop: Gap.md }}>
                {DIFFICULTIES.map(d => (
                  <button
                    key={d}
                    style={difficulty === d ? st.diffBtnSmallActive : st.diffBtnSmall}
                    onClick={() => { setDifficulty(d); setDiffOpen(false); }}
                  >
                    {t(`paths.${d}`)}
                  </button>
                ))}
              </div>
            </div>
            <div style={{ ...st.accordion, maxHeight: choptOpen ? 120 : 0 }}>
              <div style={{ ...st.choptGrid, paddingTop: Gap.md }}>
                {CHOPT_DISPLAYS.map(d => (
                  <button
                    key={d}
                    style={choptDisplay === d ? st.diffBtnSmallActive : st.diffBtnSmall}
                    onClick={() => { setChoptDisplay(d); setChoptOpen(false); }}
                  >
                    {t(`paths.chopt_${d}`)}
                  </button>
                ))}
              </div>
            </div>
          </div>
        )}
        {!isMobile && choptDisplay === 'text' && <PathDataHeader isMobile={false} columnOrder={columnOrder} onColumnOrderChange={setColumnOrder} />}
        {!isMobile && <PathImage songId={songId} instrument={selected} difficulty={difficulty} displayMode={choptDisplay} isMobile={false} columnOrder={columnOrder} />}
      </div>
    </>,
    document.body,
  );
}

type Phase = 'fadeOutImage' | 'spinner' | 'fadeOutSpinner' | 'imageReady' | 'fadeInImage' | 'idle'
  | 'textSpinner' | 'fadeOutTextSpinner' | 'textStagger';
const FADE_MS = 300;
const MIN_SPINNER_MS = 400;
const MIN_TEXT_SPINNER_MS = 500;

function PathImage({ songId, instrument, difficulty, displayMode, isMobile, columnOrder }: { songId: string; instrument: InstrumentKey; difficulty: Difficulty; displayMode: ChoptDisplay; isMobile: boolean; columnOrder?: ColumnKey[] }) {
  const { t } = useTranslation();

  // ── Image mode state ──
  const [phase, setPhase] = useState<Phase>('spinner');
  const [displaySrc, setDisplaySrc] = useState('');
  const [error, setError] = useState(false);
  const targetSrc = `/api/paths/${songId}/${instrument}/${difficulty}`;
  const pendingRef = useRef(targetSrc);
  const imgRef = useRef<HTMLImageElement>(null);
  const timerRef = useRef<ReturnType<typeof setTimeout>>(undefined);

  // ── Text mode state ──
  const [pathData, setPathData] = useState<PathDataResponse | null>(null);
  const [dataError, setDataError] = useState(false);
  const textDataRef = useRef<PathDataResponse | null>(null);
  const textSpinnerStart = useRef(0);
  const prevDisplayMode = useRef(displayMode);

  // ── Display mode switch ──
  useEffect(() => {
    const prev = prevDisplayMode.current;
    prevDisplayMode.current = displayMode;

    if (displayMode === 'text') {
      // Switching to text — fade out image first if one was showing
      if (prev === 'image' && displaySrc && (phase === 'idle' || phase === 'fadeInImage')) {
        setPhase('fadeOutImage');
        // After image fades, we'll transition to textSpinner in the phase effect
      } else {
        // No image to fade — go straight to text spinner
        setPhase('textSpinner');
      }
    }
    // When switching back to image, the existing image target-change effect handles it
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [displayMode]);

  // ── Text mode fetch ──
  useEffect(() => {
    if (displayMode !== 'text') return;
    setDataError(false);
    setPathData(null);
    textDataRef.current = null;
    let cancelled = false;
    fetch(`/api/paths/${songId}/${instrument}/${difficulty}/data`)
      .then(res => {
        if (!res.ok) throw new Error(`API ${res.status}`);
        return res.json() as Promise<PathDataResponse>;
      })
      .then(data => {
        if (cancelled) return;
        textDataRef.current = data;
        resolveTextSpinner(data, false);
      })
      .catch(() => {
        if (cancelled) return;
        resolveTextSpinner(null, true);
      });
    return () => { cancelled = true; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [displayMode, songId, instrument, difficulty]);

  // ── Text mode: instrument/difficulty change while already in text mode ──
  const prevTextParams = useRef({ songId, instrument, difficulty });
  useEffect(() => {
    if (displayMode !== 'text') {
      prevTextParams.current = { songId, instrument, difficulty };
      return;
    }
    const prev = prevTextParams.current;
    prevTextParams.current = { songId, instrument, difficulty };
    // Only reset to spinner if params changed while already in text mode (not on initial switch)
    if (prev.songId !== songId || prev.instrument !== instrument || prev.difficulty !== difficulty) {
      setPhase('textSpinner');
    }
  }, [displayMode, songId, instrument, difficulty]);

  const textReadyData = useRef<{ data: PathDataResponse | null; isError: boolean } | null>(null);
  const textTimerRef = useRef<ReturnType<typeof setTimeout>>(undefined);
  const phaseRef = useRef(phase);
  phaseRef.current = phase;

  const scheduleTextTransition = useCallback(() => {
    const ready = textReadyData.current;
    if (!ready || phaseRef.current !== 'textSpinner') return;
    const elapsed = Date.now() - textSpinnerStart.current;
    const remaining = Math.max(0, MIN_TEXT_SPINNER_MS - elapsed);
    clearTimeout(textTimerRef.current);
    textTimerRef.current = setTimeout(() => {
      if (phaseRef.current !== 'textSpinner') return;
      setPhase('fadeOutTextSpinner');
      textTimerRef.current = setTimeout(() => {
        if (phaseRef.current !== 'fadeOutTextSpinner') return;
        if (ready.isError) {
          setDataError(true);
        } else if (ready.data) {
          setPathData(ready.data);
        }
        setPhase('textStagger');
      }, FADE_MS);
    }, remaining);
  }, []);

  const resolveTextSpinner = useCallback((data: PathDataResponse | null, isError: boolean) => {
    textReadyData.current = { data, isError };
    scheduleTextTransition();
  }, [scheduleTextTransition]);

  // ── Phase transitions ──
  useEffect(() => {
    if (phase === 'fadeOutImage') {
      timerRef.current = setTimeout(() => {
        if (displayMode === 'text') {
          setPhase('textSpinner');
        } else {
          setPhase('spinner');
          loadImage(pendingRef.current);
        }
      }, FADE_MS);
      return () => clearTimeout(timerRef.current);
    }

    if (phase === 'textSpinner') {
      textSpinnerStart.current = Date.now();
      textReadyData.current = null;
      clearTimeout(textTimerRef.current);
      // Check if data already arrived (from ref)
      const cached = textDataRef.current;
      if (cached) {
        textReadyData.current = { data: cached, isError: false };
        scheduleTextTransition();
      }
    }

    if (phase === 'imageReady') {
      const raf = requestAnimationFrame(() => {
        setPhase('fadeInImage');
        timerRef.current = setTimeout(() => setPhase('idle'), FADE_MS);
      });
      return () => { cancelAnimationFrame(raf); clearTimeout(timerRef.current); };
    }
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [phase, displayMode]);

  // ── Image mode: target change (only when staying in image mode) ──
  useEffect(() => {
    if (displayMode !== 'image') return;
    pendingRef.current = targetSrc;
    setError(false);

    if (displaySrc) {
      setPhase('fadeOutImage');
    } else {
      setPhase('spinner');
      loadImage(targetSrc);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [targetSrc, displayMode]);

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
        setPhase('fadeOutSpinner');
        setTimeout(() => {
          if (pendingRef.current !== src) return;
          if (success) {
            setDisplaySrc(src);
            setError(false);
          } else {
            setError(true);
          }
          setPhase('imageReady');
        }, FADE_MS);
      }, remaining);
    };

    img.onload = () => onReady(true);
    img.onerror = () => onReady(false);
  }, []);

  const isTextSpinnerPhase = phase === 'textSpinner' || phase === 'fadeOutTextSpinner';
  const isImageSpinnerPhase = phase === 'spinner' || phase === 'fadeOutSpinner';
  const spinnerMounted = displayMode === 'image' ? isImageSpinnerPhase : isTextSpinnerPhase;
  const spinnerVisible = displayMode === 'image' ? phase === 'spinner' : phase === 'textSpinner';
  const imageMounted = displayMode === 'image' && displaySrc && (phase === 'imageReady' || phase === 'fadeInImage' || phase === 'idle' || phase === 'fadeOutImage');
  const imageVisible = phase === 'fadeInImage' || phase === 'idle';
  // Also mount image during fadeOutImage when transitioning to text
  const imageFadingToText = displayMode === 'text' && displaySrc && phase === 'fadeOutImage';
  const showError = displayMode === 'image' ? (error && phase === 'idle') : (dataError && phase === 'textStagger');
  const showTable = displayMode === 'text' && pathData != null && phase === 'textStagger';

  const scrollRef = useRef<HTMLDivElement>(null);
  const updateSelfMask = useCallback(() => {
    const el = scrollRef.current;
    if (!el) return;
    const { scrollTop, scrollHeight, clientHeight } = el;
    const canScroll = scrollHeight > clientHeight + 1;
    if (!canScroll) { el.style.maskImage = ''; el.style.webkitMaskImage = ''; return; }
    const atTop = scrollTop <= 0;
    const atBottom = scrollTop + clientHeight >= scrollHeight - 1;
    const s = 40;
    const mask = atTop
      ? `linear-gradient(to bottom, black calc(100% - ${s}px), transparent 100%)`
      : atBottom
        ? `linear-gradient(to bottom, transparent 0px, black ${s}px)`
        : `linear-gradient(to bottom, transparent 0px, black ${s}px, black calc(100% - ${s}px), transparent 100%)`;
    el.style.maskImage = mask;
    el.style.webkitMaskImage = mask;
  }, []);
  useEffect(() => { updateSelfMask(); }, [updateSelfMask, displaySrc, phase, pathData]);
  const handleScroll = useCallback(() => { updateSelfMask(); }, [updateSelfMask]);

  const imageAreaStyle: React.CSSProperties = {
    flex: 1, overflow: 'auto', position: 'relative',
    display: 'flex', alignItems: 'flex-start', justifyContent: 'center',
    padding: Gap.section,
    ...(displayMode === 'text' ? { paddingTop: Gap.sm } : {}),
  };

  return (
    <div ref={scrollRef} onScroll={handleScroll} style={imageAreaStyle}>
      {spinnerMounted && (
        <div className={anim.spinnerWrap} style={{
          opacity: spinnerVisible ? 1 : 0,
          transition: `opacity ${FADE_MS}ms ease`,
        }}>
          <ArcSpinner />
        </div>
      )}
      {showError && (
        <div className={anim.spinnerWrap}>
          <p style={{ color: Colors.textMuted, fontSize: Font.md }}>{t('paths.notAvailable')}</p>
        </div>
      )}
      {(imageMounted || imageFadingToText) && (
        <ZoomableImage
          ref={imgRef}
          src={displaySrc}
          alt={`${instrument} ${difficulty} path`}
          visible={imageVisible}
        />
      )}
      {showTable && <PathDataTable data={pathData} isMobile={isMobile} columnOrder={columnOrder} stagger />}
    </div>
  );
}

