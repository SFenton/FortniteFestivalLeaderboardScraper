import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import PlayerSectionHeading from '../../../../src/pages/player/sections/PlayerSectionHeading';

vi.mock('../../../../src/components/display/InstrumentIcons', () => ({
  InstrumentIcon: ({ instrument }: any) => <span data-testid={`icon-${instrument}`}>{instrument}</span>,
  getInstrumentStatusVisual: () => ({ fill: '#000', stroke: '#000' }),
}));

describe('PlayerSectionHeading', () => {
  it('renders with title only', () => {
    render(<PlayerSectionHeading title="Test Section" />);
    expect(screen.getByText('Test Section')).toBeTruthy();
  });

  it('renders with description', () => {
    render(<PlayerSectionHeading title="Section" description="Some description" />);
    expect(screen.getByText('Some description')).toBeTruthy();
  });

  it('renders with instrument icon', () => {
    render(<PlayerSectionHeading title="Section" instrument="Solo_Guitar" />);
    expect(screen.getByTestId('icon-Solo_Guitar')).toBeTruthy();
  });

  it('stacks multi-instrument icons above the title', () => {
    render(<PlayerSectionHeading title="Pad Global Statistics" instruments={['Solo_Guitar', 'Solo_Bass']} />);

    const title = screen.getByRole('heading', { name: 'Pad Global Statistics' });
    const iconCluster = screen.getByTestId('icon-Solo_Guitar').parentElement;
    const titleRow = title.parentElement;

    expect(iconCluster).toBeTruthy();
    expect(titleRow).toHaveStyle({ flexDirection: 'column', alignItems: 'flex-start' });
    expect(titleRow?.children[0]).toBe(iconCluster);
    expect(titleRow?.children[1]).toBe(title);
    expect(Array.from(iconCluster!.children).map(child => child.textContent)).toEqual(['Solo_Guitar', 'Solo_Bass']);
  });

  it('renders compact mode', () => {
    const { container } = render(<PlayerSectionHeading title="Section" compact />);
    expect(container.textContent).toContain('Section');
  });
});
