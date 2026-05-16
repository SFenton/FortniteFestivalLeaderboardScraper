import { describe, it, expect, vi, beforeAll } from 'vitest';
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { PageQuickLinksProvider } from '../../../src/contexts/PageQuickLinksContext';
import { ScrollContainerProvider, useHeaderPortalRef, useQuickLinksRailPortalRef, useScrollContainer } from '../../../src/contexts/ScrollContainerContext';
import LicensesPage from '../../../src/pages/settings/LicensesPage';
import { stubResizeObserver, stubScrollTo, stubElementDimensions } from '../../helpers/browserStubs';

beforeAll(() => {
  stubScrollTo();
  stubResizeObserver();
  stubElementDimensions();
  if (typeof Range !== 'undefined') {
    const rect = { top: 0, left: 0, bottom: 16, right: 120, width: 120, height: 16, x: 0, y: 0, toJSON() { return this; } };
    Object.defineProperty(Range.prototype, 'getBoundingClientRect', {
      configurable: true,
      value: () => rect,
    });
    Object.defineProperty(Range.prototype, 'getClientRects', {
      configurable: true,
      value: () => [] as unknown as DOMRectList,
    });
  }
  Object.defineProperty(window, 'requestAnimationFrame', {
    configurable: true,
    value: (callback: FrameRequestCallback) => {
      callback(0);
      return 0;
    },
  });
  Object.defineProperty(window, 'cancelAnimationFrame', {
    configurable: true,
    value: vi.fn(),
  });
});

function ShellRefInjector({ children }: { children: React.ReactNode }) {
  const scrollRef = useScrollContainer();
  const setPortalNode = useHeaderPortalRef();
  const setQuickLinksRailNode = useQuickLinksRailPortalRef();

  return (
    <>
      <div ref={setPortalNode} data-testid="test-header-portal" />
      <div
        ref={(element) => {
          if (element && !scrollRef.current) {
            Object.defineProperty(element, 'scrollHeight', { value: 5000, writable: true, configurable: true });
            Object.defineProperty(element, 'scrollTop', { value: 0, writable: true, configurable: true });
            Object.defineProperty(element, 'clientHeight', { value: 800, writable: true, configurable: true });
            element.scrollTo = (() => {}) as any;
            scrollRef.current = element;
          }
        }}
        data-testid="test-scroll-container"
      >
        {children}
      </div>
      <div ref={setQuickLinksRailNode} data-testid="test-quick-links-portal" />
    </>
  );
}

function renderLicensesPage() {
  return render(
    <ScrollContainerProvider>
      <ShellRefInjector>
        <MemoryRouter initialEntries={["/settings/licenses"]}>
          <PageQuickLinksProvider>
            <LicensesPage />
          </PageQuickLinksProvider>
        </MemoryRouter>
      </ShellRefInjector>
    </ScrollContainerProvider>,
  );
}

describe('LicensesPage', () => {
  it('renders the generated package manifest', () => {
    renderLicensesPage();

    expect(screen.getByRole('button', { name: /@babel\/core 7\.29\.0 MIT/ })).toBeDefined();
    expect(screen.getByRole('button', { name: /Npgsql 9\.0\.3 PostgreSQL/ })).toBeDefined();
  });

  it('opens and closes a package license modal', async () => {
    renderLicensesPage();

    fireEvent.click(screen.getByRole('button', { name: /Npgsql 9\.0\.3 PostgreSQL/ }));

    const dialog = await screen.findByRole('dialog', { name: 'Npgsql · PostgreSQL' });
    const licenseBody = within(dialog).getByText(/PostgreSQL License/);
    expect(licenseBody).toBeDefined();
    expect(within(dialog).getByText(/Permission to use, copy, modify, and distribute/)).toBeDefined();
    expect(licenseBody.parentElement?.style.maskImage).toContain('linear-gradient');

    fireEvent.click(within(dialog).getByRole('button', { name: 'Close' }));
    fireEvent.transitionEnd(dialog);

    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Npgsql · PostgreSQL' })).toBeNull();
    });
  });
});
