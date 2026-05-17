import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import type { ReactElement } from 'react';
import { MemoryRouter } from 'react-router-dom';
import { Font, InstrumentSize, Weight } from '@festival/theme';
import PlayerSectionHeading from '../../../../src/pages/player/sections/PlayerSectionHeading';

vi.mock('../../../../src/components/display/InstrumentIcons', () => ({
  InstrumentIcon: ({ instrument }: any) => <span data-testid={`icon-${instrument}`}>{instrument}</span>,
  getInstrumentStatusVisual: () => ({ fill: '#000', stroke: '#000' }),
}));

function renderHeading(node: ReactElement) {
  return render(<MemoryRouter>{node}</MemoryRouter>);
}

describe('PlayerSectionHeading', () => {
  it('renders with title only', () => {
    renderHeading(<PlayerSectionHeading title="Test Section" />);
    expect(screen.getByText('Test Section')).toBeTruthy();
  });

  it('renders with description', () => {
    renderHeading(<PlayerSectionHeading title="Section" description="Some description" />);
    expect(screen.getByText('Some description')).toBeTruthy();
  });

  it('renders with instrument icon', () => {
    renderHeading(<PlayerSectionHeading title="Section" instrument="Solo_Guitar" />);
    expect(screen.getByTestId('icon-Solo_Guitar')).toBeTruthy();
  });

  it('stacks multi-instrument icons above the title', () => {
    renderHeading(<PlayerSectionHeading title="Pad Global Statistics" instruments={['Solo_Guitar', 'Solo_Bass']} />);

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
    const { container } = renderHeading(<PlayerSectionHeading title="Section" compact />);
    expect(container.textContent).toContain('Section');
  });

  it('renders text-only see all action styling', () => {
    renderHeading(<PlayerSectionHeading title="Bands" actionLabel="See all" actionTo="/bands/player/p1" actionTestId="bands-action" />);

    const action = screen.getByTestId('bands-action');
    const title = screen.getByRole('heading', { name: 'Bands' });
    expect(title.parentElement).toHaveStyle({ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' });
    expect(action).toHaveTextContent('See all');
    expect(action).toHaveAttribute('href', '/bands/player/p1');
    expect(action.parentElement).toHaveStyle({ alignItems: 'center' });
    expect(action.style.backgroundColor).toBe('');
    expect(action.style.fontSize).toBe(`${Font.lg}px`);
    expect(action.style.fontWeight).toBe(`${Weight.bold}`);
    expect(action.style.minHeight).toBe(`${InstrumentSize.sm}px`);
    expect(action.style.padding).toBe('0px');
    expect(action.style.textDecoration).toBe('none');
    expect(action.style.whiteSpace).toBe('nowrap');
    expect(action.querySelector('svg')).toHaveAttribute('width', '20');
  });
});
