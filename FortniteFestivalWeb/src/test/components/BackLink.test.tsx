import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import BackLink from '../../components/shell/mobile/BackLink';

function renderWithRouter(ui: React.ReactElement, { route = '/' } = {}) {
  return render(
    <MemoryRouter initialEntries={[route]}>{ui}</MemoryRouter>,
  );
}

describe('BackLink', () => {
  it('renders a link', () => {
    const { container } = renderWithRouter(<BackLink fallback="/songs" />);
    const link = container.querySelector('a');
    expect(link).toBeTruthy();
  });

  it('uses the fallback href', () => {
    const { container } = renderWithRouter(<BackLink fallback="/songs" />);
    const link = container.querySelector('a');
    expect(link?.getAttribute('href')).toBe('/songs');
  });
});
