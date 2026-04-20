/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useCallback, useRef } from 'react';
import { playerPageStyles as pps } from '../player/playerPageStyles';
import ModalShell from '../modals/components/ModalShell';
import { modalStyles } from '../modals/modalStyles';
import { Gap } from '@festival/theme';
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

  return (
    <div style={pps.quickLinksOverlay} data-testid={`${testIdPrefix}-quick-links-rail`}>
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