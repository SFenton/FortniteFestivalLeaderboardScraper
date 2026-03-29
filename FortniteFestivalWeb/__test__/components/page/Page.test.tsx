import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import React, { type ReactNode } from 'react';
import Page, { pageCss } from '../../../src/pages/Page';
import { ScrollContainerProvider, useScrollContainer, useHeaderPortalRef } from '../../../src/contexts/ScrollContainerContext';

function ShellInjector({ children }: { children: ReactNode }) {
  const sRef = useScrollContainer();
  const setPortalNode = useHeaderPortalRef();

  return (
    <>
      <div ref={setPortalNode} data-testid="test-header-portal" />
      <div ref={(el) => {
        if (el && !sRef.current) {
          Object.defineProperty(el, 'scrollHeight', { value: 5000, writable: true, configurable: true });
          Object.defineProperty(el, 'scrollTop', { value: 0, writable: true, configurable: true });
          el.scrollTo = (() => {}) as any;
          sRef.current = el;
        }
      }} data-testid="test-scroll-container">
        {children}
      </div>
    </>
  );
}

function PageWrapper(props: Partial<React.ComponentProps<typeof Page>> & { children?: React.ReactNode }) {
  return (
    <ScrollContainerProvider>
      <ShellInjector>
        <MemoryRouter>
          <Page {...props}>{props.children ?? <div>Page content</div>}</Page>
        </MemoryRouter>
      </ShellInjector>
    </ScrollContainerProvider>
  );
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
});
