import { describe, it, expect, beforeAll, vi } from 'vitest';
import { render, screen, fireEvent, within } from '@testing-library/react';
import { Colors } from '@festival/theme';
import ManualPage, { MANUAL_SECTIONS } from '../../../src/pages/manual/ManualPage';
import { TestProviders } from '../../helpers/TestProviders';
import { stubElementDimensions, stubMatchMedia, stubResizeObserver, stubScrollTo } from '../../helpers/browserStubs';

beforeAll(() => {
  stubScrollTo();
  stubResizeObserver();
  stubElementDimensions();
  stubMatchMedia(false);
  if (typeof Range !== 'undefined') {
    Object.defineProperty(Range.prototype, 'getBoundingClientRect', {
      configurable: true,
      value: () => ({ top: 0, right: 120, bottom: 16, left: 0, width: 120, height: 16, x: 0, y: 0, toJSON() { return this; } }),
    });
    Object.defineProperty(Range.prototype, 'getClientRects', {
      configurable: true,
      value: () => [] as unknown as DOMRectList,
    });
  }
});

function renderManualPage() {
  return render(
    <TestProviders route="/manual">
      <ManualPage />
    </TestProviders>,
  );
}

function stubResponsiveMatchMedia({ mobile = false, wide = false }: { mobile?: boolean; wide?: boolean }) {
  const mock = vi.fn().mockImplementation((query: string) => ({
    matches: query.includes('min-width') ? wide : query.includes('max-width') ? mobile : false,
    media: query,
    onchange: null,
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    addListener: vi.fn(),
    removeListener: vi.fn(),
    dispatchEvent: vi.fn(),
  }));

  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    configurable: true,
    value: mock,
  });
}

describe('ManualPage', () => {
  it('renders the App Manual header with required sections and subsections', () => {
    stubMatchMedia(false);
    renderManualPage();

    expect(screen.getByRole('heading', { name: 'App Manual' })).toBeDefined();
    expect(screen.getByRole('heading', { name: 'Navigation Basics' })).toBeDefined();
    expect(screen.getByRole('heading', { name: 'Songs Page' })).toBeDefined();
    expect(screen.getByRole('heading', { name: 'Search, Sort, And Filter' })).toBeDefined();
    expect(screen.getByRole('heading', { name: 'Selecting Profiles' })).toBeDefined();
    expect(screen.getByRole('heading', { name: 'Selected-Profile Data And Extra Sorts' })).toBeDefined();
    expect(screen.getByRole('heading', { name: 'Player Details' })).toBeDefined();
    expect(screen.getByRole('heading', { name: 'Band Details' })).toBeDefined();
    expect(screen.getByRole('heading', { name: 'Song Detail And Detail Cards' })).toBeDefined();
    expect(screen.getByRole('heading', { name: 'Song Header And Metadata Cards' })).toBeDefined();
    expect(screen.getByRole('heading', { name: 'Sync Card And Post-Sync Data' })).toBeDefined();
    expect(screen.getByRole('heading', { name: 'Suggestions' })).toBeDefined();
    expect(screen.getByRole('heading', { name: 'Compete' })).toBeDefined();
    expect(screen.getByRole('heading', { name: 'Leaderboards And Rivals' })).toBeDefined();
    expect(screen.getByRole('heading', { name: 'Solo-Player Rivals' })).toBeDefined();
    expect(screen.getByRole('heading', { name: 'Item Shop' })).toBeDefined();
    expect(screen.getByRole('heading', { name: 'App Settings' })).toBeDefined();
  });

  it('renders a screenshot carousel for every section and subsection', () => {
    stubMatchMedia(false);
    renderManualPage();

    const expectedCarouselCount = MANUAL_SECTIONS.reduce(
      (count, section) => count + section.carousels.length + section.subsections.reduce((subCount, subsection) => subCount + subsection.carousels.length, 0),
      0,
    );

    for (const section of MANUAL_SECTIONS) {
      expect(screen.getByTestId(`manual-section-${section.id}`)).toBeDefined();
      for (const subsection of section.subsections) {
        expect(screen.getByTestId(`manual-subsection-${subsection.id}`)).toBeDefined();
      }
    }
    const carousels = screen.getAllByTestId(/^manual-carousel-/)
      .filter(element => !element.getAttribute('data-testid')?.startsWith('manual-carousel-frame-'));
    expect(carousels).toHaveLength(expectedCarouselCount);
  });

  it('cycles screenshot carousel viewports for mobile, compact, and wide captures', () => {
    stubMatchMedia(false);
    renderManualPage();

    const carousel = screen.getByTestId('manual-carousel-songs-overview');
    expect(within(carousel).getAllByText('Mobile').length).toBeGreaterThan(0);

    fireEvent.click(within(carousel).getByRole('button', { name: 'Next screenshot' }));
    expect(within(carousel).getAllByText('Compact Web').length).toBeGreaterThan(0);

    fireEvent.click(within(carousel).getByRole('button', { name: 'Next screenshot' }));
    expect(within(carousel).getAllByText('Wide Web').length).toBeGreaterThan(0);
  });

  it('swipes screenshot carousel viewports with the shared swipe interaction', () => {
    stubMatchMedia(false);
    renderManualPage();

    const carousel = screen.getByTestId('manual-carousel-songs-overview');
    const frame = screen.getByTestId('manual-carousel-frame-songs-overview');
    expect(within(carousel).getAllByText('Mobile').length).toBeGreaterThan(0);

    fireEvent.touchStart(frame, { touches: [{ clientX: 200 }] });
    fireEvent.touchEnd(frame, { changedTouches: [{ clientX: 100 }] });

    expect(within(carousel).getAllByText('Compact Web').length).toBeGreaterThan(0);
  });

  it('shows subsection quick links with a reserved icon slot on the desktop rail', () => {
    stubResponsiveMatchMedia({ wide: true });
    renderManualPage();

    const parentLink = screen.getByTestId('manual-quick-link-songs');
    const childLink = screen.getByTestId('manual-quick-link-songs-profile-sorts');
    const childIconSlot = childLink.querySelector('span[aria-hidden="true"]') as HTMLElement;

    expect(parentLink.getAttribute('data-depth')).toBe('0');
    expect(childLink.getAttribute('data-depth')).toBe('1');
    expect(childIconSlot).toBeDefined();
    expect(childIconSlot.style.visibility).toBe('hidden');
    expect(screen.getByRole('navigation', { name: 'Manual Sections' })).toBeDefined();
  });

  it('uses white aligned section icons, bright text, and no section divider bars', () => {
    stubMatchMedia(false);
    renderManualPage();

    const section = screen.getByTestId('manual-section-songs');
    const subsection = screen.getByTestId('manual-subsection-songs-profile-sorts');
    const icon = screen.getByTestId('manual-section-icon-songs');
    const paragraph = section.querySelector('p') as HTMLElement;
    const subsectionParagraph = subsection.querySelector('p') as HTMLElement;

    expect(icon.style.color).toBe('rgb(255, 255, 255)');
    expect(icon.style.width).toBe('32px');
    expect(icon.style.height).toBe('32px');
    expect(icon.style.borderRadius).toBe('');
    expect(section.style.borderTop).toBe('');
    expect(subsection.style.borderLeft).toBe('');
    expect(paragraph.style.color).toBe('rgb(255, 255, 255)');
    expect(subsectionParagraph.style.color).toBe('rgb(255, 255, 255)');
  });

  it('hides the duplicate App Manual page header on mobile chrome', () => {
    stubResponsiveMatchMedia({ mobile: true });
    renderManualPage();

    expect(screen.queryByRole('heading', { name: 'App Manual' })).toBeNull();
    expect(screen.getByRole('heading', { name: 'Songs Page' })).toBeDefined();
  });

  it('opens subsection quick links from the compact header action', () => {
    stubMatchMedia(false);
    renderManualPage();

    fireEvent.click(screen.getByRole('button', { name: 'Manual Sections' }));

    const modalList = screen.getByTestId('manual-quick-links-modal-list');
    expect(within(modalList).getByTestId('manual-quick-link-navigation').getAttribute('data-depth')).toBe('0');
    expect(within(modalList).getByTestId('manual-quick-link-navigation-quick-links').getAttribute('data-depth')).toBe('1');
  });

  it('keeps manual copy focused on app browsing instead of operations', () => {
    stubMatchMedia(false);
    renderManualPage();

    const content = (document.body.textContent ?? '').toLowerCase();
    expect(content).not.toMatch(/\bservice\b|\bbackend\b|\bapi\b|\bscraper\b|\bworker\b/);
  });
});