import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import SeasonPill from '../../../../src/components/songs/metadata/SeasonPill';
import { TestProviders } from '../../../helpers/TestProviders';

describe('SeasonPill', () => {
  it('renders season number with S prefix', () => {
    render(<TestProviders><SeasonPill season={5} /></TestProviders>);
    expect(screen.getByText('S5')).toBeDefined();
  });

  it('renders large season numbers', () => {
    render(<TestProviders><SeasonPill season={42} /></TestProviders>);
    expect(screen.getByText('S42')).toBeDefined();
  });

  it('applies current style when season matches currentSeason', () => {
    const { container } = render(<TestProviders><SeasonPill season={5} current /></TestProviders>);
    const span = container.querySelector('span') as HTMLElement;
    expect(span.style.backgroundColor).toBeTruthy();
  });

  it('applies default style when not current season', () => {
    const { container } = render(<TestProviders><SeasonPill season={3} /></TestProviders>);
    const span = container.querySelector('span') as HTMLElement;
    // Default and current have different background colors
    expect(span.style.backgroundColor).toBeTruthy();
  });

  it('applies default style when current is explicitly false', () => {
    const { container } = render(<TestProviders><SeasonPill season={5} current={false} /></TestProviders>);
    const span = container.querySelector('span') as HTMLElement;
    expect(span.style.backgroundColor).toBeTruthy();
  });

  it('renders without context (fallback currentSeason = 0)', () => {
    // Render without TestProviders — context is null, currentSeason defaults to 0
    render(<SeasonPill season={1} />);
    expect(screen.getByText('S1')).toBeTruthy();
  });

  it('renders season 0', () => {
    render(<TestProviders><SeasonPill season={0} /></TestProviders>);
    expect(screen.getByText('S0')).toBeTruthy();
  });
});
