/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
/**
 * Lightweight modal shell: overlay + panel + header + close + escape + lifecycle.
 * Both Modal (sort/filter with apply/reset footer) and MobilePlayerSearchModal
 * compose from this to avoid reimplementing the same infrastructure.
 */
import { useEffect, useLayoutEffect, useRef, useState, useCallback, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { useTranslation } from 'react-i18next';
import { IoClose } from 'react-icons/io5';
import { useIsMobile } from '../../../hooks/ui/useIsMobile';
import { useVisualViewportHeight, useVisualViewportOffsetTop } from '../../../hooks/ui/useVisualViewport';
import { modalStyles as css } from '../modalStyles';

const DEFAULT_TRANSITION_MS = 300;

export interface ModalShellProps {
  visible: boolean;
  title: string;
  onClose: () => void;
  children: ReactNode;
  /** Override desktop panel width (default: uses panelDesktop CSS class). */
  desktopClassName?: string;
  /** Extra inline styles merged onto the desktop panel (applied after desktopClassName). */
  desktopStyle?: React.CSSProperties;
  /** Transition duration in ms. Default: 300. */
  transitionMs?: number;
  /** Called when the open animation completes. */
  onOpenComplete?: () => void;
  /** Called after the close animation completes and the modal unmounts. */
  onCloseComplete?: () => void;
  /** Content rendered inside the portal but after the panel (e.g. ConfirmAlert). */
  afterPanel?: ReactNode;
}

export default function ModalShell({
  visible,
  title,
  onClose,
  children,
  desktopClassName,
  desktopStyle,
  transitionMs = DEFAULT_TRANSITION_MS,
  onOpenComplete,
  onCloseComplete,
  afterPanel,
}: ModalShellProps) {
  const { t } = useTranslation();
  const isMobile = useIsMobile();
  const vvHeight = useVisualViewportHeight();
  const vvOffsetTop = useVisualViewportOffsetTop();
  const [mounted, setMounted] = useState(false);
  const [animIn, setAnimIn] = useState(false);
  const panelRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (visible) setMounted(true);
    else setAnimIn(false);
  }, [visible]);

  useLayoutEffect(() => {
    if (mounted && visible) {
      panelRef.current?.getBoundingClientRect();
      const id = requestAnimationFrame(() => setAnimIn(true));
      return () => cancelAnimationFrame(id);
    }
  }, [mounted, visible]);

  const handleTransitionEnd = useCallback(() => {
    if (animIn) {
      onOpenComplete?.();
    } else {
      setMounted(false);
      onCloseComplete?.();
    }
  }, [animIn, onOpenComplete, onCloseComplete]);

  useEffect(() => {
    if (!mounted || visible) return;

    const id = window.setTimeout(() => {
      setMounted(false);
      onCloseComplete?.();
    }, transitionMs + 50);

    return () => window.clearTimeout(id);
  }, [mounted, visible, transitionMs, onCloseComplete]);

  useEffect(() => {
    if (!mounted) return;
    const handleKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    document.addEventListener('keydown', handleKey);
    return () => document.removeEventListener('keydown', handleKey);
  }, [mounted, onClose]);

  if (!mounted) return null;

  const transMs = `${transitionMs}ms`;
  const overlayTransition = `opacity ${transMs} ease`;
  const mobileTransition = `transform ${transMs} ease`;
  const desktopTransition = `opacity ${transMs} ease, transform ${transMs} ease`;

  const panelStyle: React.CSSProperties = isMobile
    ? { ...css.panelMobile, transition: mobileTransition, top: vvOffsetTop + vvHeight * 0.2, height: vvHeight * 0.8, transform: animIn ? 'translateY(0)' : 'translateY(100%)' }
    : { ...css.panelDesktop, transition: desktopTransition, transform: animIn ? 'translate(-50%, -50%)' : 'translate(-50%, -40%)', opacity: animIn ? 1 : 0, ...desktopStyle };

  return createPortal(
    <>
      <div
        style={{ ...css.overlay, transition: overlayTransition, opacity: animIn ? 1 : 0 }}
        onClick={onClose}
        data-glow-scope=""
      />
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-label={title}
        className={!isMobile ? desktopClassName : undefined}
        style={panelStyle}
        onTransitionEnd={handleTransitionEnd}
      >
        <div style={css.headerWrap}>
          <h2 style={css.headerTitle}>{title}</h2>
          <button style={css.closeBtn} onClick={onClose} aria-label={t('common.close')}><IoClose size={18} /></button>
        </div>
        {children}
      </div>
      {afterPanel}
    </>,
    document.body,
  );
}
