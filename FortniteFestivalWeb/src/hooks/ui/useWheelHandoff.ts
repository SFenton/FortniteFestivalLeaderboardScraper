import { useEffect, type RefObject } from 'react';
import { useScrollContainer } from '../../contexts/ScrollContainerContext';

const pageWheelOwners = new WeakSet<HTMLElement>();
const needsHandoffFence = () => navigator.userAgent.includes('Firefox/');

/** Firefox can retain a page wheel transaction after the pointer enters overlay chrome. */
export function usePageWheelHandoff(scrollRef: RefObject<HTMLElement | null>): void {
  useEffect(() => {
    const page = scrollRef.current;
    const body = page?.parentElement;
    if (!page || !body || !needsHandoffFence()) return;
    const rememberPage = (event: WheelEvent) => {
      if (!event.ctrlKey) pageWheelOwners.add(page);
    };
    const reset = () => pageWheelOwners.delete(page);
    page.addEventListener('wheel', rememberPage, { passive: true });
    body.addEventListener('pointerdown', reset, { passive: true });
    body.addEventListener('keydown', reset, { passive: true });
    return () => {
      reset();
      page.removeEventListener('wheel', rememberPage);
      body.removeEventListener('pointerdown', reset);
      body.removeEventListener('keydown', reset);
    };
  }, [scrollRef]);
}

export function usePanelWheelHandoff(panelRef: RefObject<HTMLElement | null>): void {
  const scrollRef = useScrollContainer();
  useEffect(() => {
    const panel = panelRef.current;
    const page = scrollRef.current;
    if (!panel || !page || !needsHandoffFence()) return;
    const handoff = (event: WheelEvent) => {
      if (event.ctrlKey) {
        pageWheelOwners.delete(page);
      } else if (event.cancelable && pageWheelOwners.has(page)) {
        event.preventDefault();
        pageWheelOwners.delete(page);
      }
    };
    panel.addEventListener('wheel', handoff, { passive: false });
    return () => panel.removeEventListener('wheel', handoff);
  }, [panelRef, scrollRef]);
}
