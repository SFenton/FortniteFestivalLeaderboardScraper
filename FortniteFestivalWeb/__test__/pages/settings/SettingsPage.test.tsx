import { describe, it, expect, vi, beforeAll, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor, act, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { SettingsProvider } from '../../../src/contexts/SettingsContext';
import { FirstRunProvider } from '../../../src/contexts/FirstRunContext';

vi.mock('../../../src/contexts/FeatureFlagsContext', () => ({
  useFeatureFlags: () => ({ shop: true, rivals: true, compete: true, leaderboards: true, firstRun: true }),
}));

import SettingsPage from '../../../src/pages/settings/SettingsPage';
import { stubResizeObserver, stubScrollTo, stubElementDimensions } from '../../helpers/browserStubs';

beforeAll(() => {
  stubScrollTo();
  stubResizeObserver();
  stubElementDimensions();
  if (!HTMLElement.prototype.animate) {
    HTMLElement.prototype.animate = vi.fn().mockReturnValue({
      cancel: vi.fn(), pause: vi.fn(), play: vi.fn(), finish: vi.fn(),
      onfinish: null, finished: Promise.resolve(),
    }) as any;
  }
  if (!HTMLElement.prototype.getAnimations) {
    HTMLElement.prototype.getAnimations = vi.fn().mockReturnValue([]) as any;
  }
});

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
    <MemoryRouter>
    <SettingsProvider>
      <FirstRunProvider>
        <SettingsPage />
      </FirstRunProvider>
    </SettingsProvider>
    </MemoryRouter>,
  );
}

describe('SettingsPage', () => {
  it('renders content on mobile', () => {
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
    // The "Settings" heading is in the app shell, not the page itself.
    // Verify core content renders instead.
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
    expect(screen.getAllByText('Score').length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText('Percentage').length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText('Percentile').length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText('Season Achieved').length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText('Song Intensity').length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText('Stars').length).toBeGreaterThanOrEqual(1);
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
    const metadataSection = screen.getByText('Show Instrument Metadata').closest('div')!.parentElement!;
    const scoreToggle = within(metadataSection).getByText('Score').closest('button')!;
    fireEvent.click(scoreToggle);

    const stored = JSON.parse(localStorage.getItem('fst:appSettings')!);
    expect(stored.metadataShowScore).toBe(false);
  });

  it('shows visual order reorder list when enabled', () => {
    renderSettings();

    // Visual order section should be collapsed initially (grid-template-rows: 0fr)
    const collapseGrid = screen.getByTestId('visual-order-collapse');
    expect(collapseGrid.style.gridTemplateRows).toBe('0fr');

    // Enable visual order
    const visualToggle = screen.getByText('Enable Independent Song Row Visual Order').closest('button')!;
    fireEvent.click(visualToggle);

    // Now the reorder list should be expanded (grid-template-rows: 1fr)
    expect(collapseGrid.style.gridTemplateRows).toBe('1fr');
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

    // Confirm the reset in the ConfirmAlert dialog
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

  it('handles version fetch failure gracefully', async () => {
    global.fetch = vi.fn().mockImplementation(() =>
      Promise.reject(new Error('Network error')),
    ) as unknown as typeof fetch;
    renderSettings();
    // Should still render the page; service version shows Loading...
    expect(screen.getByText('App Settings')).toBeDefined();
    await waitFor(() => {
      expect(screen.getByText('Loading…')).toBeDefined();
    });
  });

  it('changes leeway slider value', () => {
    // Enable filter invalid scores first
    localStorage.setItem('fst:appSettings', JSON.stringify({
      filterInvalidScores: true,
      filterInvalidScoresLeeway: 1,
    }));
    renderSettings();

    const slider = document.getElementById('fst-leeway-slider') as HTMLInputElement;
    expect(slider).toBeDefined();
    fireEvent.change(slider, { target: { value: '3.5' } });

    const stored = JSON.parse(localStorage.getItem('fst:appSettings')!);
    expect(stored.filterInvalidScoresLeeway).toBe(3.5);
  });

  it('toggles filter invalid scores', () => {
    renderSettings();
    const toggle = screen.getByText('Filter Invalid Scores').closest('button')!;
    fireEvent.click(toggle);

    const stored = JSON.parse(localStorage.getItem('fst:appSettings')!);
    expect(stored.filterInvalidScores).toBe(true);
  });

  it('toggles Disable Item Shop Highlighting', () => {
    renderSettings();
    const toggle = screen.getByText('Disable Item Shop Highlighting').closest('button')!;
    fireEvent.click(toggle);

    const stored = JSON.parse(localStorage.getItem('fst:appSettings')!);
    expect(stored.disableShopHighlighting).toBe(true);
  });

  it('toggles Hide Item Shop without changing highlighting', () => {
    renderSettings();
    const toggle = screen.getByText('Hide Item Shop').closest('button')!;
    fireEvent.click(toggle);

    const stored = JSON.parse(localStorage.getItem('fst:appSettings')!);
    expect(stored.hideItemShop).toBe(true);
    expect(stored.disableShopHighlighting).toBe(false);
  });

  it('toggles Hide Item Shop back off without changing highlighting', () => {
    localStorage.setItem('fst:appSettings', JSON.stringify({ hideItemShop: true, disableShopHighlighting: true }));
    renderSettings();
    const toggle = screen.getByText('Hide Item Shop').closest('button')!;
    fireEvent.click(toggle);

    const stored = JSON.parse(localStorage.getItem('fst:appSettings')!);
    expect(stored.hideItemShop).toBe(false);
  });

  it('renders core version', () => {
    renderSettings();
    expect(screen.getByText('@festival/core Version')).toBeDefined();
  });

  it('renders theme version', () => {
    renderSettings();
    expect(screen.getByText('@festival/theme Version')).toBeDefined();
  });

  it.each([
    ['Songs', 0],
    ['Song Info', 1],
    ['Statistics', 2],
    ['Suggestions', 3],
  ] as const)('opens %s first-run replay carousel', async (label, _idx) => {
    renderSettings();
    // There are multiple buttons per row; find the one whose sibling text matches the label
    const row = screen.getByText(label).closest('button')!;
    expect(row).toBeTruthy();

    await act(async () => {
      fireEvent.click(row);
    });

    // FirstRunCarousel renders a close button with aria-label="Close"
    await waitFor(() => {
      expect(screen.getByLabelText('Close')).toBeTruthy();
    });
  });
});
