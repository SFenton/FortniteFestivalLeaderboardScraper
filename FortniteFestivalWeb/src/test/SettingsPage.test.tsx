import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { SettingsProvider } from '../contexts/SettingsContext';
import SettingsPage from '../pages/settings/SettingsPage';

beforeEach(() => {
  localStorage.clear();
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation((query: string) => ({
      matches: false,
      media: query,
      onchange: null,
      addListener: vi.fn(),
      removeListener: vi.fn(),
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      dispatchEvent: vi.fn(),
    })),
  });
  // Mock fetch for /api/version
  global.fetch = vi.fn().mockImplementation((url: string) => {
    if (typeof url === 'string' && url.includes('/api/version')) {
      return Promise.resolve({ ok: true, json: () => Promise.resolve({ version: '1.0.0' }) });
    }
    return Promise.resolve({ ok: false, statusText: 'Not Found', json: () => Promise.resolve({}) });
  }) as unknown as typeof fetch;
});

function renderSettings() {
  return render(
    <SettingsProvider>
      <SettingsPage />
    </SettingsProvider>,
  );
}

describe('SettingsPage', () => {
  it('renders the Settings heading on mobile', () => {
    // Mock mobile viewport
    (window.matchMedia as ReturnType<typeof vi.fn>).mockImplementation((query: string) => ({
      matches: true,
      media: query,
      onchange: null,
      addListener: vi.fn(),
      removeListener: vi.fn(),
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      dispatchEvent: vi.fn(),
    }));
    renderSettings();
    expect(screen.getByText('App Settings')).toBeDefined();
  });

  it('renders App Settings section', () => {
    renderSettings();
    expect(screen.getByText('App Settings')).toBeDefined();
  });

  it('renders Show Instruments section with all instruments', () => {
    renderSettings();
    expect(screen.getByText('Show Instruments')).toBeDefined();
    expect(screen.getByText('Lead')).toBeDefined();
    expect(screen.getByText('Bass')).toBeDefined();
    expect(screen.getByText('Drums')).toBeDefined();
    expect(screen.getByText('Vocals')).toBeDefined();
    expect(screen.getByText('Pro Lead')).toBeDefined();
    expect(screen.getByText('Pro Bass')).toBeDefined();
  });

  it('renders Show Instrument Metadata section', () => {
    renderSettings();
    expect(screen.getByText('Show Instrument Metadata')).toBeDefined();
    expect(screen.getByText('Score')).toBeDefined();
    expect(screen.getByText('Percentage')).toBeDefined();
    expect(screen.getByText('Percentile')).toBeDefined();
    expect(screen.getByText('Season Achieved')).toBeDefined();
    expect(screen.getByText('Song Intensity')).toBeDefined();
    expect(screen.getByText('Stars')).toBeDefined();
  });

  it('renders Reset Settings section', () => {
    renderSettings();
    expect(screen.getByText('Reset Settings')).toBeDefined();
    expect(screen.getByText('Reset All Settings')).toBeDefined();
  });

  it('toggles Show Instrument Icons', () => {
    renderSettings();
    const toggle = screen.getByText('Show Instrument Icons').closest('button')!;
    // Default: checked (icons shown, songsHideInstrumentIcons=false)
    fireEvent.click(toggle);

    // After click: icons hidden — the toggle still exists, verify the stored state
    const stored = JSON.parse(localStorage.getItem('fst:appSettings')!);
    expect(stored.songsHideInstrumentIcons).toBe(true);
  });

  it('toggles a show instrument setting', () => {
    renderSettings();
    const leadToggle = screen.getByText('Lead').closest('button')!;
    fireEvent.click(leadToggle);

    const stored = JSON.parse(localStorage.getItem('fst:appSettings')!);
    expect(stored.showLead).toBe(false);
  });

  it('prevents disabling the last visible instrument', () => {
    // Pre-set only one instrument visible
    localStorage.setItem('fst:appSettings', JSON.stringify({
      showLead: true,
      showBass: false,
      showDrums: false,
      showVocals: false,
      showProLead: false,
      showProBass: false,
    }));

    renderSettings();
    const leadToggle = screen.getByText('Lead').closest('button')!;
    expect(leadToggle).toHaveProperty('disabled', true);
  });

  it('toggles metadata visibility', () => {
    renderSettings();
    const scoreToggle = screen.getByText('Score').closest('button')!;
    fireEvent.click(scoreToggle);

    const stored = JSON.parse(localStorage.getItem('fst:appSettings')!);
    expect(stored.metadataShowScore).toBe(false);
  });

  it('shows visual order reorder list when enabled', () => {
    renderSettings();

    // Visual order section should not be visible initially
    expect(screen.queryByText('Song Row Visual Order')).toBeNull();

    // Enable visual order
    const visualToggle = screen.getByText('Enable Independent Song Row Visual Order').closest('button')!;
    fireEvent.click(visualToggle);

    // Now the reorder list should appear
    expect(screen.getByText('Song Row Visual Order')).toBeDefined();
  });

  it('resets settings to defaults', () => {
    // Start with custom settings
    localStorage.setItem('fst:appSettings', JSON.stringify({
      showLead: false,
      metadataShowScore: false,
    }));

    renderSettings();

    const resetBtn = screen.getByText('Reset All Settings');
    fireEvent.click(resetBtn);

    // ConfirmAlert dialog appears — click Yes
    const yesBtn = screen.getByText('Yes');
    fireEvent.click(yesBtn);

    const stored = JSON.parse(localStorage.getItem('fst:appSettings')!);
    expect(stored.showLead).toBe(true);
    expect(stored.metadataShowScore).toBe(true);
  });

  it('renders Festival Score Tracker Version section', () => {
    renderSettings();
    expect(screen.getByText('Festival Score Tracker Version')).toBeDefined();
    expect(screen.getByText('App Version')).toBeDefined();
    expect(screen.getByText('Service Version')).toBeDefined();
  });

  it('displays service version after fetch', async () => {
    renderSettings();
    await waitFor(() => {
      expect(screen.getByText('1.0.0')).toBeDefined();
    });
  });
});
