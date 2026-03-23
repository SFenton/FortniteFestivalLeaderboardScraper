/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
/**
 * Lightweight modal shell: overlay + panel + header + close + escape + lifecycle.
 * Both Modal (sort/filter with apply/reset footer) and MobilePlayerSearchModal
 * compose from this to avoid reimplementing the same infrastructure.
 */
import { useEffect, useLayoutEffect, useRef, useState, useCallback, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { IoClose } from 'react-icons/io5';
import { useIsMobile } from '../../../hooks/ui/useIsMobile';
import { useVisualViewportHeight, useVisualViewportOffsetTop } from '../../../hooks/ui/useVisualViewport';
import css from '../Modal.module.css';

const DEFAULT_TRANSITION_MS = 300;

export interface ModalShellProps {
  visible: boolean;
  title: string;
  onClose: () => void;
  children: ReactNode;
  /** Override desktop panel width (default: uses panelDesktop CSS class). */
  desktopClassName?: string;
  /** Transition duration in ms. Default: 300. */
  transitionMs?: number;
  /** Called when the open animation completes. */
  onOpenComplete?: () => void;
  /** Called after the close animation completes and the modal unmounts. */
  onCloseComplete?: () => void;
}

export default function ModalShell({
  visible,
  title,
  onClose,
  children,
  desktopClassName,
  transitionMs = DEFAULT_TRANSITION_MS,
  onOpenComplete,
  onCloseComplete,
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
    if (!mounted) return;
    const handleKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    document.addEventListener('keydown', handleKey);
    return () => document.removeEventListener('keydown', handleKey);
  }, [mounted, onClose]);

  if (!mounted) return null;

  const transVar = { '--modal-transition-ms': `${transitionMs}ms` } as React.CSSProperties;

  const panelStyle: React.CSSProperties = isMobile
    ? { top: vvOffsetTop + vvHeight * 0.2, height: vvHeight * 0.8, transform: animIn ? 'translateY(0)' : 'translateY(100%)' }
    : { transform: animIn ? 'translate(-50%, -50%)' : 'translate(-50%, -40%)', opacity: animIn ? 1 : 0 };

  const panelClass = isMobile ? css.panelMobile : (desktopClassName ?? css.panelDesktop);

  return (
    <>
      <div
        className={css.overlay}
        style={{ ...transVar, opacity: animIn ? 1 : 0 }}
        onClick={onClose}
      />
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-label={title}
        className={panelClass}
        style={{ ...transVar, ...panelStyle }}
        onTransitionEnd={handleTransitionEnd}
      >
        <div className={css.headerWrap}>
          <h2 className={css.headerTitle}>{title}</h2>
          <button className={css.closeBtn} onClick={onClose} aria-label={t('common.close')}><IoClose size={18} /></button>
        </div>
        {children}
      </div>
    </>
  );
}
