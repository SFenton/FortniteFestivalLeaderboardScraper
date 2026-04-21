import { describe, it, expect, vi, beforeAll, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor, act, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { SettingsProvider } from '../../../src/contexts/SettingsContext';
import { FirstRunProvider } from '../../../src/contexts/FirstRunContext';
import { PageQuickLinksProvider, usePageQuickLinksController } from '../../../src/contexts/PageQuickLinksContext';
import { ScrollContainerProvider, useHeaderPortalRef, useQuickLinksRailPortalRef, useScrollContainer } from '../../../src/contexts/ScrollContainerContext';

vi.mock('../../../src/contexts/FeatureFlagsContext', () => ({
  useFeatureFlags: () => ({ rivals: true, compete: true, leaderboards: true, firstRun: true }),
}));

import SettingsPage from '../../../src/pages/settings/SettingsPage';
import { stubResizeObserver, stubScrollTo, stubElementDimensions } from '../../helpers/browserStubs';

const defaultServiceInfo = {
  lastCompletedUpdate: {
    startedAt: '2026-04-20T12:00:00Z',
    completedAt: '2026-04-20T12:30:00Z',
  },
  currentUpdate: {
    status: 'idle',
    startedAt: null,
    phase: null,
    subOperation: null,
  },
  nextScheduledUpdateAt: '2026-04-20T16:30:00Z',
};

const defaultSyncStatus = {
  accountId: 'tracked-player-1',
  isTracked: true,
  backfill: null,
  historyRecon: null,
  rivals: {
    status: 'complete',
    combosComputed: 8,
    totalCombosToCompute: 8,
    rivalsFound: 14,
    startedAt: '2026-04-20T12:31:00Z',
    completedAt: '2026-04-20T12:32:00Z',
  },
};

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
  setViewportQueries();
  // Mock fetch for /api/version
  global.fetch = vi.fn().mockImplementation((url: string) => {
    if (typeof url === 'string' && url.includes('/api/version')) {
      return Promise.resolve({ ok: true, json: () => Promise.resolve({ version: '1.0.0' }) });
    }
    if (typeof url === 'string' && url.includes('/api/service-info')) {
      return Promise.resolve({ ok: true, json: () => Promise.resolve(defaultServiceInfo) });
    }
    if (typeof url === 'string' && url.includes('/sync-status')) {
      return Promise.resolve({ ok: true, json: () => Promise.resolve(defaultSyncStatus) });
    }
    return Promise.resolve({ ok: false, statusText: 'Not Found', json: () => Promise.resolve({}) });
  }) as unknown as typeof fetch;
});

function mockScrollWidths(scale = 7) {
  const original = Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'scrollWidth');
  Object.defineProperty(HTMLElement.prototype, 'scrollWidth', {
    configurable: true,
    get() {
      const text = this.textContent ?? '';
      return Math.max(0, text.length * scale);
    },
  });

  return () => {
    if (original) {
      Object.defineProperty(HTMLElement.prototype, 'scrollWidth', original);
      return;
    }

    delete (HTMLElement.prototype as Partial<HTMLElement>).scrollWidth;
  };
}

function setViewportQueries({ mobile = false, wide = false }: { mobile?: boolean; wide?: boolean; } = {}) {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    configurable: true,
    value: vi.fn().mockImplementation((query: string) => ({
      matches: query.includes('max-width') ? mobile : query.includes('min-width') ? wide : false,
      media: query,
      onchange: null,
      addListener: vi.fn(),
      removeListener: vi.fn(),
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      dispatchEvent: vi.fn(),
    })),
  });
}

function ShellRefInjector({ children }: { children: React.ReactNode }) {
  const scrollRef = useScrollContainer();
  const setPortalNode = useHeaderPortalRef();
  const setQuickLinksRailNode = useQuickLinksRailPortalRef();

  return (
    <>
      <div ref={setPortalNode} data-testid="test-header-portal" />
      <div
        ref={(el) => {
          if (el && !scrollRef.current) {
            Object.defineProperty(el, 'scrollHeight', { value: 5000, writable: true, configurable: true });
            Object.defineProperty(el, 'scrollTop', { value: 0, writable: true, configurable: true });
            Object.defineProperty(el, 'clientHeight', { value: 800, writable: true, configurable: true });
            el.scrollTo = (() => {}) as any;
            scrollRef.current = el;
          }
        }}
        data-testid="test-scroll-container"
      >
        {children}
      </div>
      <div ref={setQuickLinksRailNode} data-testid="test-quick-links-portal" />
    </>
  );
}

function PageQuickLinksHarness() {
  const pageQuickLinks = usePageQuickLinksController();

  if (!pageQuickLinks.hasPageQuickLinks) {
    return null;
  }

  return (
    <button type="button" data-testid="test-open-page-quick-links" onClick={() => pageQuickLinks.openPageQuickLinks()}>
      Open Settings Quick Links
    </button>
  );
}

function renderSettings({ withQuickLinksHarness = false }: { withQuickLinksHarness?: boolean; } = {}) {
  return render(
    <ScrollContainerProvider>
      <ShellRefInjector>
        <MemoryRouter>
          <PageQuickLinksProvider>
            <SettingsProvider>
              <FirstRunProvider>
                <SettingsPage />
                {withQuickLinksHarness ? <PageQuickLinksHarness /> : null}
              </FirstRunProvider>
            </SettingsProvider>
          </PageQuickLinksProvider>
        </MemoryRouter>
      </ShellRefInjector>
    </ScrollContainerProvider>,
  );
}

describe('SettingsPage', () => {
  it('renders content on mobile', () => {
    setViewportQueries({ mobile: true, wide: false });
    renderSettings();
    expect(screen.queryByRole('heading', { name: 'Settings' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Quick Links' })).toBeDefined();
    expect(screen.getByText('App Settings')).toBeDefined();
  });

  it('renders App Settings section', () => {
    renderSettings();
    expect(screen.getByText('App Settings')).toBeDefined();
  });

  it('registers quick links with the shared page controller', async () => {
    renderSettings({ withQuickLinksHarness: true });
    expect(await screen.findByTestId('test-open-page-quick-links')).toBeDefined();
  });

  it('renders a compact desktop quick links trigger and opens the modal', async () => {
    renderSettings();

    const trigger = await screen.findByRole('button', { name: 'Quick Links' });
    fireEvent.click(trigger);

    const dialog = await screen.findByRole('dialog', { name: 'Quick Links' });
    expect(dialog).toBeDefined();

    fireEvent.click(screen.getByTestId('settings-quick-link-item-shop'));
    fireEvent.transitionEnd(dialog);

    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Quick Links' })).toBeNull();
    });
  });

  it('renders a mobile quick links trigger and opens the modal', async () => {
    setViewportQueries({ mobile: true, wide: false });

    renderSettings();

    const trigger = await screen.findByRole('button', { name: 'Quick Links' });
    fireEvent.click(trigger);

    const dialog = await screen.findByRole('dialog', { name: 'Quick Links' });
    expect(dialog).toBeDefined();

    fireEvent.click(screen.getByTestId('settings-quick-link-first-run'));
    fireEvent.transitionEnd(dialog);

    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Quick Links' })).toBeNull();
    });
  });

  it('opens the settings quick links modal with the expected sections and closes on selection', async () => {
    renderSettings({ withQuickLinksHarness: true });

    fireEvent.click(await screen.findByTestId('test-open-page-quick-links'));

    const list = await screen.findByTestId('settings-quick-links-modal-list');
    expect(within(list).getByTestId('settings-quick-link-app-settings')).toHaveTextContent('App Settings');
    expect(within(list).getByTestId('settings-quick-link-item-shop')).toHaveTextContent('Item Shop');
    expect(within(list).getByTestId('settings-quick-link-service-info')).toHaveTextContent('Service Info');
    expect(within(list).getByTestId('settings-quick-link-first-run')).toHaveTextContent('First Run Guides');
    expect(within(list).getByTestId('settings-quick-link-reset')).toHaveTextContent('Reset Settings');

    const dialog = screen.getByRole('dialog', { name: 'Quick Links' });
    fireEvent.click(within(list).getByTestId('settings-quick-link-reset'));
    fireEvent.transitionEnd(dialog);

    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Quick Links' })).toBeNull();
    });
  });

  it('renders Show Instruments section with all instruments', () => {
    renderSettings();
    expect(screen.getByText('Show Instruments')).toBeDefined();
    expect(screen.getByText('Lead')).toBeDefined();
    expect(screen.getByText('Bass')).toBeDefined();
    expect(screen.getByText('Drums')).toBeDefined();
    expect(screen.getByText('Tap Vocals')).toBeDefined();
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
      showPeripheralVocals: false,
      showPeripheralCymbals: false,
      showPeripheralDrums: false,
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

  it('renders Service Info section', () => {
    renderSettings();
    expect(screen.getByText('Service Info')).toBeDefined();
    expect(screen.getByText('Most recent leaderboard update start')).toBeDefined();
    expect(screen.getByText('Leaderboard update status')).toBeDefined();
  });

  it('keeps service info rows inline when every row fits on one line', async () => {
    renderSettings();

    const list = await screen.findByTestId('settings-service-info-list');
    expect(list.getAttribute('data-layout')).toBe('inline');
    expect(screen.getByTestId('settings-service-info-row-last-update-start').getAttribute('data-layout')).toBe('inline');
    expect(screen.getByTestId('settings-service-info-row-next-scheduled-update').getAttribute('data-layout')).toBe('inline');
  });

  it('stacks every service info row when any inline row would overflow', async () => {
    const restoreScrollWidths = mockScrollWidths(32);

    try {
      localStorage.setItem('fst:trackedPlayer', JSON.stringify({
        accountId: '195e93ef108143b2975ee46662d4d0e1',
        displayName: 'Tracked Player',
      }));

      renderSettings();

      await waitFor(() => {
        expect(screen.getByTestId('settings-service-info-list').getAttribute('data-layout')).toBe('stacked');
      });

      expect(screen.getByTestId('settings-service-info-row-last-update-start').getAttribute('data-layout')).toBe('stacked');
      expect(screen.getByTestId('settings-service-info-row-next-scheduled-update').getAttribute('data-layout')).toBe('stacked');
      expect(screen.getByTestId('settings-service-info-row-selected-player-id').getAttribute('data-layout')).toBe('stacked');
      expect(screen.getByTestId('settings-service-info-row-selected-player-rivals-status').getAttribute('data-layout')).toBe('stacked');
    } finally {
      restoreScrollWidths();
    }
  });

  it('displays service info values after fetch', async () => {
    renderSettings();

    await waitFor(() => {
      expect(screen.getByText(new Date(defaultServiceInfo.lastCompletedUpdate.startedAt).toLocaleString())).toBeDefined();
    });

    expect(screen.getByText(new Date(defaultServiceInfo.lastCompletedUpdate.completedAt).toLocaleString())).toBeDefined();
    expect(screen.getByText('Idle')).toBeDefined();
    expect(screen.getByText('Waiting for the next scheduled update')).toBeDefined();
    expect(screen.getByText(new Date(defaultServiceInfo.nextScheduledUpdateAt).toLocaleString())).toBeDefined();
  });

  it('shows tracked player rows when a player is selected', async () => {
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'tracked-player-1', displayName: 'Tracked Player' }));
    renderSettings();

    await waitFor(() => {
      expect(screen.getByText('Selected player ID')).toBeDefined();
    });

    expect(screen.getByText('tracked-player-1')).toBeDefined();
    expect(screen.getByText('Selected player Rivals status')).toBeDefined();
    expect(screen.getByText('Complete')).toBeDefined();
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

  it('toggles Show Buttons In Header (Mobile)', () => {
    renderSettings();
    const toggle = screen.getByText('Show Buttons In Header (Mobile)').closest('button')!;
    fireEvent.click(toggle);

    const stored = JSON.parse(localStorage.getItem('fst:appSettings')!);
    expect(stored.showButtonsInHeaderMobile).toBe(false);
  });

  it('hides the mobile quick links header trigger when the setting is off', () => {
    setViewportQueries({ mobile: true, wide: false });
    localStorage.setItem('fst:appSettings', JSON.stringify({ showButtonsInHeaderMobile: false }));

    renderSettings();

    expect(screen.queryByRole('button', { name: 'Quick Links' })).toBeNull();
    expect(screen.getByText('App Settings')).toBeDefined();
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
