/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
/**
 * Lightweight modal shell: overlay + panel + header + close + escape + lifecycle.
 * Modal variants compose from this to avoid reimplementing the same infrastructure.
 */
import { useEffect, useLayoutEffect, useRef, useState, useCallback, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { useTranslation } from 'react-i18next';
import { IoClose } from 'react-icons/io5';
import { useIsMobile } from '../../../hooks/ui/useIsMobile';
import { usePressAction } from '../../../hooks/ui/usePressAction';
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
  /** Desktop panel placement. Center preserves the existing modal; rightDrawer slides in from the right edge. */
  desktopPlacement?: 'center' | 'rightDrawer';
  /** Optional test id applied to the dialog panel. */
  panelTestId?: string;
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
  desktopPlacement = 'center',
  panelTestId,
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
  const mobilePanelTopRef = useRef<number | null>(null);
  const rendered = visible || mounted;
  const overlayPressHandlers = usePressAction<HTMLDivElement>({ onPress: onClose, disabled: !visible });
  const closeButtonPressHandlers = usePressAction<HTMLButtonElement>({ onPress: onClose, disabled: !visible });

  useEffect(() => {
    if (visible) {
      if (!mounted) mobilePanelTopRef.current = null;
      setMounted(true);
    } else {
      setAnimIn(false);
    }
  }, [mounted, visible]);

  useLayoutEffect(() => {
    if (rendered && visible) {
      panelRef.current?.getBoundingClientRect();
      const id = requestAnimationFrame(() => setAnimIn(true));
      return () => cancelAnimationFrame(id);
    }
  }, [rendered, visible]);

  const handleTransitionEnd = useCallback(() => {
    if (animIn) {
      onOpenComplete?.();
    } else {
      mobilePanelTopRef.current = null;
      setMounted(false);
      onCloseComplete?.();
    }
  }, [animIn, onOpenComplete, onCloseComplete]);

  useEffect(() => {
    if (!mounted || visible) return;

    const id = window.setTimeout(() => {
      mobilePanelTopRef.current = null;
      setMounted(false);
      onCloseComplete?.();
    }, transitionMs + 50);

    return () => window.clearTimeout(id);
  }, [mounted, visible, transitionMs, onCloseComplete]);

  useEffect(() => {
    if (!rendered) return;
    const handleKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    document.addEventListener('keydown', handleKey);
    return () => document.removeEventListener('keydown', handleKey);
  }, [rendered, onClose]);

  if (!rendered) return null;

  const transMs = `${transitionMs}ms`;
  const overlayTransition = `opacity ${transMs} ease`;
  const mobileTransition = `transform ${transMs} ease`;
  const desktopTransition = `opacity ${transMs} ease, transform ${transMs} ease`;
  const useDesktopPanel = desktopPlacement === 'rightDrawer' || !isMobile;
  const modalPointerEvents = visible ? 'auto' as const : 'none' as const;
  const computedMobileTop = vvOffsetTop + vvHeight * 0.2;
  if (!useDesktopPanel && visible && mobilePanelTopRef.current === null) {
    mobilePanelTopRef.current = computedMobileTop;
  }
  const mobilePanelTop = mobilePanelTopRef.current ?? computedMobileTop;

  const panelStyle: React.CSSProperties = !useDesktopPanel
    ? { ...css.panelMobile, transition: mobileTransition, top: mobilePanelTop, bottom: 0, transform: animIn ? 'translateY(0)' : 'translateY(100%)', pointerEvents: modalPointerEvents }
    : desktopPlacement === 'rightDrawer'
      ? { ...css.panelDesktopRightDrawer, transition: desktopTransition, transform: animIn ? 'translateX(0)' : 'translateX(100%)', opacity: 1, pointerEvents: modalPointerEvents, ...desktopStyle }
      : { ...css.panelDesktop, transition: desktopTransition, transform: animIn ? 'translate(-50%, -50%)' : 'translate(-50%, -40%)', opacity: animIn ? 1 : 0, pointerEvents: modalPointerEvents, ...desktopStyle };

  return createPortal(
    <>
      <div
        style={{ ...css.overlay, transition: overlayTransition, opacity: animIn ? 1 : 0, pointerEvents: modalPointerEvents }}
        {...overlayPressHandlers}
        data-glow-scope=""
      />
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-label={title}
        className={useDesktopPanel ? desktopClassName : undefined}
        data-testid={panelTestId}
        data-modal-placement={useDesktopPanel ? desktopPlacement : 'mobileSheet'}
        style={panelStyle}
        onTransitionEnd={handleTransitionEnd}
      >
        <div style={css.headerWrap}>
          <h2 style={css.headerTitle}>{title}</h2>
          <button style={css.closeBtn} {...closeButtonPressHandlers} aria-label={t('common.close')}><IoClose size={18} /></button>
        </div>
        {children}
      </div>
      {afterPanel}
    </>,
    document.body,
  );
}
