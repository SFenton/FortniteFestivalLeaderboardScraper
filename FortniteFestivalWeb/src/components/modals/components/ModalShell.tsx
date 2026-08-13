/**
 * Lightweight modal shell: overlay + panel + header + close + escape + lifecycle.
 * Modal variants compose from this to avoid reimplementing the same infrastructure.
 */
import { createContext, useContext, useEffect, useLayoutEffect, useRef, useState, useCallback, type ReactNode, type TransitionEvent as ReactTransitionEvent } from 'react';
import { createPortal } from 'react-dom';
import { useTranslation } from 'react-i18next';
import { IoClose } from 'react-icons/io5';
import { useIsMobile } from '../../../hooks/ui/useIsMobile';
import { usePressAction } from '../../../hooks/ui/usePressAction';
import { useVisualViewportHeight, useVisualViewportOffsetTop } from '../../../hooks/ui/useVisualViewport';
import { modalStyles as css } from '../modalStyles';

const DEFAULT_TRANSITION_MS = 300;
const FOCUSABLE_SELECTOR = [
  'a[href]',
  'button:not([disabled])',
  'input:not([disabled])',
  'select:not([disabled])',
  'textarea:not([disabled])',
  '[tabindex]:not([tabindex="-1"])',
].join(',');

type ActiveModal = {
  token: symbol;
  panel: HTMLElement;
  returnFocusTarget: HTMLElement | null;
};

export const ModalReturnFocusTargetContext = createContext<HTMLElement | null>(null);

const activeModals: ActiveModal[] = [];
let backgroundLockCount = 0;
let backgroundSnapshots: Array<{ element: HTMLElement; inert: boolean; ariaHidden: string | null }> = [];
let previousBodyOverflow = '';

function getFocusableElements(panel: HTMLElement): HTMLElement[] {
  return Array.from(panel.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR))
    .filter((element) => !element.hidden && element.getAttribute('aria-hidden') !== 'true');
}

function syncModalInertState() {
  activeModals.forEach(({ panel }, index) => {
    const isTopModal = index === activeModals.length - 1;
    panel.inert = !isTopModal;
    if (isTopModal) panel.removeAttribute('aria-hidden');
    else panel.setAttribute('aria-hidden', 'true');
  });
}

export function registerModalLayer(
  token: symbol,
  panel: HTMLElement,
  returnFocusTarget: HTMLElement | null,
): void {
  activeModals.push({ token, panel, returnFocusTarget });
  acquireBackgroundLock();
  syncModalInertState();
}

export function unregisterModalLayer(token: symbol, panel: HTMLElement): void {
  const index = activeModals.findIndex((entry) => entry.token === token);
  if (index >= 0) activeModals.splice(index, 1);
  panel.inert = false;
  panel.removeAttribute('aria-hidden');
  syncModalInertState();
  releaseBackgroundLock();
}

export function isTopModalLayer(token: symbol): boolean {
  return activeModals[activeModals.length - 1]?.token === token;
}

function acquireBackgroundLock() {
  backgroundLockCount += 1;
  if (backgroundLockCount !== 1) return;

  backgroundSnapshots = Array.from(document.body.children)
    .filter((element): element is HTMLElement =>
      element instanceof HTMLElement && !element.hasAttribute('data-modal-root'))
    .map((element) => ({
      element,
      inert: Boolean(element.inert),
      ariaHidden: element.getAttribute('aria-hidden'),
    }));
  for (const snapshot of backgroundSnapshots) {
    snapshot.element.inert = true;
    snapshot.element.setAttribute('aria-hidden', 'true');
  }

  previousBodyOverflow = document.body.style.overflow;
  document.body.style.overflow = 'hidden';
}

function releaseBackgroundLock() {
  backgroundLockCount = Math.max(0, backgroundLockCount - 1);
  if (backgroundLockCount !== 0) return;

  for (const snapshot of backgroundSnapshots) {
    snapshot.element.inert = snapshot.inert;
    if (snapshot.ariaHidden === null) snapshot.element.removeAttribute('aria-hidden');
    else snapshot.element.setAttribute('aria-hidden', snapshot.ariaHidden);
  }
  backgroundSnapshots = [];
  document.body.style.overflow = previousBodyOverflow;
}

export interface ModalShellProps {
  visible: boolean;
  title: string;
  onClose: () => void;
  children: ReactNode;
  /** Override desktop panel width (default: uses panelDesktop CSS class). */
  desktopClassName?: string;
  /** Extra inline styles merged onto the desktop panel (applied after desktopClassName). */
  desktopStyle?: React.CSSProperties;
  /** Mobile enter translation. Defaults to a full-height bottom-sheet slide. */
  mobileEnterOffset?: number | string;
  /** Initial structural focus target after the opening gesture settles. */
  initialFocus?: 'first' | 'panel';
  /** Receives the resolved launcher so lazy shell replacements can inherit it. */
  onReturnFocusTargetCapture?: (target: HTMLElement | null) => void;
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
  mobileEnterOffset = '100%',
  initialFocus = 'first',
  onReturnFocusTargetCapture,
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
  const inheritedReturnFocusTarget = useContext(ModalReturnFocusTargetContext);
  const [mounted, setMounted] = useState(false);
  const [animIn, setAnimIn] = useState(false);
  const panelRef = useRef<HTMLDivElement>(null);
  const modalTokenRef = useRef(Symbol('modal'));
  const previousFocusRef = useRef<HTMLElement | null>(null);
  const onCloseRef = useRef(onClose);
  onCloseRef.current = onClose;
  const mobilePanelTopRef = useRef<number | null>(null);
  const rendered = visible || mounted;
  const useDesktopPanel = desktopPlacement === 'rightDrawer' || !isMobile;
  const transitionCompleteProperty = useDesktopPanel && desktopPlacement === 'rightDrawer' ? 'transform' : 'opacity';
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
      const activeElement = document.activeElement instanceof HTMLElement ? document.activeElement : null;
      const topModal = activeModals[activeModals.length - 1];
      previousFocusRef.current = (inheritedReturnFocusTarget?.isConnected ? inheritedReturnFocusTarget : null)
        ?? (topModal && (!topModal.panel.isConnected || activeElement === document.body)
          ? topModal.returnFocusTarget
          : activeElement);
      onReturnFocusTargetCapture?.(previousFocusRef.current);
      panelRef.current?.getBoundingClientRect();
      const id = requestAnimationFrame(() => setAnimIn(true));
      return () => cancelAnimationFrame(id);
    }
  }, [inheritedReturnFocusTarget, onReturnFocusTargetCapture, rendered, visible]);

  const restorePreviousFocus = useCallback((panel: HTMLElement | null) => {
    const previousFocus = previousFocusRef.current;
    previousFocusRef.current = null;
    const topOtherModal = [...activeModals]
      .reverse()
      .find((entry) => entry.token !== modalTokenRef.current);
    if (topOtherModal && (!previousFocus || !topOtherModal.panel.contains(previousFocus))) return;
    if (previousFocus?.isConnected && !panel?.contains(previousFocus)) {
      previousFocus.focus({ preventScroll: true });
    }
  }, []);

  useEffect(() => () => {
    restorePreviousFocus(panelRef.current);
  }, [restorePreviousFocus]);

  const handleTransitionEnd = useCallback((event: ReactTransitionEvent<HTMLDivElement>) => {
    if (event.target !== event.currentTarget) return;
    if (event.propertyName && event.propertyName !== transitionCompleteProperty) return;
    if (animIn) {
      onOpenComplete?.();
    } else {
      mobilePanelTopRef.current = null;
      restorePreviousFocus(panelRef.current);
      setMounted(false);
      onCloseComplete?.();
    }
  }, [animIn, onOpenComplete, onCloseComplete, restorePreviousFocus, transitionCompleteProperty]);

  useEffect(() => {
    if (!mounted || visible) return;

    const id = window.setTimeout(() => {
      mobilePanelTopRef.current = null;
      restorePreviousFocus(panelRef.current);
      setMounted(false);
      onCloseComplete?.();
    }, transitionMs + 50);

    return () => window.clearTimeout(id);
  }, [mounted, visible, transitionMs, onCloseComplete, restorePreviousFocus]);

  useEffect(() => {
    if (!visible) return;
    const panel = panelRef.current;
    if (!panel) return;

    const token = modalTokenRef.current;
    registerModalLayer(token, panel, previousFocusRef.current);

    const focusPanel = () => {
      if (panel.contains(document.activeElement)) return;
      if (initialFocus === 'panel') {
        panel.focus({ preventScroll: true });
        return;
      }
      const [firstFocusable] = getFocusableElements(panel);
      (firstFocusable ?? panel).focus({ preventScroll: true });
    };
    const focusFrame = window.requestAnimationFrame(focusPanel);
    const focusFallback = window.setTimeout(focusPanel, 100);
    const handleKey = (event: KeyboardEvent) => {
      if (activeModals[activeModals.length - 1]?.token !== token) return;
      if (event.key === 'Escape') {
        event.preventDefault();
        onCloseRef.current();
        return;
      }
      if (event.key !== 'Tab') return;

      const focusable = getFocusableElements(panel);
      if (focusable.length === 0) {
        event.preventDefault();
        panel.focus();
        return;
      }

      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      const activeElement = document.activeElement;
      if (event.shiftKey && (activeElement === first || !panel.contains(activeElement))) {
        event.preventDefault();
        last?.focus();
      } else if (!event.shiftKey && (activeElement === last || !panel.contains(activeElement))) {
        event.preventDefault();
        first?.focus();
      }
    };
    document.addEventListener('keydown', handleKey);
    return () => {
      window.cancelAnimationFrame(focusFrame);
      window.clearTimeout(focusFallback);
      document.removeEventListener('keydown', handleKey);
      unregisterModalLayer(token, panel);
    };
  }, [initialFocus, restorePreviousFocus, visible]);

  if (!rendered) return null;

  const transMs = `${transitionMs}ms`;
  const overlayTransition = `opacity ${transMs} ease`;
  const mobileTransition = `opacity ${transMs} ease, transform ${transMs} ease`;
  const desktopTransition = `opacity ${transMs} ease, transform ${transMs} ease`;
  const modalPointerEvents = visible ? 'auto' as const : 'none' as const;
  const mobileEnterTranslate = typeof mobileEnterOffset === 'number' ? `${mobileEnterOffset}px` : mobileEnterOffset;
  const computedMobileTop = vvOffsetTop + vvHeight * 0.2;
  if (!useDesktopPanel && visible && mobilePanelTopRef.current === null) {
    mobilePanelTopRef.current = computedMobileTop;
  }
  const mobilePanelTop = mobilePanelTopRef.current ?? computedMobileTop;

  const panelStyle: React.CSSProperties = !useDesktopPanel
    ? { ...css.panelMobile, transition: mobileTransition, top: mobilePanelTop, bottom: 0, transform: animIn ? 'translateY(0)' : `translateY(${mobileEnterTranslate})`, opacity: animIn ? 1 : 0, pointerEvents: modalPointerEvents }
    : desktopPlacement === 'rightDrawer'
      ? { ...css.panelDesktopRightDrawer, transition: desktopTransition, transform: animIn ? 'translateX(0)' : 'translateX(100%)', opacity: 1, pointerEvents: modalPointerEvents, ...desktopStyle }
      : { ...css.panelDesktop, transition: desktopTransition, transform: animIn ? 'translate(-50%, -50%)' : 'translate(-50%, -40%)', opacity: animIn ? 1 : 0, pointerEvents: modalPointerEvents, ...desktopStyle };

  return createPortal(
    <div data-modal-root="">
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
        tabIndex={-1}
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
    </div>,
    document.body,
  );
}
