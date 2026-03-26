/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import React, { useEffect, useLayoutEffect, useRef, useState, useCallback, useMemo, type CSSProperties } from 'react';
import { IoClose, IoChevronDown } from 'react-icons/io5';
import { useTranslation } from 'react-i18next';
import { useIsMobile } from '../../../../hooks/ui/useIsMobile';
import { useScrollMask } from '../../../../hooks/ui/useScrollMask';
import { useVisualViewportHeight, useVisualViewportOffsetTop } from '../../../../hooks/ui/useVisualViewport';
import { useSettings, visibleInstruments } from '../../../../contexts/SettingsContext';
import { INSTRUMENT_LABELS, DEFAULT_INSTRUMENT, type ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { InstrumentIcon } from '../../../../components/display/InstrumentIcons';
import ArcSpinner from '../../../../components/common/ArcSpinner';
import {
  Colors, Radius, Font, Gap, Weight, Shadow,
  Display, Position, Overflow, TextAlign, Cursor, CssValue, CssProp,
  frostedCard, border, padding, transition, transitions,
} from '@festival/theme';
import { modalStyles } from '../../../../components/modals/modalStyles';
import anim from '../../../../styles/animations.module.css';
import { ZoomableImage } from './ZoomableImage';

const TRANSITION_MS = 300;
const DIFFICULTIES = ['easy', 'medium', 'hard', 'expert'] as const;
type Difficulty = typeof DIFFICULTIES[number];

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
      instrumentRow: { display: Display.flex, gap: Gap.md, flexWrap: 'wrap', justifyContent: 'center' } as CSSProperties,
      instrumentBtn: {
        width: 64, height: 64, display: Display.flex, alignItems: 'center', justifyContent: 'center',
        borderRadius: '50%', border: CssValue.none, backgroundColor: 'transparent', cursor: Cursor.pointer,
        position: Position.relative, overflow: Overflow.hidden,
      } as CSSProperties,
      instrumentCircle: {
        position: Position.absolute, inset: 0, borderRadius: '50%',
        backgroundColor: '#2ECC71', transform: 'scale(0)',
        transition: transition(CssProp.transform, 250),
      } as CSSProperties,
      instrumentCircleActive: {
        position: Position.absolute, inset: 0, borderRadius: '50%',
        backgroundColor: '#2ECC71', transform: 'scale(1)',
        transition: transition(CssProp.transform, 250),
      } as CSSProperties,
      mobileRow: { display: 'grid', gridTemplateColumns: '1fr 1fr', gap: Gap.md, overflow: Overflow.hidden } as CSSProperties,
      mobileSelector: {
        ...frostedCard, display: Display.flex, alignItems: 'center', gap: Gap.md,
        padding: selectorPad, borderRadius: Radius.md, color: Colors.textPrimary,
        fontSize: Font.md, fontWeight: Weight.semibold, cursor: Cursor.pointer,
      } as CSSProperties,
      mobileSelectorLabel: { flex: 1, textAlign: TextAlign.left } as CSSProperties,
      chevron: { flexShrink: 0, color: Colors.textMuted, transition: transition(CssProp.transform, 250) } as CSSProperties,
      accordion: { overflow: Overflow.hidden, transition: `max-height 300ms ${ACCORDION_EASE}` } as CSSProperties,
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
  const { settings } = useSettings();
  const instruments = visibleInstruments(settings);
  const [selected, setSelected] = useState<InstrumentKey>(DEFAULT_INSTRUMENT);
  const [difficulty, setDifficulty] = useState<Difficulty>('expert');
  const [instOpen, setInstOpen] = useState(false);
  const [diffOpen, setDiffOpen] = useState(false);
  const accordionTimer = useRef<ReturnType<typeof setTimeout>>(undefined);
  const st = usePathsModalStyles();

  const toggleInst = useCallback(() => {
    clearTimeout(accordionTimer.current);
    if (instOpen) {
      setInstOpen(false);
    } else if (diffOpen) {
      setDiffOpen(false);
      accordionTimer.current = setTimeout(() => setInstOpen(true), 300);
    } else {
      setInstOpen(true);
    }
  }, [instOpen, diffOpen]);

  const toggleDiff = useCallback(() => {
    clearTimeout(accordionTimer.current);
    if (diffOpen) {
      setDiffOpen(false);
    } else if (instOpen) {
      setInstOpen(false);
      accordionTimer.current = setTimeout(() => setDiffOpen(true), 300);
    } else {
      setDiffOpen(true);
    }
  }, [instOpen, diffOpen]);

  useEffect(() => {
    if (visible) {
      setMounted(true);
    } else {
      setAnimIn(false);
      setSelected(DEFAULT_INSTRUMENT);
      setDifficulty('expert');
      setInstOpen(false);
      setDiffOpen(false);
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

  return (
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
          <div style={st.controls}>
            <div style={st.mobileRow}>
              <button style={st.mobileSelector} onClick={toggleInst}>
                <InstrumentIcon instrument={selected} size={28} />
                <span style={st.mobileSelectorLabel}>{INSTRUMENT_LABELS[selected]}</span>
                <IoChevronDown size={16} style={{ ...st.chevron, transform: instOpen ? 'rotate(180deg)' : 'rotate(0)' }} />
              </button>
              <button style={st.mobileSelector} onClick={toggleDiff}>
                <span style={st.mobileSelectorLabel}>{t(`paths.${difficulty}`)}</span>
                <IoChevronDown size={16} style={{ ...st.chevron, transform: diffOpen ? 'rotate(180deg)' : 'rotate(0)' }} />
              </button>
            </div>
            <div style={{ ...st.accordion, maxHeight: instOpen ? 160 : 0 }}>
              <div style={{ ...st.instrumentRow, paddingTop: Gap.md }}>
                {instruments.map(key => {
                  const active = selected === key;
                  return (
                    <button
                      key={key}
                      style={st.instrumentBtn}
                      onClick={() => { setSelected(key); setInstOpen(false); }}
                      title={INSTRUMENT_LABELS[key]}
                    >
                      <div style={active ? st.instrumentCircleActive : st.instrumentCircle} />
                      <div style={{ position: 'relative' as const, zIndex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
                        <InstrumentIcon instrument={key} size={48} />
                      </div>
                    </button>
                  );
                })}
              </div>
            </div>
            <div style={{ ...st.accordion, maxHeight: diffOpen ? 120 : 0 }}>
              <div style={{ ...st.diffGridMobile, paddingTop: Gap.md }}>
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
          </div>
        ) : (
          <div style={st.controls}>
            <div style={st.instrumentRow}>
              {instruments.map(key => {
                const active = selected === key;
                return (
                  <button
                    key={key}
                    style={st.instrumentBtn}
                    onClick={() => setSelected(key)}
                    title={INSTRUMENT_LABELS[key]}
                  >
                    <div style={active ? st.instrumentCircleActive : st.instrumentCircle} />
                    <div style={{ position: 'relative' as const, zIndex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
                      <InstrumentIcon instrument={key} size={48} />
                    </div>
                  </button>
                );
              })}
            </div>
            <div style={st.diffGridDesktop}>
              {DIFFICULTIES.map(d => (
                <button
                  key={d}
                  style={difficulty === d ? st.diffBtnActive : st.diffBtn}
                  onClick={() => setDifficulty(d)}
                >
                  {t(`paths.${d}`)}
                </button>
              ))}
            </div>
          </div>
        )}
        <PathImage songId={songId} instrument={selected} difficulty={difficulty} />
      </div>
    </>
  );
}

type Phase = 'fadeOutImage' | 'spinner' | 'fadeOutSpinner' | 'imageReady' | 'fadeInImage' | 'idle';
const FADE_MS = 300;
const MIN_SPINNER_MS = 400;

function PathImage({ songId, instrument, difficulty }: { songId: string; instrument: InstrumentKey; difficulty: Difficulty }) {
  const { t } = useTranslation();
  const [phase, setPhase] = useState<Phase>('spinner');
  const [displaySrc, setDisplaySrc] = useState('');
  const [error, setError] = useState(false);
  const targetSrc = `/api/paths/${songId}/${instrument}/${difficulty}`;
  const pendingRef = useRef(targetSrc);
  const imgRef = useRef<HTMLImageElement>(null);
  const timerRef = useRef<ReturnType<typeof setTimeout>>(undefined);

  // When target changes, start the transition sequence
  useEffect(() => {
    pendingRef.current = targetSrc;
    setError(false);

    if (displaySrc) {
      // We have an image showing — fade it out first
      setPhase('fadeOutImage');
    } else {
      // First load — go straight to spinner
      setPhase('spinner');
      loadImage(targetSrc);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [targetSrc]);

  // Handle phase transitions after fade-out of image
  useEffect(() => {
    if (phase === 'fadeOutImage') {
      timerRef.current = setTimeout(() => {
        setPhase('spinner');
        loadImage(pendingRef.current);
      }, FADE_MS);
      return () => clearTimeout(timerRef.current);
    }
    if (phase === 'imageReady') {
      // Image is mounted but invisible — trigger fade-in on next frame
      const raf = requestAnimationFrame(() => {
        setPhase('fadeInImage');
        timerRef.current = setTimeout(() => setPhase('idle'), FADE_MS);
      });
      return () => { cancelAnimationFrame(raf); clearTimeout(timerRef.current); };
    }
  // eslint-disable-next-line react-hooks/exhaustive-deps -- loadImage is stable useCallback, defined below
  }, [phase]);

  const loadImage = useCallback((src: string) => {
    const spinnerStart = Date.now();
    const img = new Image();
    img.src = src;

    const onReady = (success: boolean) => {
      // Ignore if a newer request has been made
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
          // Mount image hidden, then fade in on next frame
          setPhase('imageReady');
        }, FADE_MS);
      }, remaining);
    };

    img.onload = () => onReady(true);
    img.onerror = () => onReady(false);
  }, []);

  const spinnerVisible = phase === 'spinner';
  const spinnerMounted = phase === 'spinner' || phase === 'fadeOutSpinner';
  const imageMounted = displaySrc && (phase === 'imageReady' || phase === 'fadeInImage' || phase === 'idle' || phase === 'fadeOutImage');
  const imageVisible = phase === 'fadeInImage' || phase === 'idle';
  const scrollRef = useRef<HTMLDivElement>(null);
  const updateScrollMask = useScrollMask(scrollRef, [displaySrc, phase]);
  const handleScroll = useCallback(() => { updateScrollMask(); }, [updateScrollMask]);

  const imageAreaStyle: React.CSSProperties = {
    flex: 1, overflow: 'auto', position: 'relative',
    display: 'flex', alignItems: 'flex-start', justifyContent: 'center',
    padding: Gap.section,
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
      {error && phase === 'idle' && (
        <div className={anim.spinnerWrap}>
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
  );
}

