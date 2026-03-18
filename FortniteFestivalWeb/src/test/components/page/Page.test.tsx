import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { useRef } from 'react';
import Page from '../../../pages/Page';

function PageWrapper(props: Partial<React.ComponentProps<typeof Page>> & { children?: React.ReactNode }) {
  const scrollRef = useRef<HTMLDivElement>(null);
  return <Page scrollRef={scrollRef} {...props}>{props.children ?? <div>Page content</div>}</Page>;
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
});
