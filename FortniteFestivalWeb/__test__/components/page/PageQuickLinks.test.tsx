import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { useEffect } from 'react';
import { PageQuickLinksModal } from '../../../src/components/page/PageQuickLinks';
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
});
