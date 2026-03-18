import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import DesktopNav from '../../../../src/components/shell/desktop/DesktopNav';

const mockNavigate = vi.fn();
vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal() as Record<string, unknown>;
  return { ...actual, useNavigate: () => mockNavigate };
});

beforeEach(() => { vi.clearAllMocks(); });

function renderWithRouter(ui: React.ReactElement) {
  return render(<MemoryRouter>{ui}</MemoryRouter>);
}

describe('DesktopNav', () => {
  it('renders a nav element', () => {
    renderWithRouter(<DesktopNav hasPlayer={false} onOpenSidebar={() => {}} onProfileClick={() => {}} />);
    expect(screen.getByRole('navigation')).toBeDefined();
  });

  it('renders hamburger button', () => {
    const { container } = renderWithRouter(<DesktopNav hasPlayer={false} onOpenSidebar={() => {}} onProfileClick={() => {}} />);
    expect(container.querySelector('[class*="hamburger"]')).toBeTruthy();
  });

  it('renders profile button', () => {
    renderWithRouter(<DesktopNav hasPlayer={true} onOpenSidebar={() => {}} onProfileClick={() => {}} />);
    const buttons = screen.getAllByRole('button');
    expect(buttons.length).toBeGreaterThanOrEqual(2); // hamburger + profile
  });

  it('calls onOpenSidebar when hamburger clicked', () => {
    const onOpen = vi.fn();
    renderWithRouter(<DesktopNav hasPlayer={false} onOpenSidebar={onOpen} onProfileClick={vi.fn()} />);
    const buttons = screen.getAllByRole('button');
    fireEvent.click(buttons[0]!);
    expect(onOpen).toHaveBeenCalled();
  });

  it('calls onProfileClick when profile button clicked', () => {
    const onProfile = vi.fn();
    renderWithRouter(<DesktopNav hasPlayer={false} onOpenSidebar={vi.fn()} onProfileClick={onProfile} />);
    const buttons = screen.getAllByRole('button');
    fireEvent.click(buttons[buttons.length - 1]!);
    expect(onProfile).toHaveBeenCalled();
  });
});
