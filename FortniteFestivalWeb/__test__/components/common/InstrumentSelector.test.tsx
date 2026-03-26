import { describe, it, expect, vi } from 'vitest';
import { render, fireEvent } from '@testing-library/react';
import React from 'react';
import { InstrumentSelector } from '../../../src/components/common/InstrumentSelector';
import type { InstrumentSelectorItem } from '../../../src/components/common/InstrumentSelector';
import type { ServerInstrumentKey } from '@festival/core/api/serverTypes';


const instruments: InstrumentSelectorItem[] = [
  { key: 'Solo_Guitar' as ServerInstrumentKey },
  { key: 'Solo_Bass' as ServerInstrumentKey },
  { key: 'Solo_Drums' as ServerInstrumentKey },
];

describe('InstrumentSelector', () => {
  it('renders a button for each instrument', () => {
    const { container } = render(
      React.createElement(InstrumentSelector, {
        instruments,
        selected: null,
        onSelect: () => {},
      }),
    );
    const buttons = container.querySelectorAll('button');
    expect(buttons).toHaveLength(3);
  });

  it('calls onSelect with the instrument key when clicked', () => {
    const onSelect = vi.fn();
    const { container } = render(
      React.createElement(InstrumentSelector, {
        instruments,
        selected: null,
        onSelect,
      }),
    );
    const buttons = container.querySelectorAll('button');
    fireEvent.click(buttons[0]!);
    expect(onSelect).toHaveBeenCalledWith('Solo_Guitar');
  });

  it('calls onSelect with null when clicking the already-selected instrument', () => {
    const onSelect = vi.fn();
    const { container } = render(
      React.createElement(InstrumentSelector, {
        instruments,
        selected: 'Solo_Guitar',
        onSelect,
      }),
    );
    const buttons = container.querySelectorAll('button');
    fireEvent.click(buttons[0]!);
    expect(onSelect).toHaveBeenCalledWith(null);
  });

  it('does not render children wrapper when no children provided', () => {
    const { container } = render(
      React.createElement(InstrumentSelector, {
        instruments,
        selected: null,
        onSelect: () => {},
      }),
    );
    // No collapsible grid-template-rows wrapper rendered
    expect(container.querySelector('[style*="grid-template-rows"]')).toBeNull();
  });

  it('renders children in a collapsible wrapper', () => {
    const { container } = render(
      React.createElement(InstrumentSelector, {
        instruments,
        selected: 'Solo_Guitar',
        onSelect: () => {},
        children: React.createElement('div', { 'data-testid': 'child' }, 'Content'),
      }),
    );
    expect(container.textContent).toContain('Content');
  });

  it('collapses children when nothing is selected', () => {
    const { container } = render(
      React.createElement(InstrumentSelector, {
        instruments,
        selected: null,
        onSelect: () => {},
        children: React.createElement('div', null, 'Hidden'),
      }),
    );
    // The wrapper should have gridTemplateRows: 0fr
    const wrapper = container.querySelector('[style*="grid"]');
    expect(wrapper?.getAttribute('style')).toContain('0fr');
  });

  it('expands children when an instrument is selected', () => {
    const { container } = render(
      React.createElement(InstrumentSelector, {
        instruments,
        selected: 'Solo_Bass',
        onSelect: () => {},
        children: React.createElement('div', null, 'Visible'),
      }),
    );
    const wrapper = container.querySelector('[style*="grid"]');
    expect(wrapper?.getAttribute('style')).toContain('1fr');
  });

  it('uses custom label when provided', () => {
    const customInstruments: InstrumentSelectorItem[] = [
      { key: 'Solo_Guitar' as ServerInstrumentKey, label: 'Custom Lead' },
    ];
    const { container } = render(
      React.createElement(InstrumentSelector, {
        instruments: customInstruments,
        selected: null,
        onSelect: () => {},
      }),
    );
    const button = container.querySelector('button');
    expect(button?.getAttribute('title')).toBe('Custom Lead');
  });

  it('does not deselect when required is true', () => {
    const onSelect = vi.fn();
    const { container } = render(
      React.createElement(InstrumentSelector, {
        instruments,
        selected: 'Solo_Guitar',
        onSelect,
        required: true,
      }),
    );
    const buttons = container.querySelectorAll('button');
    fireEvent.click(buttons[0]!);
    expect(onSelect).toHaveBeenCalledWith('Solo_Guitar');
  });

  it('renders compact mode with arrow buttons', () => {
    const onSelect = vi.fn();
    const { container } = render(
      React.createElement(InstrumentSelector, {
        instruments,
        selected: 'Solo_Bass',
        onSelect,
        compact: true,
        compactLabels: { previous: 'Prev', next: 'Next' },
      }),
    );
    const buttons = container.querySelectorAll('button');
    // arrow-left, active icon, arrow-right
    expect(buttons).toHaveLength(3);
    expect(buttons[0]!.getAttribute('aria-label')).toBe('Prev');
    expect(buttons[2]!.getAttribute('aria-label')).toBe('Next');
  });

  it('cycles to next instrument in compact mode', () => {
    const onSelect = vi.fn();
    const { container } = render(
      React.createElement(InstrumentSelector, {
        instruments,
        selected: 'Solo_Guitar',
        onSelect,
        compact: true,
      }),
    );
    const buttons = container.querySelectorAll('button');
    // Click next arrow
    fireEvent.click(buttons[2]!);
    expect(onSelect).toHaveBeenCalledWith('Solo_Bass');
  });

  it('cycles to previous instrument in compact mode', () => {
    const onSelect = vi.fn();
    const { container } = render(
      React.createElement(InstrumentSelector, {
        instruments,
        selected: 'Solo_Bass',
        onSelect,
        compact: true,
      }),
    );
    const buttons = container.querySelectorAll('button');
    // Click previous arrow
    fireEvent.click(buttons[0]!);
    expect(onSelect).toHaveBeenCalledWith('Solo_Guitar');
  });

  it('wraps around in compact mode', () => {
    const onSelect = vi.fn();
    const { container } = render(
      React.createElement(InstrumentSelector, {
        instruments,
        selected: 'Solo_Guitar',
        onSelect,
        compact: true,
      }),
    );
    const buttons = container.querySelectorAll('button');
    // Click previous arrow from first → wraps to last
    fireEvent.click(buttons[0]!);
    expect(onSelect).toHaveBeenCalledWith('Solo_Drums');
  });
});
