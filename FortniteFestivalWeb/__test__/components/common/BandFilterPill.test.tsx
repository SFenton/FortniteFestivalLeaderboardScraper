import { describe, it, expect, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { Gap, InstrumentSize, Layout } from '@festival/theme';
import BandFilterPill from '../../../src/components/common/BandFilterPill';

describe('BandFilterPill', () => {
  it('shows the empty filter label when no instruments are selected', () => {
    render(<BandFilterPill label="Filter Band Type" selectedInstruments={[]} onClick={vi.fn()} />);

    expect(screen.getByRole('button', { name: 'Filter Band Type' })).toBeTruthy();
    expect(screen.getByText('Filter Band Type')).toBeTruthy();
  });

  it('shows the filter icon and larger instrument icons when a filter is applied', () => {
    const { container } = render(
      <BandFilterPill
        label="Lead / Bass / Lead"
        selectedInstruments={['Solo_Guitar', 'Solo_Bass', 'Solo_Guitar']}
        onClick={vi.fn()}
      />,
    );

    const button = screen.getByRole('button', { name: 'Lead / Bass / Lead' });
    expect(button).toBeTruthy();
    expect(button.style.height).toBe(`${Layout.pillButtonHeight}px`);
    expect(button.style.gap).toBe(`${Gap.lg}px`);
    expect(screen.queryByText('Lead / Bass / Lead')).toBeNull();
    expect(screen.getByTestId('band-filter-pill-filter-icon')).toBeTruthy();

    const icons = container.querySelectorAll('img');
    expect(icons).toHaveLength(3);
    icons.forEach((icon) => {
      expect(icon.getAttribute('width')).toBe(String(InstrumentSize.sm));
      expect(icon.getAttribute('height')).toBe(String(InstrumentSize.sm));
    });
    expect(container.querySelectorAll('img[alt="Solo_Guitar"]')).toHaveLength(2);
    expect(container.querySelector('img[alt="Solo_Bass"]')).toBeTruthy();
  });

  it('calls onClick when pressed', () => {
    const onClick = vi.fn();
    render(<BandFilterPill label="Filter Band Type" selectedInstruments={[]} onClick={onClick} />);

    fireEvent.click(screen.getByRole('button', { name: 'Filter Band Type' }));

    expect(onClick).toHaveBeenCalledOnce();
  });
});
