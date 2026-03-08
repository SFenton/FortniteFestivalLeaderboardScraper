import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { ToggleRow } from '../components/Modal';

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
    // The icon container div should not exist — check there's no extra wrapper before the label
    const button = container.querySelector('button')!;
    // First child should be the text container (flex:1), not an icon div
    expect(button.children.length).toBe(2); // text div + track div
  });
});
