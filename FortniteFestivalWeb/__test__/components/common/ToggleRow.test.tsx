import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { ToggleRow } from '../../../src/components/common/ToggleRow';

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

describe('ToggleRow', () => {
  it('renders label and toggle', () => {
    render(<ToggleRow label="Test Label" checked={false} onToggle={() => {}} />);
    expect(screen.getByText('Test Label')).toBeDefined();
  });

  it('renders description when provided', () => {
    render(<ToggleRow label="Label" description="A description" checked={false} onToggle={() => {}} />);
    expect(screen.getByText('A description')).toBeDefined();
  });

  it('calls onToggle when clicked', () => {
    const onToggle = vi.fn();
    render(<ToggleRow label="Label" checked={false} onToggle={onToggle} />);
    fireEvent.click(screen.getByText('Label').closest('button')!);
    expect(onToggle).toHaveBeenCalledOnce();
  });

  it('does not call onToggle when disabled', () => {
    const onToggle = vi.fn();
    render(<ToggleRow label="Label" checked={true} onToggle={onToggle} disabled />);
    const btn = screen.getByText('Label').closest('button')!;
    fireEvent.click(btn);
    expect(onToggle).not.toHaveBeenCalled();
  });

  it('renders disabled button when disabled prop is true', () => {
    render(<ToggleRow label="Label" checked={true} onToggle={() => {}} disabled />);
    const btn = screen.getByText('Label').closest('button')!;
    expect(btn).toHaveProperty('disabled', true);
  });

  it('renders icon when provided', () => {
    render(
      <ToggleRow
        label="With Icon"
        checked={false}
        onToggle={() => {}}
        icon={<span data-testid="test-icon">🎸</span>}
      />,
    );
    expect(screen.getByTestId('test-icon')).toBeDefined();
  });

  it('does not render icon container when no icon provided', () => {
    const { container } = render(<ToggleRow label="No Icon" checked={false} onToggle={() => {}} />);
    // The icon container div should not exist -- check there's no extra wrapper before the label
    const button = container.querySelector('button')!;
    // First child should be the text container (flex:1), not an icon div
    expect(button.children.length).toBe(2); // text div + track div
  });

  it('uses pointerup for info without toggling the row', () => {
    const onInfo = vi.fn();
    const onToggle = vi.fn();
    const { container } = render(<ToggleRow label="Label" checked={false} onToggle={onToggle} onInfo={onInfo} />);
    const info = container.querySelector('span[role="button"]')!;

    dispatchPointer(info, 'pointerdown', { clientX: 8, clientY: 8, timeStamp: 10 });
    dispatchPointer(info, 'pointerup', { clientX: 8, clientY: 8, timeStamp: 20 });
    dispatchClick(info, 80);

    expect(onInfo).toHaveBeenCalledTimes(1);
    expect(onToggle).not.toHaveBeenCalled();
  });
});
