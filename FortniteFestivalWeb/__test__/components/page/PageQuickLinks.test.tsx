import { describe, it, expect, vi } from 'vitest';
import { act, render, screen, fireEvent } from '@testing-library/react';
import { useEffect } from 'react';
import { PageQuickLinksModal, PageQuickLinksRail } from '../../../src/components/page/PageQuickLinks';
import { FADE_DURATION } from '@festival/theme';
import { TestProviders } from '../../helpers/TestProviders';

vi.mock('../../../src/hooks/ui/useScrollMask', () => ({
  useScrollMask: () => vi.fn(),
}));

vi.mock('../../../src/components/modals/components/ModalShell', () => ({
  default: ({ visible, title, children, onOpenComplete, onCloseComplete }: {
    visible: boolean; title: string; children: React.ReactNode;
    onOpenComplete?: () => void; onCloseComplete?: () => void;
  }) => {
    useEffect(() => {
      if (visible) onOpenComplete?.();
      else onCloseComplete?.();
    }, [visible, onOpenComplete, onCloseComplete]);
    if (!visible) return null;
    return <div role="dialog" aria-label={title}><h2>{title}</h2>{children}</div>;
  },
}));

describe('PageQuickLinksModal', () => {
  it('pads quick-link content above mobile safe-area bottoms', () => {
    render(
      <TestProviders>
        <PageQuickLinksModal
          quickLinks={{
            title: 'Jump to section',
            items: [{ id: 'summary', label: 'Summary', landmarkLabel: 'Summary section' }],
            activeItemId: null,
            visible: true,
            onOpen: vi.fn(),
            onClose: vi.fn(),
            onSelect: vi.fn(),
          }}
        />
      </TestProviders>,
    );

    const list = screen.getByTestId('page-quick-links-modal-list');
    const content = list.parentElement as HTMLElement;
    expect(content.style.padding).toContain('safe-area-inset-bottom');
  });

  describe('PageQuickLinksRail reveal ownership', () => {
    it('keeps the root disabled until reveal and resets for a new delayed reveal', () => {
      vi.useFakeTimers();
      const quickLinks = {
        title: 'Quick Links',
        items: [{ id: 'summary', label: 'Summary', landmarkLabel: 'Summary' }],
        activeItemId: null,
        visible: false,
        onOpen: vi.fn(),
        onClose: vi.fn(),
        onSelect: vi.fn(),
        desktopRailRevealDelayMs: 100,
        maxHeight: 420,
      };
      const { rerender, unmount } = render(<PageQuickLinksRail quickLinks={quickLinks} />);
      try {
        const rail = screen.getByTestId('page-quick-links-rail');
        expect(rail.style.pointerEvents).toBe('none');
        expect(screen.getByRole('navigation')).toHaveStyle({ maxHeight: '420px' });
        act(() => vi.advanceTimersByTime(100 + FADE_DURATION - 1));
        expect(rail.style.pointerEvents).toBe('none');
        act(() => vi.advanceTimersByTime(1));
        expect(rail.style.pointerEvents).toBe('');
        rerender(<PageQuickLinksRail quickLinks={{ ...quickLinks, desktopRailRevealDelayMs: 200 }} />);
        expect(rail.style.pointerEvents).toBe('none');
        act(() => vi.advanceTimersByTime(200 + FADE_DURATION));
        expect(rail.style.pointerEvents).toBe('');
      } finally {
        unmount();
        vi.useRealTimers();
      }
    });
  });

  it('selects modal quick links from touch pointerup without double firing on click', () => {
    const onSelect = vi.fn();
    render(
      <TestProviders>
        <PageQuickLinksModal
          quickLinks={{
            title: 'Jump to section',
            items: [{ id: 'summary', label: 'Summary', landmarkLabel: 'Summary section' }],
            activeItemId: null,
            visible: true,
            onOpen: vi.fn(),
            onClose: vi.fn(),
            onSelect,
          }}
        />
      </TestProviders>,
    );

    const quickLink = screen.getByTestId('page-quick-link-summary');
    fireEvent.pointerDown(quickLink, { pointerId: 1, pointerType: 'touch', button: 0, clientX: 72, clientY: 240 });
    fireEvent.pointerUp(quickLink, { pointerId: 1, pointerType: 'touch', button: 0, clientX: 72, clientY: 241 });

    expect(onSelect).toHaveBeenCalledTimes(1);
    expect(onSelect).toHaveBeenCalledWith(expect.objectContaining({ id: 'summary' }));

    fireEvent.click(quickLink);
    expect(onSelect).toHaveBeenCalledTimes(1);
  });
});
