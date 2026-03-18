import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import SeasonPill from '../../components/songs/metadata/SeasonPill';

describe('SeasonPill', () => {
  it('renders season number with S prefix', () => {
    render(<SeasonPill season={5} />);
    expect(screen.getByText('S5')).toBeDefined();
  });

  it('renders large season numbers', () => {
    render(<SeasonPill season={42} />);
    expect(screen.getByText('S42')).toBeDefined();
  });
});
