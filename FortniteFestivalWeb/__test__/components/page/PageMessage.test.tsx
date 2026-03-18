import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { PageMessage } from '../../../src/pages/PageMessage';

describe('PageMessage', () => {
  it('renders children text', () => {
    render(<PageMessage>No songs found</PageMessage>);
    expect(screen.getByText('No songs found')).toBeTruthy();
  });

  it('applies default (non-error) class', () => {
    const { container } = render(<PageMessage>Info</PageMessage>);
    const div = container.firstElementChild!;
    expect(div.className).not.toContain('error');
  });

  it('applies error class when error prop is true', () => {
    const { container } = render(<PageMessage error>Error occurred</PageMessage>);
    const div = container.firstElementChild!;
    expect(div.className).toContain('error');
  });
});
