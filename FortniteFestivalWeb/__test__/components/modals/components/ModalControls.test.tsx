import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { ModalSection } from '../../../../src/components/modals/components/ModalSection';
import { RadioRow } from '../../../../src/components/common/RadioRow';
import { BulkActions } from '../../../../src/components/modals/components/BulkActions';

function dispatchPointer(target: Element, type: string, props: Partial<PointerEvent> = {}) {
  const event = new Event(type, { bubbles: true, cancelable: true }) as PointerEvent;
  Object.defineProperties(event, {
    pointerId: { value: props.pointerId ?? 1 },
    pointerType: { value: props.pointerType ?? 'touch' },
    isPrimary: { value: props.isPrimary ?? true },
    button: { value: props.button ?? 0 },
    clientX: { value: props.clientX ?? 0 },
    clientY: { value: props.clientY ?? 0 },
    timeStamp: { value: props.timeStamp ?? 0 },
  });
  fireEvent(target, event);
  return event;
}

function dispatchClick(target: Element, timeStamp = 0) {
  const event = new MouseEvent('click', { bubbles: true, cancelable: true });
  Object.defineProperty(event, 'timeStamp', { value: timeStamp });
  fireEvent(target, event);
  return event;
}

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

  it('uses pointerup for info without selecting the row', () => {
    const onInfo = vi.fn();
    const onSelect = vi.fn();
    const { container } = render(<RadioRow label="Option" selected={false} onSelect={onSelect} onInfo={onInfo} />);
    const info = container.querySelector('span[role="button"]')!;

    dispatchPointer(info, 'pointerdown', { clientX: 8, clientY: 8, timeStamp: 10 });
    dispatchPointer(info, 'pointerup', { clientX: 8, clientY: 8, timeStamp: 20 });
    dispatchClick(info, 80);

    expect(onInfo).toHaveBeenCalledTimes(1);
    expect(onSelect).not.toHaveBeenCalled();
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
