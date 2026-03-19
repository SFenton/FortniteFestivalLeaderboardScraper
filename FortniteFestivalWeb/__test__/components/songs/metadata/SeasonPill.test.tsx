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
    expect(container.querySelector('[class*="pillCurrent"]')).toBeTruthy();
  });

  it('applies default style when not current season', () => {
    const { container } = render(<TestProviders><SeasonPill season={3} /></TestProviders>);
    expect(container.querySelector('[class*="pillCurrent"]')).toBeNull();
  });
});
