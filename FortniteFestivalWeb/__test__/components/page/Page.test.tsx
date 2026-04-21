import { describe, it, expect, vi } from 'vitest';
import { act, createEvent, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import React, { type ReactNode } from 'react';
import { FADE_DURATION, Layout, MaxWidth } from '@festival/theme';
import Page, { pageCss } from '../../../src/pages/Page';
import { ScrollContainerProvider, useScrollContainer, useHeaderPortalRef, useQuickLinksRailPortalRef } from '../../../src/contexts/ScrollContainerContext';
import { PageQuickLinksProvider, usePageQuickLinksController } from '../../../src/contexts/PageQuickLinksContext';

function setViewportQueries({ mobile = false, wide = false }: { mobile?: boolean; wide?: boolean } = {}) {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    configurable: true,
    value: vi.fn().mockImplementation((query: string) => ({
      matches: query.includes('max-width') ? mobile : query.includes('min-width') ? wide : false,
      media: query,
      onchange: null,
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      addListener: vi.fn(),
      removeListener: vi.fn(),
      dispatchEvent: vi.fn(),
    })),
  });
}

function ShellInjector({ children }: { children: ReactNode }) {
  const sRef = useScrollContainer();
  const setPortalNode = useHeaderPortalRef();
  const setQuickLinksRailNode = useQuickLinksRailPortalRef();

  return (
    <>
      <div ref={setPortalNode} data-testid="test-header-portal" />
      <div ref={(el) => {
        if (el && !sRef.current) {
          Object.defineProperty(el, 'clientHeight', { value: 500, writable: true, configurable: true });
          Object.defineProperty(el, 'scrollHeight', { value: 5000, writable: true, configurable: true });
          Object.defineProperty(el, 'scrollTop', { value: 0, writable: true, configurable: true });
          el.scrollTo = (() => {}) as any;
          sRef.current = el;
        }
      }} data-testid="test-scroll-container">
        {children}
      </div>
      <div ref={(el) => {
        if (el) {
          Object.defineProperty(el, 'clientHeight', { value: 620, writable: true, configurable: true });
          setQuickLinksRailNode(el);
          return;
        }

        setQuickLinksRailNode(null);
      }} data-testid="test-quick-links-portal" />
    </>
  );
}

function PageWrapper(props: Partial<React.ComponentProps<typeof Page>> & { children?: React.ReactNode }) {
  return (
    <PageQuickLinksProvider>
      <ScrollContainerProvider>
        <ShellInjector>
          <MemoryRouter>
            <Page {...props}>{props.children ?? <div>Page content</div>}</Page>
          </MemoryRouter>
        </ShellInjector>
      </ScrollContainerProvider>
    </PageQuickLinksProvider>
  );
}

function PageQuickLinksSpy() {
  const pageQuickLinks = usePageQuickLinksController();
  return <span data-testid="page-quick-links-title">{pageQuickLinks.pageQuickLinks?.title ?? ''}</span>;
}

describe('Page', () => {
  it('renders children', () => {
    render(<PageWrapper><div>Test content</div></PageWrapper>);
    expect(screen.getByText('Test content')).toBeDefined();
  });

  it('renders before and after slots', () => {
    render(<PageWrapper before={<div>Before</div>} after={<div>After</div>}><div>Main</div></PageWrapper>);
    expect(screen.getByText('Before')).toBeDefined();
    expect(screen.getByText('After')).toBeDefined();
    expect(screen.getByText('Main')).toBeDefined();
  });

  it('renders with custom className', () => {
    const { container } = render(<PageWrapper className="custom"><div>C</div></PageWrapper>);
    expect(container.querySelector('.custom')).toBeTruthy();
  });

  it('renders fabSpacer at end of scroll area by default', () => {
    const { container } = render(<PageWrapper><div>Content</div></PageWrapper>);
    const scrollArea = container.querySelector('[data-testid="scroll-area"]')!;
    const spacer = scrollArea.lastElementChild as HTMLElement;
    expect(spacer.style.height).toBe(`${pageCss.fabSpacer.height}px`);
    expect(spacer.style.flexShrink).toBe('0');
  });

  it('omits fabSpacer when fabSpacer="none"', () => {
    const { container } = render(<PageWrapper fabSpacer="none"><div>Content</div></PageWrapper>);
    const scrollArea = container.querySelector('[data-testid="scroll-area"]')!;
    const lastChild = scrollArea.lastElementChild as HTMLElement;
    expect(lastChild.style.height).not.toBe(`${pageCss.fabSpacer.height}px`);
  });

  it('applies marginBottom to scroll container when fabSpacer="fixed"', () => {
    render(<PageWrapper fabSpacer="fixed"><div>Content</div></PageWrapper>);
    const scrollContainer = screen.getByTestId('test-scroll-container');
    expect(scrollContainer.style.marginBottom).toBe(`${pageCss.fabSpacer.height}px`);
  });

  it('registers page quick links when provided', () => {
    const quickLinks = {
      title: 'Quick Links',
      items: [{ id: 'alpha', label: 'Alpha', landmarkLabel: 'Alpha', icon: <span>A</span> }],
      activeItemId: 'alpha',
      visible: false,
      onOpen: () => {},
      onClose: () => {},
      onSelect: () => {},
      testIdPrefix: 'page',
    };

    render(
      <PageWrapper quickLinks={quickLinks}>
        <PageQuickLinksSpy />
      </PageWrapper>,
    );

    expect(screen.getByTestId('page-quick-links-title').textContent).toBe('Quick Links');
  });

  it('allocates a dedicated desktop rail lane when wide quick links are visible', () => {
    setViewportQueries({ mobile: false, wide: true });

    const quickLinks = {
      title: 'Quick Links',
      items: [{ id: 'alpha', label: 'Alpha', landmarkLabel: 'Alpha', icon: <span>A</span> }],
      activeItemId: 'alpha',
      visible: false,
      onOpen: () => {},
      onClose: () => {},
      onSelect: () => {},
      testIdPrefix: 'page',
    };

    const { container } = render(<PageWrapper quickLinks={quickLinks}><div>Page content</div></PageWrapper>);

    const scrollArea = container.querySelector('[data-testid="scroll-area"]') as HTMLElement;
    const pageContainer = scrollArea.firstElementChild as HTMLElement;
    const pageRoot = screen.getByTestId('page-root');
    const portal = screen.getByTestId('test-quick-links-portal');
    const scrollContainer = screen.getByTestId('test-scroll-container');
    const rail = screen.getByTestId('page-quick-links-rail');
    const nav = screen.getByRole('navigation', { name: 'Quick Links' });

    expect(nav).toBeDefined();
    expect(pageContainer.style.maxWidth).toBe(`${MaxWidth.card}px`);
    expect(pageRoot).toContainElement(scrollArea);
    expect(pageRoot).not.toContainElement(rail);
    expect(scrollContainer).not.toContainElement(rail);
    expect(portal).toContainElement(rail);
    expect(rail).toHaveStyle({ width: `${Layout.sidebarWidth}px` });
    expect(nav).toHaveStyle({ overscrollBehavior: 'contain', paddingTop: '8px', paddingLeft: '8px', boxSizing: 'border-box', maxHeight: '620px' });
  });

  it('delays the wide desktop rail reveal as a single fade when configured', () => {
    setViewportQueries({ mobile: false, wide: true });

    const quickLinks = {
      title: 'Quick Links',
      items: [{ id: 'alpha', label: 'Alpha', landmarkLabel: 'Alpha', icon: <span>A</span> }],
      activeItemId: 'alpha',
      visible: false,
      onOpen: () => {},
      onClose: () => {},
      onSelect: () => {},
      desktopRailRevealDelayMs: 750,
      testIdPrefix: 'page',
    };

    render(<PageWrapper quickLinks={quickLinks}><div>Page content</div></PageWrapper>);

    const rail = screen.getByTestId('page-quick-links-rail');

    expect(rail).toHaveStyle({ opacity: '0', pointerEvents: 'none' });
    expect(rail.style.animation).toContain('fadeIn');
    expect(rail.style.animation).toContain('750ms');
  });

  it('fades the rail in immediately when mounted with a zero reveal delay', async () => {
    vi.useFakeTimers();
    try {
      setViewportQueries({ mobile: false, wide: true });

      const quickLinks = {
        title: 'Quick Links',
        items: [{ id: 'alpha', label: 'Alpha', landmarkLabel: 'Alpha', icon: <span>A</span> }],
        activeItemId: 'alpha',
        visible: false,
        onOpen: () => {},
        onClose: () => {},
        onSelect: () => {},
        desktopRailRevealDelayMs: 0,
        testIdPrefix: 'page',
      };

      render(<PageWrapper quickLinks={quickLinks}><div>Page content</div></PageWrapper>);

      const rail = screen.getByTestId('page-quick-links-rail');
      expect(rail).toHaveStyle({ opacity: '0', pointerEvents: 'none' });
      expect(rail.style.animation).toContain('fadeIn');

      await act(async () => {
        await vi.advanceTimersByTimeAsync(FADE_DURATION + 50);
      });

      const revealedRail = screen.getByTestId('page-quick-links-rail');
      expect(revealedRail.style.pointerEvents).not.toBe('none');
      expect(revealedRail.style.opacity).not.toBe('0');
      expect(revealedRail.style.animation).toBe('');
    } finally {
      vi.useRealTimers();
    }
  });

  it('keeps the rail interactive after reveal and updates the active item without re-hiding', async () => {
    vi.useFakeTimers();
    try {
      setViewportQueries({ mobile: false, wide: true });

      const quickLinks = {
        title: 'Quick Links',
        items: [
          { id: 'alpha', label: 'Alpha', landmarkLabel: 'Alpha', icon: <span>A</span> },
          { id: 'beta', label: 'Beta', landmarkLabel: 'Beta', icon: <span>B</span> },
        ],
        activeItemId: 'alpha',
        visible: false,
        onOpen: () => {},
        onClose: () => {},
        onSelect: () => {},
        desktopRailRevealDelayMs: 750,
        testIdPrefix: 'page',
      };

      const { rerender } = render(<PageWrapper quickLinks={quickLinks}><div>Page content</div></PageWrapper>);

      const rail = screen.getByTestId('page-quick-links-rail');
      expect(rail).toHaveStyle({ opacity: '0', pointerEvents: 'none' });

      await act(async () => {
        await vi.advanceTimersByTimeAsync(750 + FADE_DURATION + 50);
      });

      const revealedRail = screen.getByTestId('page-quick-links-rail');
      expect(revealedRail.style.pointerEvents).not.toBe('none');
      expect(revealedRail.style.opacity).not.toBe('0');
      expect(revealedRail.style.animation).toBe('');
      expect(screen.getByTestId('page-quick-link-alpha')).toHaveAttribute('aria-current', 'location');

      rerender(
        <PageWrapper quickLinks={{ ...quickLinks, activeItemId: 'beta' }}>
          <div>Page content</div>
        </PageWrapper>,
      );

      const updatedRail = screen.getByTestId('page-quick-links-rail');
      expect(updatedRail.style.pointerEvents).not.toBe('none');
      expect(updatedRail.style.opacity).not.toBe('0');
      expect(updatedRail.style.animation).toBe('');
      expect(screen.getByTestId('page-quick-link-beta')).toHaveAttribute('aria-current', 'location');
      expect(screen.getByTestId('page-quick-link-alpha')).not.toHaveAttribute('aria-current');
    } finally {
      vi.useRealTimers();
    }
  });

  it('starts an immediate fade when a pending reveal delay collapses to zero', async () => {
    vi.useFakeTimers();
    try {
      setViewportQueries({ mobile: false, wide: true });

      const quickLinks = {
        title: 'Quick Links',
        items: [{ id: 'alpha', label: 'Alpha', landmarkLabel: 'Alpha', icon: <span>A</span> }],
        activeItemId: 'alpha',
        visible: false,
        onOpen: () => {},
        onClose: () => {},
        onSelect: () => {},
        desktopRailRevealDelayMs: 750,
        testIdPrefix: 'page',
      };

      const { rerender } = render(<PageWrapper quickLinks={quickLinks}><div>Page content</div></PageWrapper>);

      expect(screen.getByTestId('page-quick-links-rail')).toHaveStyle({ opacity: '0', pointerEvents: 'none' });

      rerender(
        <PageWrapper quickLinks={{ ...quickLinks, desktopRailRevealDelayMs: 0 }}>
          <div>Page content</div>
        </PageWrapper>,
      );

      const rail = screen.getByTestId('page-quick-links-rail');
      expect(rail).toHaveStyle({ opacity: '0', pointerEvents: 'none' });
      expect(rail.style.animation).toContain('fadeIn');

      await act(async () => {
        await vi.advanceTimersByTimeAsync(FADE_DURATION + 50);
      });

      const revealedRail = screen.getByTestId('page-quick-links-rail');
      expect(revealedRail.style.pointerEvents).not.toBe('none');
      expect(revealedRail.style.opacity).not.toBe('0');
      expect(revealedRail.style.animation).toBe('');
    } finally {
      vi.useRealTimers();
    }
  });

  it('keeps wheel input over the rail isolated from the shell scroll owner', () => {
    setViewportQueries({ mobile: false, wide: true });

    const quickLinks = {
      title: 'Quick Links',
      items: [{ id: 'alpha', label: 'Alpha', landmarkLabel: 'Alpha', icon: <span>A</span> }],
      activeItemId: 'alpha',
      visible: false,
      onOpen: () => {},
      onClose: () => {},
      onSelect: () => {},
      testIdPrefix: 'page',
    };

    render(<PageWrapper quickLinks={quickLinks}><div>Page content</div></PageWrapper>);

    const nav = screen.getByRole('navigation', { name: 'Quick Links' }) as HTMLElement;
    Object.defineProperty(nav, 'scrollTop', { value: 180, writable: true, configurable: true });
    const scrollContainer = screen.getByTestId('test-scroll-container') as HTMLElement;
    Object.defineProperty(scrollContainer, 'scrollTop', { value: 700, writable: true, configurable: true });

    const ordinaryEvent = createEvent.wheel(nav, { deltaY: 40 });
    fireEvent(nav, ordinaryEvent);

    expect(nav.scrollTop).toBe(180);
    expect(scrollContainer.scrollTop).toBe(700);
    expect(ordinaryEvent.defaultPrevented).toBe(false);
  });

  it('keeps shell scrolling from mutating the rail state', () => {
    setViewportQueries({ mobile: false, wide: true });

    const quickLinks = {
      title: 'Quick Links',
      items: [{ id: 'alpha', label: 'Alpha', landmarkLabel: 'Alpha', icon: <span>A</span> }],
      activeItemId: 'alpha',
      visible: false,
      onOpen: () => {},
      onClose: () => {},
      onSelect: () => {},
      testIdPrefix: 'page',
    };

    render(<PageWrapper quickLinks={quickLinks}><div>Page content</div></PageWrapper>);

    const nav = screen.getByRole('navigation', { name: 'Quick Links' }) as HTMLElement;
    Object.defineProperty(nav, 'scrollTop', { value: 180, writable: true, configurable: true });
    const scrollContainer = screen.getByTestId('test-scroll-container') as HTMLElement;
    Object.defineProperty(scrollContainer, 'scrollTop', { value: 700, writable: true, configurable: true });

    const shellEvent = createEvent.wheel(scrollContainer, { deltaY: -60 });
    fireEvent(scrollContainer, shellEvent);

    expect(nav.scrollTop).toBe(180);
    expect(scrollContainer.scrollTop).toBe(700);
    expect(shellEvent.defaultPrevented).toBe(false);
  });
});
