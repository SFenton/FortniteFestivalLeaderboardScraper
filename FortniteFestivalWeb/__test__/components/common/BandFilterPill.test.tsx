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
        bandType="Band_Duets"
        onClick={vi.fn()}
      />,
    );

    const button = screen.getByRole('button', { name: 'Lead / Bass / Lead' });
    expect(button).toBeTruthy();
    expect(button.style.height).toBe(`${Layout.pillButtonHeight}px`);
    expect(button.style.gap).toBe(`${Gap.lg}px`);
    expect(button.style.background).toBe('rgb(45, 130, 230)');
    expect(button.style.border).toBe('1px solid transparent');
    expect(button.style.boxShadow).toBe('none');
    expect(button.style.color).toBe('rgb(255, 255, 255)');
    expect(screen.queryByText('Lead / Bass / Lead')).toBeNull();
    expect(screen.getByText('Duos')).toBeTruthy();
    expect(screen.getByTestId('band-filter-pill-filter-icon')).toBeTruthy();

    const visibleTextNodes = Array.from(button.childNodes)
      .map(node => node.textContent?.trim())
      .filter(Boolean);
    expect(visibleTextNodes).toEqual(['Duos']);

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
