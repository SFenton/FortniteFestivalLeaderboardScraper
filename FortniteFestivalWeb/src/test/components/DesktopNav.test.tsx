import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import DesktopNav from '../../components/shell/desktop/DesktopNav';

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
});
