import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import Page from '../../../src/pages/Page';

function PageWrapper(props: Partial<React.ComponentProps<typeof Page>> & { children?: React.ReactNode }) {
  return <MemoryRouter><Page {...props}>{props.children ?? <div>Page content</div>}</Page></MemoryRouter>;
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
