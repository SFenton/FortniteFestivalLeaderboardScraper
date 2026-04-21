/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useCallback, useEffect, useRef, useState } from 'react';
import { playerPageStyles as pps } from '../player/playerPageStyles';
import ModalShell from '../modals/components/ModalShell';
import { modalStyles } from '../modals/modalStyles';
import { FADE_DURATION, Gap } from '@festival/theme';
import { useScrollMask } from '../../hooks/ui/useScrollMask';
import { getPageQuickLinkTestId, type PageQuickLinkItem } from '../../hooks/ui/usePageQuickLinks';

const QUICK_LINKS_MODAL_DESKTOP_STYLE = {
  width: 420,
  maxWidth: '90vw',
  height: 520,
  maxHeight: '70vh',
};

const QUICK_LINKS_MODAL_LIST_STYLE = {
  display: 'flex',
  flexDirection: 'column',
  gap: Gap.xs,
} as const;

type PageQuickLinksButtonsProps<T extends PageQuickLinkItem> = {
  items: readonly T[];
  activeItemId: string | null;
  onSelect: (item: T) => void;
  testIdPrefix?: string;
};

export type PageQuickLinksConfig<T extends PageQuickLinkItem = PageQuickLinkItem> = {
  title: string;
  items: readonly T[];
  activeItemId: string | null;
  visible: boolean;
  onOpen: () => void;
  onClose: () => void;
  onSelect: (item: T) => void;
  maxHeight?: number | null;
  desktopRailRevealDelayMs?: number;
  testIdPrefix?: string;
};

function PageQuickLinksButtons<T extends PageQuickLinkItem>({ items, activeItemId, onSelect, testIdPrefix = 'page' }: PageQuickLinksButtonsProps<T>) {
  return (
    <>
      {items.map((item) => {
        const isActive = item.id === activeItemId;
        const hasIcon = item.icon != null;
        return (
          <button
            key={item.id}
            type="button"
            data-testid={`${testIdPrefix}-quick-link-${getPageQuickLinkTestId(item.id)}`}
            aria-label={item.landmarkLabel}
            aria-current={isActive ? 'location' : undefined}
            style={isActive ? pps.quickLinkButtonActive : pps.quickLinkButton}
            onClick={() => onSelect(item)}
          >
            {hasIcon ? <span style={pps.quickLinkIcon} aria-hidden="true">{item.icon}</span> : null}
            <span style={pps.quickLinkLabel}>{item.label}</span>
          </button>
        );
      })}
    </>
  );
}

export function PageQuickLinksRail<T extends PageQuickLinkItem>({ quickLinks }: { quickLinks: PageQuickLinksConfig<T>; }) {
  const testIdPrefix = quickLinks.testIdPrefix ?? 'page';
  const revealDelayMs = quickLinks.desktopRailRevealDelayMs ?? 0;
  const [activeRevealDelayMs, setActiveRevealDelayMs] = useState(revealDelayMs > 0 ? revealDelayMs : 0);
  const [railRevealed, setRailRevealed] = useState(revealDelayMs <= 0);
  const previousRevealDelayMsRef = useRef(revealDelayMs);

  useEffect(() => {
    const previousRevealDelayMs = previousRevealDelayMsRef.current;
    previousRevealDelayMsRef.current = revealDelayMs;

    if (revealDelayMs <= 0) {
      if (activeRevealDelayMs <= 0) {
        setRailRevealed(true);
      }
      return;
    }

    if (previousRevealDelayMs <= 0 || previousRevealDelayMs !== revealDelayMs) {
      setActiveRevealDelayMs(revealDelayMs);
      setRailRevealed(false);
    }
  }, [activeRevealDelayMs, revealDelayMs]);

  useEffect(() => {
    if (railRevealed || activeRevealDelayMs <= 0) {
      return;
    }

    const revealTimeoutId = window.setTimeout(() => {
      setActiveRevealDelayMs(0);
      setRailRevealed(true);
    }, activeRevealDelayMs + FADE_DURATION);

    return () => {
      window.clearTimeout(revealTimeoutId);
    };
  }, [activeRevealDelayMs, railRevealed]);

  const handleRailAnimationEnd = useCallback(() => {
    setActiveRevealDelayMs(0);
    setRailRevealed(true);
  }, []);

  const railStyle = !railRevealed && activeRevealDelayMs > 0
    ? {
      ...pps.quickLinksOverlay,
      opacity: 0,
      pointerEvents: 'none' as const,
      willChange: 'opacity',
      animation: `fadeIn ${FADE_DURATION}ms ease-out ${activeRevealDelayMs}ms forwards`,
    }
    : pps.quickLinksOverlay;

  return (
    <div style={railStyle} data-testid={`${testIdPrefix}-quick-links-rail`} onAnimationEnd={handleRailAnimationEnd}>
      <nav
        style={{
          ...pps.quickLinksSticky,
          ...(quickLinks.maxHeight ? { maxHeight: `${quickLinks.maxHeight}px` } : {}),
        }}
        aria-label={quickLinks.title}
      >
        <PageQuickLinksButtons
          items={quickLinks.items}
          activeItemId={quickLinks.activeItemId}
          onSelect={quickLinks.onSelect}
          testIdPrefix={quickLinks.testIdPrefix}
        />
      </nav>
    </div>
  );
}

export function PageQuickLinksModal<T extends PageQuickLinkItem>({ quickLinks }: { quickLinks: PageQuickLinksConfig<T>; }) {
  const scrollRef = useRef<HTMLDivElement>(null);
  const updateScrollMask = useScrollMask(scrollRef, [quickLinks.visible, quickLinks.items.length, quickLinks.activeItemId], { selfScroll: true });
  const handleContentScroll = useCallback(() => { updateScrollMask(); }, [updateScrollMask]);
  const testIdPrefix = quickLinks.testIdPrefix ?? 'page';

  return (
    <ModalShell
      visible={quickLinks.visible}
      title={quickLinks.title}
      onClose={quickLinks.onClose}
      desktopStyle={QUICK_LINKS_MODAL_DESKTOP_STYLE}
    >
      <div ref={scrollRef} onScroll={handleContentScroll} style={modalStyles.contentScroll}>
        <nav aria-label={quickLinks.title} style={QUICK_LINKS_MODAL_LIST_STYLE} data-testid={`${testIdPrefix}-quick-links-modal-list`}>
          <PageQuickLinksButtons
            items={quickLinks.items}
            activeItemId={quickLinks.activeItemId}
            onSelect={quickLinks.onSelect}
            testIdPrefix={testIdPrefix}
          />
        </nav>
      </div>
    </ModalShell>
  );
}