import { describe, it, expect } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { ModalSection } from '../../../../src/components/modals/components/ModalSection';
import { RadioRow } from '../../../../src/components/common/RadioRow';
import { BulkActions } from '../../../../src/components/modals/components/BulkActions';

describe('ModalSection', () => {
  it('renders children', () => {
    render(<ModalSection><span>content</span></ModalSection>);
    expect(screen.getByText('content')).toBeDefined();
  });

  it('renders title when provided', () => {
    render(<ModalSection title="My Section"><span>body</span></ModalSection>);
    expect(screen.getByText('My Section')).toBeDefined();
  });

  it('renders hint when provided', () => {
    render(<ModalSection hint="Some hint"><span>body</span></ModalSection>);
    expect(screen.getByText('Some hint')).toBeDefined();
  });
});

describe('RadioRow', () => {
  it('renders label', () => {
    render(<RadioRow label="Option A" selected={false} onSelect={() => {}} />);
    expect(screen.getByText('Option A')).toBeDefined();
  });

  it('calls onSelect when clicked', () => {
    let clicked = false;
    render(<RadioRow label="Option" selected={false} onSelect={() => { clicked = true; }} />);
    fireEvent.click(screen.getByText('Option'));
    expect(clicked).toBe(true);
  });
});

describe('BulkActions', () => {
  it('renders select all and clear all buttons', () => {
    render(<BulkActions onSelectAll={() => {}} onClearAll={() => {}} />);
    expect(screen.getByText('Select All')).toBeDefined();
    expect(screen.getByText('Clear All')).toBeDefined();
  });

  it('calls onSelectAll when clicked', () => {
    let selected = false;
    render(<BulkActions onSelectAll={() => { selected = true; }} onClearAll={() => {}} />);
    fireEvent.click(screen.getByText('Select All'));
    expect(selected).toBe(true);
  });

  it('calls onClearAll when clicked', () => {
    let cleared = false;
    render(<BulkActions onSelectAll={() => {}} onClearAll={() => { cleared = true; }} />);
    fireEvent.click(screen.getByText('Clear All'));
    expect(cleared).toBe(true);
  });
});
