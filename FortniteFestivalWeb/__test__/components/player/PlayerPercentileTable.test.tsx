import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { PlayerPercentileHeader, PlayerPercentileRow } from '../../../src/components/player/PlayerPercentileTable';

function getPercentileRow() {
  return screen.getByText('Top 1%').closest('[role="button"]') as HTMLElement;
}

describe('PlayerPercentileTable', () => {
  it('renders the header labels', () => {
    render(<PlayerPercentileHeader percentileLabel="Percentile" songsLabel="Songs" />);

    expect(screen.getByText('Percentile')).toBeDefined();
    expect(screen.getByText('Songs')).toBeDefined();
  });

  it('uses click fallback for percentile rows', () => {
    const onClick = vi.fn();
    render(<PlayerPercentileRow pct={1} count={12} isLast={false} onClick={onClick} />);

    fireEvent.click(getPercentileRow());

    expect(onClick).toHaveBeenCalledTimes(1);
  });

  it('activates percentile rows from touch pointerup and suppresses the follow-up click', () => {
    const onClick = vi.fn();
    render(<PlayerPercentileRow pct={1} count={12} isLast={false} onClick={onClick} />);
    const row = getPercentileRow();

    fireEvent.pointerDown(row, { pointerId: 1, pointerType: 'touch', isPrimary: true, button: 0, clientX: 20, clientY: 20 });
    expect(row).toHaveAttribute('data-pressed', 'true');
    fireEvent.pointerUp(row, { pointerId: 1, pointerType: 'touch', isPrimary: true, button: 0, clientX: 20, clientY: 20 });
    expect(onClick).toHaveBeenCalledTimes(1);
    expect(row).not.toHaveAttribute('data-pressed');

    fireEvent.click(row);
    expect(onClick).toHaveBeenCalledTimes(1);
  });

  it('cancels percentile row activation when a touch gesture turns into scrolling', () => {
    const onClick = vi.fn();
    render(<PlayerPercentileRow pct={1} count={12} isLast={false} onClick={onClick} />);
    const row = getPercentileRow();

    fireEvent.pointerDown(row, { pointerId: 1, pointerType: 'touch', isPrimary: true, button: 0, clientX: 20, clientY: 20 });
    expect(row).toHaveAttribute('data-pressed', 'true');
    fireEvent.pointerMove(row, { pointerId: 1, pointerType: 'touch', isPrimary: true, button: 0, clientX: 20, clientY: 28 });
    expect(row).not.toHaveAttribute('data-pressed');
    fireEvent.pointerUp(row, { pointerId: 1, pointerType: 'touch', isPrimary: true, button: 0, clientX: 20, clientY: 28 });

    expect(onClick).not.toHaveBeenCalled();
  });

  it('supports keyboard activation for percentile rows', () => {
    const onClick = vi.fn();
    render(<PlayerPercentileRow pct={1} count={12} isLast={false} onClick={onClick} />);
    const row = getPercentileRow();

    fireEvent.keyDown(row, { key: 'Enter' });
    fireEvent.keyDown(row, { key: ' ' });

    expect(onClick).toHaveBeenCalledTimes(2);
  });

  it('removes the bottom border from the final percentile row', () => {
    render(<PlayerPercentileRow pct={1} count={12} isLast onClick={vi.fn()} />);

    expect(getPercentileRow().style.borderBottomStyle).toBe('none');
  });
});
