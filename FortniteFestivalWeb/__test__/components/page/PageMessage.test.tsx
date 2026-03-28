import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { PageMessage } from '../../../src/pages/PageMessage';

describe('PageMessage', () => {
  it('renders children text', () => {
    render(<PageMessage>No songs found</PageMessage>);
    expect(screen.getByText('No songs found')).toBeTruthy();
  });

  it('applies default style', () => {
    const { container } = render(<PageMessage>Info</PageMessage>);
    const div = container.firstElementChild! as HTMLElement;
    expect(div.style.color).toBeTruthy();
  });
});
