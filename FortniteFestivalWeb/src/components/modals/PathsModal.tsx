import React, { useEffect, useLayoutEffect, useRef, useState, useCallback, forwardRef } from 'react';
import { IoClose, IoChevronDown } from 'react-icons/io5';
import { useIsMobile } from '../../hooks/useIsMobile';
import { useScrollMask } from '../../hooks/useScrollMask';
import { useVisualViewportHeight, useVisualViewportOffsetTop } from '../../hooks/useVisualViewport';
import { useSettings, visibleInstruments } from '../../contexts/SettingsContext';
import { INSTRUMENT_LABELS, type InstrumentKey } from '../../models';
import { InstrumentIcon } from '../InstrumentIcons';
import { Colors, Radius, Font, Gap, frostedCard } from '@festival/theme';

const TRANSITION_MS = 300;
const DIFFICULTIES = ['easy', 'medium', 'hard', 'expert'] as const;
type Difficulty = typeof DIFFICULTIES[number];
const DIFFICULTY_LABELS: Record<Difficulty, string> = { easy: 'Easy', medium: 'Medium', hard: 'Hard', expert: 'Expert' };

type Props = {
  visible: boolean;
  songId: string;
  onClose: () => void;
};

export default function PathsModal({ visible, songId, onClose }: Props) {
  const isMobile = useIsMobile();
  const vvHeight = useVisualViewportHeight();
  const vvOffsetTop = useVisualViewportOffsetTop();
  const [mounted, setMounted] = useState(false);
  const [animIn, setAnimIn] = useState(false);
  const panelRef = useRef<HTMLDivElement>(null);
  const { settings } = useSettings();
  const instruments = visibleInstruments(settings);
  const [selected, setSelected] = useState<InstrumentKey>('Solo_Guitar');
  const [difficulty, setDifficulty] = useState<Difficulty>('expert');
  const [instOpen, setInstOpen] = useState(false);
  const [diffOpen, setDiffOpen] = useState(false);
  const accordionTimer = useRef<ReturnType<typeof setTimeout>>(undefined);

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
      setSelected('Solo_Guitar');
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
        aria-label="Paths"
        style={isMobile ? mobilePanel : desktopPanel}
        onTransitionEnd={handleTransitionEnd}
      >
        <div style={styles.header}>
          <h2 style={styles.title}>Paths</h2>
          <button style={styles.closeBtn} onClick={onClose} aria-label="Close"><IoClose size={18} /></button>
        </div>
        {isMobile ? (
          <div style={styles.controls}>
            <div style={styles.mobileRow}>
              <button style={styles.mobileSelector} onClick={toggleInst}>
                <InstrumentIcon instrument={selected} size={28} />
                <span style={styles.mobileSelectorLabel}>{INSTRUMENT_LABELS[selected]}</span>
                <IoChevronDown size={16} style={{ ...styles.chevron, transform: instOpen ? 'rotate(180deg)' : 'rotate(0)' }} />
              </button>
              <button style={styles.mobileSelector} onClick={toggleDiff}>
                <span style={styles.mobileSelectorLabel}>{DIFFICULTY_LABELS[difficulty]}</span>
                <IoChevronDown size={16} style={{ ...styles.chevron, transform: diffOpen ? 'rotate(180deg)' : 'rotate(0)' }} />
              </button>
            </div>
            <div style={{ ...styles.accordion, maxHeight: instOpen ? 160 : 0 }}>
              <div style={{ ...styles.instrumentRow, paddingTop: Gap.md }}>
                {instruments.map(key => {
                  const active = selected === key;
                  return (
                    <button
                      key={key}
                      style={styles.instrumentBtn}
                      onClick={() => { setSelected(key); setInstOpen(false); }}
                      title={INSTRUMENT_LABELS[key]}
                    >
                      <div style={{ ...styles.instrumentCircle, ...(active ? styles.instrumentCircleActive : {}) }} />
                      <div style={{ position: 'relative' as const, zIndex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
                        <InstrumentIcon instrument={key} size={48} />
                      </div>
                    </button>
                  );
                })}
              </div>
            </div>
            <div style={{ ...styles.accordion, maxHeight: diffOpen ? 120 : 0 }}>
              <div style={{ ...styles.diffGridMobile, paddingTop: Gap.md }}>
                {DIFFICULTIES.map(d => (
                  <button
                    key={d}
                    style={{ ...styles.diffBtnSmall, ...(difficulty === d ? styles.diffBtnActive : {}) }}
                    onClick={() => { setDifficulty(d); setDiffOpen(false); }}
                  >
                    {DIFFICULTY_LABELS[d]}
                  </button>
                ))}
              </div>
            </div>
          </div>
        ) : (
          <div style={styles.controls}>
            <div style={styles.instrumentRow}>
              {instruments.map(key => {
                const active = selected === key;
                return (
                  <button
                    key={key}
                    style={styles.instrumentBtn}
                    onClick={() => setSelected(key)}
                    title={INSTRUMENT_LABELS[key]}
                  >
                    <div style={{ ...styles.instrumentCircle, ...(active ? styles.instrumentCircleActive : {}) }} />
                    <div style={{ position: 'relative' as const, zIndex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
                      <InstrumentIcon instrument={key} size={48} />
                    </div>
                  </button>
                );
              })}
            </div>
            <div style={styles.diffGridDesktop}>
              {DIFFICULTIES.map(d => (
                <button
                  key={d}
                  style={{ ...styles.diffBtn, ...(difficulty === d ? styles.diffBtnActive : {}) }}
                  onClick={() => setDifficulty(d)}
                >
                  {DIFFICULTY_LABELS[d]}
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

  return (
    <div ref={scrollRef} onScroll={handleScroll} style={styles.imageArea}>
      {spinnerMounted && (
        <div style={{
          ...styles.spinnerWrap,
          opacity: spinnerVisible ? 1 : 0,
          transition: `opacity ${FADE_MS}ms ease`,
        }}>
          <div style={styles.spinner} />
        </div>
      )}
      {error && phase === 'idle' && (
        <div style={styles.spinnerWrap}>
          <p style={{ color: Colors.textMuted, fontSize: Font.md }}>Path not available</p>
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

const ZoomableImage = forwardRef<HTMLImageElement, { src: string; alt: string; visible: boolean }>(
  function ZoomableImage({ src, alt, visible }, fwdRef) {
    const containerRef = useRef<HTMLDivElement>(null);
    const innerRef = useRef<HTMLImageElement>(null);
    const [scale, setScale] = useState(1);
    const [translate, setTranslate] = useState({ x: 0, y: 0 });
    const gestureRef = useRef({ startScale: 1, startDist: 0, startX: 0, startY: 0, startTx: 0, startTy: 0 });

    // Assign forwarded ref
    useEffect(() => {
      if (typeof fwdRef === 'function') fwdRef(innerRef.current);
      else if (fwdRef) (fwdRef as React.MutableRefObject<HTMLImageElement | null>).current = innerRef.current;
    });

    // Reset zoom when src changes
    useEffect(() => {
      setScale(1);
      setTranslate({ x: 0, y: 0 });
    }, [src]);

    const getTouchDist = (t: React.TouchList) => {
      const dx = t[1]!.clientX - t[0]!.clientX;
      const dy = t[1]!.clientY - t[0]!.clientY;
      return Math.sqrt(dx * dx + dy * dy);
    };

    const getTouchCenter = (t: React.TouchList) => ({
      x: (t[0]!.clientX + t[1]!.clientX) / 2,
      y: (t[0]!.clientY + t[1]!.clientY) / 2,
    });

    const handleTouchStart = useCallback((e: React.TouchEvent) => {
      if (e.touches.length === 2) {
        e.preventDefault();
        gestureRef.current = {
          startScale: scale,
          startDist: getTouchDist(e.touches),
          ...getTouchCenter(e.touches),
          startX: getTouchCenter(e.touches).x,
          startY: getTouchCenter(e.touches).y,
          startTx: translate.x,
          startTy: translate.y,
        };
      }
    }, [scale, translate]);

    const handleTouchMove = useCallback((e: React.TouchEvent) => {
      if (e.touches.length === 2) {
        e.preventDefault();
        const g = gestureRef.current;
        const dist = getTouchDist(e.touches);
        const center = getTouchCenter(e.touches);
        const newScale = Math.min(Math.max(g.startScale * (dist / g.startDist), 1), 5);
        const dx = center.x - g.startX;
        const dy = center.y - g.startY;
        setScale(newScale);
        setTranslate({ x: g.startTx + dx, y: g.startTy + dy });
      }
    }, []);

    const handleDoubleClick = useCallback(() => {
      if (scale > 1) {
        setScale(1);
        setTranslate({ x: 0, y: 0 });
      } else {
        setScale(2.5);
      }
    }, [scale]);

    const handleWheel = useCallback((e: React.WheelEvent) => {
      if (e.ctrlKey || e.metaKey) {
        e.preventDefault();
        const delta = e.deltaY > 0 ? 0.9 : 1.1;
        setScale(s => Math.min(Math.max(s * delta, 1), 5));
      }
    }, []);

    const transform = `translate(${translate.x}px, ${translate.y}px) scale(${scale})`;

    return (
      <div
        ref={containerRef}
        onTouchStart={handleTouchStart}
        onTouchMove={handleTouchMove}
        onDoubleClick={handleDoubleClick}
        onWheel={handleWheel}
        style={{ touchAction: scale > 1 ? 'none' : 'pan-y', display: 'inline-block' }}
      >
        <img
          ref={innerRef}
          src={src}
          alt={alt}
          draggable={false}
          style={{
            ...styles.pathImg,
            opacity: visible ? 1 : 0,
            transform: visible ? transform : `translateY(16px) ${transform}`,
            transformOrigin: 'center top',
            transition: scale === 1 && translate.x === 0 && translate.y === 0
              ? `opacity ${FADE_MS}ms ease, transform ${FADE_MS}ms ease`
              : `opacity ${FADE_MS}ms ease`,
          }}
        />
      </div>
    );
  },
);

const styles: Record<string, React.CSSProperties> = {
  header: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    padding: `${Gap.xl}px 16px ${Gap.xl}px ${Gap.section}px`,
    flexShrink: 0,
  },
  title: {
    fontSize: Font.xl,
    fontWeight: 700,
    margin: 0,
  },
  closeBtn: {
    width: 32,
    height: 32,
    borderRadius: '50%',
    background: Colors.surfaceElevated,
    border: `1px solid ${Colors.borderPrimary}`,
    color: Colors.textSecondary,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    cursor: 'pointer',
    flexShrink: 0,
  },
  controls: {
    flexShrink: 0,
    padding: `${Gap.xl}px ${Gap.section}px`,
  },
  imageArea: {
    flex: 1,
    overflow: 'auto',
    position: 'relative' as const,
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'center',
    padding: Gap.section,
  },
  spinnerWrap: {
    position: 'absolute' as const,
    inset: 0,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
  },
  spinner: {
    width: 48,
    height: 48,
    border: '4px solid rgba(255,255,255,0.10)',
    borderTopColor: Colors.accentPurple,
    borderRadius: '50%',
    animation: 'spin 0.8s linear infinite',
  },
  pathImg: {
    maxWidth: '100%',
    height: 'auto',
    borderRadius: Radius.md,
    transition: 'opacity 400ms ease, transform 400ms ease',
    userSelect: 'none' as const,
  },
  instrumentRow: {
    display: 'flex',
    gap: Gap.md,
    flexWrap: 'wrap' as const,
    justifyContent: 'center',
  },
  instrumentBtn: {
    width: 64,
    height: 64,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    borderRadius: '50%',
    border: 'none',
    backgroundColor: 'transparent',
    cursor: 'pointer',
    position: 'relative' as const,
    overflow: 'hidden' as const,
  },
  instrumentCircle: {
    position: 'absolute' as const,
    inset: 0,
    borderRadius: '50%',
    backgroundColor: '#2ECC71',
    transform: 'scale(0)',
    transition: 'transform 250ms ease',
  },
  instrumentCircleActive: {
    transform: 'scale(1)',
  },
  mobileRow: {
    display: 'grid',
    gridTemplateColumns: '1fr 1fr',
    gap: Gap.md,
  },
  mobileSelector: {
    ...frostedCard,
    display: 'flex',
    alignItems: 'center',
    gap: Gap.md,
    padding: `${Gap.lg}px ${Gap.md + 4}px`,
    borderRadius: Radius.md,
    color: Colors.textPrimary,
    fontSize: Font.md,
    fontWeight: 600,
    cursor: 'pointer',
  } as React.CSSProperties,
  mobileSelectorLabel: {
    flex: 1,
    textAlign: 'left' as const,
  },
  chevron: {
    flexShrink: 0,
    color: Colors.textMuted,
    transition: 'transform 250ms ease',
  },
  accordion: {
    overflow: 'hidden' as const,
    transition: 'max-height 300ms cubic-bezier(0.4, 0, 0.2, 1)',
  },
  diffGridMobile: {
    display: 'grid',
    gridTemplateColumns: '1fr 1fr',
    gap: Gap.sm,
  },
  diffBtnSmall: {
    ...frostedCard,
    padding: `${Gap.lg}px ${Gap.md + 4}px`,
    borderRadius: Radius.md,
    color: Colors.textSecondary,
    fontSize: Font.md,
    fontWeight: 600,
    cursor: 'pointer',
    textAlign: 'center' as const,
    transition: 'background-color 200ms ease, border-color 200ms ease, color 200ms ease',
  } as React.CSSProperties,
  diffGridDesktop: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(100px, 1fr))',
    gap: Gap.md,
    marginTop: Gap.section,
  },
  diffBtn: {
    ...frostedCard,
    padding: `${Gap.xl}px ${Gap.md}px`,
    borderRadius: Radius.md,
    color: Colors.textSecondary,
    fontSize: Font.md,
    fontWeight: 600,
    cursor: 'pointer',
    textAlign: 'center' as const,
    transition: 'background-color 200ms ease, border-color 200ms ease, color 200ms ease',
  } as React.CSSProperties,
  diffBtnActive: {
    backgroundColor: 'rgba(124,58,237,0.5)',
    border: '1px solid rgba(168,120,255,0.4)',
    color: '#fff',
  },
};
