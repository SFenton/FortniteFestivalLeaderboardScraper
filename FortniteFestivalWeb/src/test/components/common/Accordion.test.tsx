import { describe, it, expect } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { Accordion } from '../../../components/common/Accordion';

describe('Accordion', () => {
  it('renders title', () => {
    render(<Accordion title="Section Title"><span>content</span></Accordion>);
    expect(screen.getByText('Section Title')).toBeDefined();
  });

  it('renders hint when provided', () => {
    render(<Accordion title="Title" hint="A hint"><span>content</span></Accordion>);
    expect(screen.getByText('A hint')).toBeDefined();
  });

  it('starts closed by default', () => {
    const { container } = render(<Accordion title="Title"><span>content</span></Accordion>);
    const bodyWrap = container.querySelector('[class*="accordionBodyWrap"]');
    expect(bodyWrap?.getAttribute('style')).toContain('0fr');
  });

  it('starts open when defaultOpen is true', () => {
    const { container } = render(<Accordion title="Title" defaultOpen><span>content</span></Accordion>);
    const bodyWrap = container.querySelector('[class*="accordionBodyWrap"]');
    expect(bodyWrap?.getAttribute('style')).toContain('1fr');
  });

  it('toggles open/closed on header click', () => {
    const { container } = render(<Accordion title="Toggle Me"><span>content</span></Accordion>);
    const button = screen.getByText('Toggle Me').closest('button')!;

    // Initially closed
    let bodyWrap = container.querySelector('[class*="accordionBodyWrap"]');
    expect(bodyWrap?.getAttribute('style')).toContain('0fr');

    // Click to open
    fireEvent.click(button);
    bodyWrap = container.querySelector('[class*="accordionBodyWrap"]');
    expect(bodyWrap?.getAttribute('style')).toContain('1fr');

    // Click to close
    fireEvent.click(button);
    bodyWrap = container.querySelector('[class*="accordionBodyWrap"]');
    expect(bodyWrap?.getAttribute('style')).toContain('0fr');
  });

  it('renders icon when provided', () => {
    render(<Accordion title="Title" icon={<span data-testid="icon">Ã°Å¸Å½Â¸</span>}><span>body</span></Accordion>);
    expect(screen.getByTestId('icon')).toBeDefined();
  });
});
