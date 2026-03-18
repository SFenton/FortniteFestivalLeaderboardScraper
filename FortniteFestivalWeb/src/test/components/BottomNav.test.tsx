import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { TabKey } from '@festival/core';
import BottomNav from '../../components/shell/mobile/BottomNav';

describe('BottomNav', () => {
  const onTabClick = vi.fn();

  it('renders tab buttons', () => {
    render(
      <MemoryRouter>
        <BottomNav player={null} activeTab={TabKey.Songs} onTabClick={onTabClick} />
      </MemoryRouter>,
    );
    expect(screen.getByText('Songs')).toBeDefined();
    expect(screen.getByText('Settings')).toBeDefined();
  });

  it('shows suggestions and statistics tabs when player exists', () => {
    render(
      <MemoryRouter>
        <BottomNav player={{ accountId: 'p1', displayName: 'P' }} activeTab={TabKey.Songs} onTabClick={onTabClick} />
      </MemoryRouter>,
    );
    expect(screen.getByText('Suggestions')).toBeDefined();
    expect(screen.getByText('Statistics')).toBeDefined();
  });

  it('calls onTabClick when a tab is pressed', () => {
    render(
      <MemoryRouter>
        <BottomNav player={null} activeTab={TabKey.Songs} onTabClick={onTabClick} />
      </MemoryRouter>,
    );
    fireEvent.click(screen.getByText('Settings'));
    expect(onTabClick).toHaveBeenCalled();
  });

  it('applies active class to current tab', () => {
    render(
      <MemoryRouter>
        <BottomNav player={null} activeTab={TabKey.Settings} onTabClick={vi.fn()} />
      </MemoryRouter>,
    );
    const settingsBtn = screen.getByText('Settings').closest('button')!;
    expect(settingsBtn.className).toContain('tabActive');
  });

  it('applies inactive class to non-active tab', () => {
    render(
      <MemoryRouter>
        <BottomNav player={null} activeTab={TabKey.Songs} onTabClick={vi.fn()} />
      </MemoryRouter>,
    );
    const settingsBtn = screen.getByText('Settings').closest('button')!;
    expect(settingsBtn.className).not.toContain('tabActive');
  });
});
