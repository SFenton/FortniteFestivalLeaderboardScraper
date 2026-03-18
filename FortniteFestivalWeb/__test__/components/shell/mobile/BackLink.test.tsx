import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import BackLink from '../../../../src/components/shell/mobile/BackLink';

const mockNavigate = vi.fn();
vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal() as Record<string, unknown>;
  return { ...actual, useNavigate: () => mockNavigate };
});

beforeEach(() => { vi.clearAllMocks(); });

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

  it('renders with animated wrapper by default', () => {
    const { container } = renderWithRouter(<BackLink fallback="/songs" />);
    expect(container.querySelector('[class*="Animated"]')).toBeTruthy();
  });

  it('renders without animated wrapper when animate=false', () => {
    const { container } = renderWithRouter(<BackLink fallback="/songs" animate={false} />);
    expect(container.querySelector('[class*="wrapperAnimated"]')).toBeNull();
  });

  it('calls navigate(-1) on click', () => {
    renderWithRouter(<BackLink fallback="/songs" />);
    const links = screen.getAllByRole('link');
    fireEvent.click(links[0]!);
    expect(mockNavigate).toHaveBeenCalledWith(-1);
  });
});
