import { describe, it, expect } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { Accordion } from '../../../src/components/common/Accordion';

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
    // The body wrap is the div with display:grid after the button
    const button = screen.getByText('Title').closest('button')!;
    const bodyWrap = button.nextElementSibling as HTMLElement;
    expect(bodyWrap?.style.gridTemplateRows).toBe('0fr');
  });

  it('starts open when defaultOpen is true', () => {
    const { container } = render(<Accordion title="Title" defaultOpen><span>content</span></Accordion>);
    const button = screen.getByText('Title').closest('button')!;
    const bodyWrap = button.nextElementSibling as HTMLElement;
    expect(bodyWrap?.style.gridTemplateRows).toBe('1fr');
  });

  it('toggles open/closed on header click', () => {
    const { container } = render(<Accordion title="Toggle Me"><span>content</span></Accordion>);
    const button = screen.getByText('Toggle Me').closest('button')!;
    const getBodyWrap = () => button.nextElementSibling as HTMLElement;

    // Initially closed
    expect(getBodyWrap()?.style.gridTemplateRows).toBe('0fr');

    // Click to open
    fireEvent.click(button);
    expect(getBodyWrap()?.style.gridTemplateRows).toBe('1fr');

    // Click to close
    fireEvent.click(button);
    expect(getBodyWrap()?.style.gridTemplateRows).toBe('0fr');
  });

  it('renders icon when provided', () => {
    render(<Accordion title="Title" icon={<span data-testid="icon">🎸</span>}><span>body</span></Accordion>);
    expect(screen.getByTestId('icon')).toBeDefined();
  });
});
