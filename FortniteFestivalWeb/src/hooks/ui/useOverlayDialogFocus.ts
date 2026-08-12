import { useEffect, useLayoutEffect, useRef, type RefObject } from 'react';
import {
  isTopModalLayer,
  registerModalLayer,
  unregisterModalLayer,
} from '../../components/modals/components/ModalShell';

const FOCUSABLE_SELECTOR = [
  'button:not([disabled])',
  'a[href]',
  'input:not([disabled])',
  'select:not([disabled])',
  'textarea:not([disabled])',
  '[tabindex]:not([tabindex="-1"])',
].join(',');

export function useOverlayDialogFocus(
  onDismiss: () => void,
): RefObject<HTMLDivElement | null> {
  const panelRef = useRef<HTMLDivElement>(null);
  const previousFocusRef = useRef<HTMLElement | null>(null);
  const modalTokenRef = useRef(Symbol('overlay-dialog'));

  useLayoutEffect(() => {
    previousFocusRef.current = document.activeElement instanceof HTMLElement
      ? document.activeElement
      : null;
    const panel = panelRef.current;
    if (!panel) return;
    const token = modalTokenRef.current;
    registerModalLayer(token, panel, previousFocusRef.current);
    const frame = requestAnimationFrame(() => {
      const target = panel?.querySelector<HTMLElement>(FOCUSABLE_SELECTOR) ?? panel;
      target?.focus();
    });

    return () => {
      cancelAnimationFrame(frame);
      unregisterModalLayer(token, panel);
      previousFocusRef.current?.focus();
      previousFocusRef.current = null;
    };
  }, []);

  useEffect(() => {
    const handleKeyDown = (event: KeyboardEvent) => {
      const panel = panelRef.current;
      if (!panel) return;
      if (!isTopModalLayer(modalTokenRef.current)) return;
      if (event.key === 'Escape') {
        event.preventDefault();
        onDismiss();
        return;
      }
      if (event.key !== 'Tab') return;

      const focusable = Array.from(panel.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR))
        .filter(element => !element.hasAttribute('disabled'));
      if (focusable.length === 0) {
        event.preventDefault();
        panel.focus();
        return;
      }

      const first = focusable[0]!;
      const last = focusable[focusable.length - 1]!;
      if (event.shiftKey && document.activeElement === first) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first.focus();
      }
    };

    document.addEventListener('keydown', handleKeyDown);
    return () => document.removeEventListener('keydown', handleKeyDown);
  }, [onDismiss]);

  return panelRef;
}
