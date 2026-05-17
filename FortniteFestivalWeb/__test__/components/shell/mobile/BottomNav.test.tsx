import { afterEach, beforeEach, describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { TabKey } from '@festival/core';
import { Layout } from '@festival/theme';

vi.mock('../../../../src/contexts/FeatureFlagsContext', () => ({
  useFeatureFlags: () => ({ rivals: true, compete: true, leaderboards: true, firstRun: true }),
}));

import BottomNav from '../../../../src/components/shell/mobile/BottomNav';

const SPACIOUS_BOTTOM_NAV_QUERY = '(min-width: 600px)';

function stubSpaciousBottomNav(matches: boolean) {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    configurable: true,
    value: vi.fn().mockImplementation((query: string) => ({
      matches: query === SPACIOUS_BOTTOM_NAV_QUERY ? matches : false,
      media: query,
      onchange: null,
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      addListener: vi.fn(),
      removeListener: vi.fn(),
      dispatchEvent: vi.fn(),
    })),
  });
}

describe('BottomNav', () => {
  const onTabClick = vi.fn();
  const originalMatchMedia = window.matchMedia;
  const bandProfile = {
    type: 'band' as const,
    bandId: 'band-1',
    bandType: 'Band_Duets' as const,
    teamKey: 'p1:p2',
    displayName: 'Player One + Player Two',
    members: [],
  };

  beforeEach(() => {
    onTabClick.mockClear();
  });

  afterEach(() => {
    Object.defineProperty(window, 'matchMedia', {
      writable: true,
      configurable: true,
      value: originalMatchMedia,
    });
  });

  it('renders tab buttons', () => {
    render(
      <MemoryRouter>
        <BottomNav player={null} activeTab={TabKey.Songs} onTabClick={onTabClick} />
      </MemoryRouter>,
    );
    expect(screen.getByText('Songs')).toBeDefined();
    expect(screen.queryByText('Suggestions')).toBeNull();
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

  it('splits leaderboards and rivals from compete when player has spacious nav width', () => {
    stubSpaciousBottomNav(true);

    render(
      <MemoryRouter>
        <BottomNav player={{ accountId: 'p1', displayName: 'P' }} activeTab={TabKey.Songs} onTabClick={onTabClick} />
      </MemoryRouter>,
    );

    expect(screen.getByTestId('bottom-nav-leaderboards')).toBeDefined();
    expect(screen.getByTestId('bottom-nav-rivals')).toBeDefined();
    expect(screen.queryByTestId('bottom-nav-compete')).toBeNull();

    fireEvent.click(screen.getByTestId('bottom-nav-leaderboards'));
    expect(onTabClick).toHaveBeenCalledWith(TabKey.Leaderboards, '/leaderboards');

    fireEvent.click(screen.getByTestId('bottom-nav-rivals'));
    expect(onTabClick).toHaveBeenCalledWith(TabKey.Rivals, '/rivals');
  });

  it('keeps the compact compete tab when player nav width is narrow', () => {
    stubSpaciousBottomNav(false);

    render(
      <MemoryRouter>
        <BottomNav player={{ accountId: 'p1', displayName: 'P' }} activeTab={TabKey.Songs} onTabClick={onTabClick} />
      </MemoryRouter>,
    );

    expect(screen.getByTestId('bottom-nav-compete')).toBeDefined();
    expect(screen.queryByTestId('bottom-nav-leaderboards')).toBeNull();
    expect(screen.queryByTestId('bottom-nav-rivals')).toBeNull();
  });

  it('shows leaderboards but not rivals without a player even when spacious', () => {
    stubSpaciousBottomNav(true);

    render(
      <MemoryRouter>
        <BottomNav player={null} activeTab={TabKey.Songs} onTabClick={onTabClick} />
      </MemoryRouter>,
    );

    expect(screen.getByTestId('bottom-nav-leaderboards')).toBeDefined();
    expect(screen.queryByTestId('bottom-nav-rivals')).toBeNull();
    expect(screen.queryByTestId('bottom-nav-compete')).toBeNull();
  });

  it('shows suggestions and statistics tabs when a band profile is selected without a player', () => {
    render(
      <MemoryRouter>
        <BottomNav player={null} selectedProfile={bandProfile} activeTab={TabKey.Songs} onTabClick={onTabClick} />
      </MemoryRouter>,
    );

    expect(screen.getByText('Suggestions')).toBeDefined();
    expect(screen.getByText('Statistics')).toBeDefined();

    fireEvent.click(screen.getByText('Suggestions'));
    expect(onTabClick).toHaveBeenCalledWith(TabKey.Suggestions, undefined);
    fireEvent.click(screen.getByText('Statistics'));
    expect(onTabClick).toHaveBeenCalledWith(TabKey.Statistics, '/statistics');
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

  it('applies active style to current tab', () => {
    render(
      <MemoryRouter>
        <BottomNav player={null} activeTab={TabKey.Settings} onTabClick={vi.fn()} />
      </MemoryRouter>,
    );
    const settingsBtn = screen.getByText('Settings').closest('button')!;
    expect(settingsBtn.style.fontWeight).toBe('700');
    expect(settingsBtn.style.color).toBe('rgb(124, 58, 237)');
  });

  it('applies active style to split rivals tab', () => {
    stubSpaciousBottomNav(true);

    render(
      <MemoryRouter>
        <BottomNav player={{ accountId: 'p1', displayName: 'P' }} activeTab={TabKey.Rivals} onTabClick={vi.fn()} />
      </MemoryRouter>,
    );

    const rivalsBtn = screen.getByTestId('bottom-nav-rivals');
    expect(rivalsBtn.style.fontWeight).toBe('700');
    expect(rivalsBtn.style.color).toBe('rgb(124, 58, 237)');
  });

  it('keeps compact compete visually active for split competitive routes', () => {
    stubSpaciousBottomNav(false);

    render(
      <MemoryRouter>
        <BottomNav player={{ accountId: 'p1', displayName: 'P' }} activeTab={TabKey.Leaderboards} onTabClick={vi.fn()} />
      </MemoryRouter>,
    );

    const competeBtn = screen.getByTestId('bottom-nav-compete');
    expect(competeBtn.style.fontWeight).toBe('700');
    expect(competeBtn.style.color).toBe('rgb(124, 58, 237)');
  });

  it('applies inactive style to non-active tab', () => {
    render(
      <MemoryRouter>
        <BottomNav player={null} activeTab={TabKey.Songs} onTabClick={vi.fn()} />
      </MemoryRouter>,
    );
    const settingsBtn = screen.getByText('Settings').closest('button')!;
    expect(settingsBtn.style.fontWeight).not.toBe('700');
    expect(settingsBtn.style.color).toBe('rgb(154, 166, 178)');
  });

  it('applies pending active style on pointer down', () => {
    render(
      <MemoryRouter>
        <BottomNav player={null} activeTab={TabKey.Songs} onTabClick={vi.fn()} />
      </MemoryRouter>,
    );

    const settingsBtn = screen.getByText('Settings').closest('button')!;
    fireEvent.pointerDown(settingsBtn);

    expect(settingsBtn.style.fontWeight).toBe('700');
    expect(settingsBtn.style.color).toBe('rgb(124, 58, 237)');
    expect(settingsBtn.dataset.pending).toBe('true');
  });

  it('commits touch navigation on pointer up without waiting for click', () => {
    render(
      <MemoryRouter>
        <BottomNav player={null} activeTab={TabKey.Songs} onTabClick={onTabClick} />
      </MemoryRouter>,
    );

    const settingsBtn = screen.getByText('Settings').closest('button')!;
    fireEvent.pointerDown(settingsBtn, { pointerId: 1, pointerType: 'touch', button: 0, clientX: 40, clientY: 820 });
    fireEvent.pointerUp(settingsBtn, { pointerId: 1, pointerType: 'touch', button: 0, clientX: 41, clientY: 821 });

    expect(onTabClick).toHaveBeenCalledTimes(1);
    expect(onTabClick).toHaveBeenCalledWith(TabKey.Settings, undefined);

    fireEvent.click(settingsBtn);
    expect(onTabClick).toHaveBeenCalledTimes(1);
  });

  it('renders without an active tab for neutral profile detail routes', () => {
    render(
      <MemoryRouter>
        <BottomNav player={{ accountId: 'p1', displayName: 'P' }} activeTab={null} onTabClick={vi.fn()} />
      </MemoryRouter>,
    );

    for (const button of screen.getAllByRole('button')) {
      expect(button.style.fontWeight).not.toBe('700');
      expect(button.style.color).toBe('rgb(154, 166, 178)');
    }
  });

  it('extends the frosted nav through the bottom safe area', () => {
    render(
      <MemoryRouter>
        <BottomNav player={null} activeTab={TabKey.Songs} onTabClick={vi.fn()} />
      </MemoryRouter>,
    );

    const nav = screen.getByRole('navigation');
    expect(nav.style.padding).toContain('var(--sab');
  });

  it('prevents native touch scrolling on the nav chrome', () => {
    render(
      <MemoryRouter>
        <BottomNav player={null} activeTab={TabKey.Songs} onTabClick={vi.fn()} />
      </MemoryRouter>,
    );

    expect(screen.getByRole('navigation').style.touchAction).toBe('none');
  });

  it('keeps bottom navigation tabs at least FAB-height for touch', () => {
    render(
      <MemoryRouter>
        <BottomNav player={null} activeTab={TabKey.Songs} onTabClick={vi.fn()} />
      </MemoryRouter>,
    );

    const settingsBtn = screen.getByText('Settings').closest('button')!;
    expect(settingsBtn.style.minHeight).toBe(`${Layout.fabSize}px`);
  });
});
