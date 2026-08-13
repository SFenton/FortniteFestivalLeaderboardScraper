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
    render(<Accordion title="Title"><button type="button">content</button></Accordion>);
    const trigger = screen.getByRole('button', { name: 'Title' });
    const panel = document.getElementById(trigger.getAttribute('aria-controls')!)!;
    expect(trigger).toHaveAttribute('aria-expanded', 'false');
    expect(panel.style.gridTemplateRows).toBe('0fr');
    expect(panel).toHaveAttribute('inert');
    expect(panel).toHaveAttribute('aria-hidden', 'true');
  });

  it('starts open when defaultOpen is true', () => {
    render(<Accordion title="Title" defaultOpen><span>content</span></Accordion>);
    const trigger = screen.getByRole('button', { name: 'Title' });
    const panel = document.getElementById(trigger.getAttribute('aria-controls')!)!;
    expect(trigger).toHaveAttribute('aria-expanded', 'true');
    expect(panel.style.gridTemplateRows).toBe('1fr');
    expect(panel).not.toHaveAttribute('inert');
    expect(panel).not.toHaveAttribute('aria-hidden');
  });

  it('toggles open/closed on header click', () => {
    render(<Accordion title="Toggle Me"><span>content</span></Accordion>);
    const trigger = screen.getByRole('button', { name: 'Toggle Me' });
    const panel = document.getElementById(trigger.getAttribute('aria-controls')!)!;

    expect(panel.style.gridTemplateRows).toBe('0fr');

    fireEvent.click(trigger);
    expect(trigger).toHaveAttribute('aria-expanded', 'true');
    expect(panel.style.gridTemplateRows).toBe('1fr');

    fireEvent.click(trigger);
    expect(trigger).toHaveAttribute('aria-expanded', 'false');
    expect(panel.style.gridTemplateRows).toBe('0fr');
  });

  it('renders icon when provided', () => {
    render(<Accordion title="Title" icon={<span data-testid="icon">🎸</span>}><span>body</span></Accordion>);
    expect(screen.getByTestId('icon')).toBeDefined();
  });

  it('connects unique trigger and panel IDs', () => {
    render(
      <>
        <Accordion title="First"><span>first</span></Accordion>
        <Accordion title="Second"><span>second</span></Accordion>
      </>,
    );
    const first = screen.getByRole('button', { name: 'First' });
    const second = screen.getByRole('button', { name: 'Second' });
    expect(first.id).not.toBe(second.id);
    expect(first.getAttribute('aria-controls')).not.toBe(second.getAttribute('aria-controls'));
    expect(document.getElementById(first.getAttribute('aria-controls')!)).toBeTruthy();
    expect(document.getElementById(second.getAttribute('aria-controls')!)).toBeTruthy();
  });

  it('adds optional region semantics', () => {
    render(<Accordion title="Landmark" panelLandmark defaultOpen><span>content</span></Accordion>);
    const trigger = screen.getByRole('button', { name: 'Landmark' });
    const region = screen.getByRole('region', { name: 'Landmark' });
    expect(region.id).toBe(trigger.getAttribute('aria-controls'));
    expect(region).toHaveAttribute('aria-labelledby', trigger.id);
  });

  it('returns child focus to the trigger before closing', () => {
    render(
      <Accordion title="Focusable" defaultOpen>
        <button type="button">Child action</button>
      </Accordion>,
    );
    const trigger = screen.getByRole('button', { name: 'Focusable' });
    const child = screen.getByRole('button', { name: 'Child action' });
    child.focus();

    fireEvent.click(trigger);

    expect(trigger).toHaveFocus();
    expect(trigger).toHaveAttribute('aria-expanded', 'false');
  });
});
