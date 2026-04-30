import { describe, it, expect, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import BandFilterPill from '../../../src/components/common/BandFilterPill';

describe('BandFilterPill', () => {
  it('shows the empty filter label when no instruments are selected', () => {
    render(<BandFilterPill label="Filter Band Type" selectedInstruments={[]} onClick={vi.fn()} />);

    expect(screen.getByRole('button', { name: 'Filter Band Type' })).toBeTruthy();
    expect(screen.getByText('Filter Band Type')).toBeTruthy();
  });

  it('shows instrument icons only when a filter is applied', () => {
    const { container } = render(
      <BandFilterPill
        label="Lead / Bass"
        selectedInstruments={['Solo_Guitar', 'Solo_Bass']}
        onClick={vi.fn()}
      />,
    );

    expect(screen.getByRole('button', { name: 'Lead / Bass' })).toBeTruthy();
    expect(screen.queryByText('Lead / Bass')).toBeNull();
    expect(container.querySelectorAll('img')).toHaveLength(2);
    expect(container.querySelector('img[alt="Solo_Guitar"]')).toBeTruthy();
    expect(container.querySelector('img[alt="Solo_Bass"]')).toBeTruthy();
  });

  it('calls onClick when pressed', () => {
    const onClick = vi.fn();
    render(<BandFilterPill label="Filter Band Type" selectedInstruments={[]} onClick={onClick} />);

    fireEvent.click(screen.getByRole('button', { name: 'Filter Band Type' }));

    expect(onClick).toHaveBeenCalledOnce();
  });
});
