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

const mockIsMobileChromeOverride = vi.hoisted(() => ({ value: null as boolean | null }));

vi.mock('../../../src/hooks/ui/useIsMobile', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../src/hooks/ui/useIsMobile')>();
  return {
    ...actual,
    useIsMobileChrome: () => mockIsMobileChromeOverride.value ?? window.matchMedia('(max-width: 768px)').matches,
  };
});

import { Colors, Opacity } from '@festival/theme';
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
    progressPercent: null,
    elapsedSeconds: null,
    estimatedRemainingSeconds: null,
    branches: null,
  },
  workerStatus: {
    workerKey: 'scraper',
    status: 'online',
    rawStatus: 'running',
    mode: 'scraper',
    instanceId: 'settings-test-worker',
    startedAt: '2026-04-20T11:55:00Z',
    lastHeartbeatAt: '2026-04-20T12:35:00Z',
    lastStatusChangeAt: '2026-04-20T11:55:00Z',
    heartbeatAgeSeconds: 5,
    staleAfterSeconds: 90,
    message: 'ready',
    currentOperation: null,
    lastOperation: {
      operationKey: 'rankings.instrument.Solo_Guitar',
      operationLabel: 'Computing Lead Rankings',
      status: 'completed',
      phase: 'ComputingRankings',
      subOperation: 'per_instrument_rankings',
      detail: 'Solo_Guitar',
      startedAt: '2026-04-20T12:20:00Z',
      updatedAt: '2026-04-20T12:25:00Z',
      endedAt: '2026-04-20T12:25:00Z',
      progressPercent: 100,
      elapsedSeconds: 300,
      estimatedRemainingSeconds: null,
    },
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

const defaultBandSyncStatus = {
  bandId: 'band-1',
  bandType: 'Band_Duets',
  teamKey: 'member-a:member-b',
  isTracked: true,
  processing: {
    status: 'complete',
    lookupsChecked: 10,
    totalLookupsToCheck: 10,
    entriesFound: 4,
    startedAt: '2026-04-20T12:31:00Z',
    completedAt: '2026-04-20T12:32:00Z',
  },
};

beforeAll(() => {
  stubScrollTo();
  stubResizeObserver();
  stubElementDimensions();
  if (typeof Range !== 'undefined') {
    const rect = { top: 0, left: 0, bottom: 16, right: 120, width: 120, height: 16, x: 0, y: 0, toJSON() { return this; } };
    Object.defineProperty(Range.prototype, 'getBoundingClientRect', {
      configurable: true,
      value: () => rect,
    });
    Object.defineProperty(Range.prototype, 'getClientRects', {
      configurable: true,
      value: () => [] as unknown as DOMRectList,
    });
  }
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
  mockIsMobileChromeOverride.value = null;
  setViewportQueries();
  // Mock fetch for /api/version
  globalThis.fetch = vi.fn().mockImplementation((url: string) => {
    if (typeof url === 'string' && url.includes('/api/version')) {
      return Promise.resolve({ ok: true, json: () => Promise.resolve({ version: '1.0.0' }) });
    }
    if (typeof url === 'string' && url.includes('/api/service-info')) {
      return Promise.resolve({ ok: true, json: () => Promise.resolve(defaultServiceInfo) });
    }
    if (typeof url === 'string' && url.includes('/api/bands/') && url.includes('/sync-status')) {
      return Promise.resolve({ ok: true, json: () => Promise.resolve(defaultBandSyncStatus) });
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

    delete (HTMLElement.prototype as unknown as Record<string, unknown>)['scrollWidth'];
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
    expect(screen.queryByRole('button', { name: 'Quick Links' })).toBeNull();
    expect(screen.getByTestId('test-header-portal').childElementCount).toBe(0);
    expect(screen.getByText('App Settings')).toBeDefined();
  });

  it('hides the settings title and header quick links in PWA mobile chrome', () => {
    mockIsMobileChromeOverride.value = true;
    setViewportQueries({ mobile: false, wide: false });

    renderSettings();

    expect(screen.queryByRole('heading', { name: 'Settings' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Quick Links' })).toBeNull();
    expect(screen.getByTestId('test-header-portal').childElementCount).toBe(0);
    expect(screen.getByText('App Settings')).toBeDefined();
  });

  it('renders App Settings section', () => {
    renderSettings();
    expect(screen.getByText('App Settings')).toBeDefined();
  });

  it('renders a Licenses navigation row', () => {
    renderSettings();
    const licensesLink = screen.getByRole('link', { name: 'Licenses' });
    expect(licensesLink).toHaveAttribute('href', '/settings/licenses');
    expect(screen.getByText('Open source package license details.')).toBeDefined();

    const licensesChevron = licensesLink.querySelector('svg[aria-hidden="true"]');
    expect(licensesChevron).toHaveAttribute('width', '20');
    expect(licensesChevron).toHaveAttribute('height', '20');
    expect(licensesChevron).toHaveStyle({ color: Colors.textPrimary });
  });

  it('renders debug diagnostics toggles and persists them for mobile PWA sessions', () => {
    renderSettings();

    expect(screen.getByText('Diagnostics')).toBeDefined();
    const diagnosticsToggle = screen.getByRole('button', { name: /^Tap Diagnostics\b/i });
    const telemetryToggle = screen.getByRole('button', { name: /Upload Tap Telemetry/i });

    expect(telemetryToggle).toBeDisabled();

    fireEvent.click(diagnosticsToggle);
    expect(localStorage.getItem('fst.tapDiagnostics')).toBe('1');

    const enabledTelemetryToggle = screen.getByRole('button', { name: /Upload Tap Telemetry/i });
    expect(enabledTelemetryToggle).not.toBeDisabled();
    fireEvent.click(enabledTelemetryToggle);
    expect(localStorage.getItem('fst.tapTelemetry')).toBe('1');

    fireEvent.click(screen.getByRole('button', { name: /^Tap Diagnostics\b/i }));
    expect(localStorage.getItem('fst.tapDiagnostics')).toBeNull();
    expect(localStorage.getItem('fst.tapTelemetry')).toBeNull();
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

  it('registers mobile quick links for the shell FAB and opens the modal through the shared controller', async () => {
    setViewportQueries({ mobile: true, wide: false });

    renderSettings({ withQuickLinksHarness: true });

    expect(screen.queryByRole('button', { name: 'Quick Links' })).toBeNull();
    fireEvent.click(await screen.findByTestId('test-open-page-quick-links'));

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
    expect(within(list).getByTestId('settings-quick-link-licenses')).toHaveTextContent('Licenses');
    expect(within(list).getByTestId('settings-quick-link-export')).toHaveTextContent('Export Data');
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

  it('renders Export Data section disabled until a profile is selected', () => {
    renderSettings();
    expect(screen.getAllByText('Export Data').length).toBeGreaterThanOrEqual(2);
    expect(screen.getByText('Select a player or band profile to export data.')).toBeDefined();
    const exportButton = screen.getByRole('button', { name: 'Export Data' });
    expect(exportButton).toHaveProperty('disabled', true);
    expect(exportButton).toHaveStyle({ opacity: String(Opacity.faded) });
  });

  it('downloads the selected player export from Settings', async () => {
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'tracked-player-1', displayName: 'Tracked Player' }));

    const createObjectUrl = vi.fn().mockReturnValue('blob:test-export');
    const revokeObjectUrl = vi.fn();
    Object.defineProperty(URL, 'createObjectURL', { configurable: true, value: createObjectUrl });
    Object.defineProperty(URL, 'revokeObjectURL', { configurable: true, value: revokeObjectUrl });
    const clickSpy = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});

    globalThis.fetch = vi.fn().mockImplementation((url: string) => {
      if (typeof url === 'string' && url.includes('/api/version')) {
        return Promise.resolve({ ok: true, json: () => Promise.resolve({ version: '1.0.0' }) });
      }
      if (typeof url === 'string' && url.includes('/api/service-info')) {
        return Promise.resolve({ ok: true, json: () => Promise.resolve(defaultServiceInfo) });
      }
      if (typeof url === 'string' && url.includes('/api/bands/') && url.includes('/sync-status')) {
        return Promise.resolve({ ok: true, json: () => Promise.resolve(defaultBandSyncStatus) });
      }
      if (typeof url === 'string' && url.includes('/sync-status')) {
        return Promise.resolve({ ok: true, json: () => Promise.resolve(defaultSyncStatus) });
      }
      if (typeof url === 'string' && url.includes('/api/player/tracked-player-1/export')) {
        return Promise.resolve({
          ok: true,
          blob: () => Promise.resolve(new Blob(['xlsx'])),
          headers: { get: (name: string) => name.toLowerCase() === 'content-disposition' ? 'attachment; filename="fst-export.xlsx"' : null },
        });
      }
      return Promise.resolve({ ok: false, statusText: 'Not Found', json: () => Promise.resolve({}) });
    }) as unknown as typeof fetch;

    renderSettings();
    const exportButton = await screen.findByRole('button', { name: 'Export Data' });
    await waitFor(() => {
      expect(exportButton).toHaveProperty('disabled', false);
    });
    expect(exportButton).toHaveStyle({ backgroundColor: Colors.chipSelected });
    fireEvent.click(exportButton);

    await waitFor(() => {
      expect(globalThis.fetch).toHaveBeenCalledWith('/api/player/tracked-player-1/export', expect.objectContaining({ cache: 'no-store' }));
      expect(clickSpy).toHaveBeenCalled();
      expect(revokeObjectUrl).toHaveBeenCalledWith('blob:test-export');
    });

    clickSpy.mockRestore();
  });

  it('allows player export before selected player sync is complete', async () => {
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'tracked-player-1', displayName: 'Tracked Player' }));

    globalThis.fetch = vi.fn().mockImplementation((url: string) => {
      if (typeof url === 'string' && url.includes('/api/version')) {
        return Promise.resolve({ ok: true, json: () => Promise.resolve({ version: '1.0.0' }) });
      }
      if (typeof url === 'string' && url.includes('/api/service-info')) {
        return Promise.resolve({ ok: true, json: () => Promise.resolve(defaultServiceInfo) });
      }
      if (typeof url === 'string' && url.includes('/sync-status')) {
        return Promise.resolve({
          ok: true,
          json: () => Promise.resolve({
            ...defaultSyncStatus,
            backfill: {
              status: 'pending',
              songsChecked: 0,
              totalSongsToCheck: 100,
              entriesFound: 0,
              startedAt: null,
              completedAt: null,
            },
          }),
        });
      }
      return Promise.resolve({ ok: false, statusText: 'Not Found', json: () => Promise.resolve({}) });
    }) as unknown as typeof fetch;

    renderSettings();

    await waitFor(() => {
      expect(screen.getByText('Download an Excel workbook archive with available data for Tracked Player.')).toBeDefined();
    });
    expect(screen.getByRole('button', { name: 'Export Data' })).toHaveProperty('disabled', false);
    expect(globalThis.fetch).not.toHaveBeenCalledWith('/api/player/tracked-player-1/export', expect.anything());
  });

  it('downloads the selected band export without using a stale tracked player', async () => {
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'stale-player-1', displayName: 'Stale Player' }));
    localStorage.setItem('fst:selectedProfile', JSON.stringify({
      type: 'band',
      bandId: 'band-1',
      bandType: 'Band_Duets',
      teamKey: 'member-a:member-b',
      displayName: 'Band Buddies',
      members: [
        { accountId: 'member-a', displayName: 'Member A' },
        { accountId: 'member-b', displayName: 'Member B' },
      ],
    }));

    const createObjectUrl = vi.fn().mockReturnValue('blob:test-band-export');
    const revokeObjectUrl = vi.fn();
    Object.defineProperty(URL, 'createObjectURL', { configurable: true, value: createObjectUrl });
    Object.defineProperty(URL, 'revokeObjectURL', { configurable: true, value: revokeObjectUrl });
    const clickSpy = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});

    globalThis.fetch = vi.fn().mockImplementation((url: string) => {
      if (typeof url === 'string' && url.includes('/api/version')) {
        return Promise.resolve({ ok: true, json: () => Promise.resolve({ version: '1.0.0' }) });
      }
      if (typeof url === 'string' && url.includes('/api/service-info')) {
        return Promise.resolve({ ok: true, json: () => Promise.resolve(defaultServiceInfo) });
      }
      if (typeof url === 'string' && url.includes('/api/bands/Band_Duets/member-a%3Amember-b/sync-status')) {
        return Promise.resolve({ ok: true, json: () => Promise.resolve(defaultBandSyncStatus) });
      }
      if (typeof url === 'string' && url.includes('/api/bands/Band_Duets/member-a%3Amember-b/export')) {
        return Promise.resolve({
          ok: true,
          blob: () => Promise.resolve(new Blob(['zip'])),
          headers: { get: (name: string) => name.toLowerCase() === 'content-disposition' ? 'attachment; filename="band-export.zip"' : null },
        });
      }
      return Promise.resolve({ ok: false, statusText: 'Not Found', json: () => Promise.resolve({}) });
    }) as unknown as typeof fetch;

    renderSettings();
    const exportButton = await screen.findByRole('button', { name: 'Export Data' });
    await waitFor(() => {
      expect(exportButton).toHaveProperty('disabled', false);
    });
    fireEvent.click(exportButton);

    await waitFor(() => {
      expect(globalThis.fetch).toHaveBeenCalledWith('/api/bands/Band_Duets/member-a%3Amember-b/export', expect.objectContaining({ cache: 'no-store' }));
      expect(globalThis.fetch).not.toHaveBeenCalledWith('/api/player/stale-player-1/export', expect.anything());
      expect(clickSpy).toHaveBeenCalled();
      expect(revokeObjectUrl).toHaveBeenCalledWith('blob:test-band-export');
    });

    clickSpy.mockRestore();
  });

  it('allows band export before selected band sync is complete', async () => {
    localStorage.setItem('fst:selectedProfile', JSON.stringify({
      type: 'band',
      bandId: 'band-1',
      bandType: 'Band_Duets',
      teamKey: 'member-a:member-b',
      displayName: 'Band Buddies',
      members: [],
    }));

    globalThis.fetch = vi.fn().mockImplementation((url: string) => {
      if (typeof url === 'string' && url.includes('/api/version')) {
        return Promise.resolve({ ok: true, json: () => Promise.resolve({ version: '1.0.0' }) });
      }
      if (typeof url === 'string' && url.includes('/api/service-info')) {
        return Promise.resolve({ ok: true, json: () => Promise.resolve(defaultServiceInfo) });
      }
      if (typeof url === 'string' && url.includes('/api/bands/') && url.includes('/sync-status')) {
        return Promise.resolve({
          ok: true,
          json: () => Promise.resolve({
            ...defaultBandSyncStatus,
            processing: { ...defaultBandSyncStatus.processing, status: 'pending', lookupsChecked: 2 },
          }),
        });
      }
      return Promise.resolve({ ok: false, statusText: 'Not Found', json: () => Promise.resolve({}) });
    }) as unknown as typeof fetch;

    renderSettings();

    await waitFor(() => {
      expect(screen.getByText('Download an Excel workbook archive with available data for Band Buddies.')).toBeDefined();
    });
    expect(screen.getByRole('button', { name: 'Export Data' })).toHaveProperty('disabled', false);
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
    expect(within(screen.getByTestId('settings-service-info-row-last-update-start')).getByText('Most recent leaderboard update start')).toBeDefined();
    expect(within(screen.getByTestId('settings-service-info-row-current-update-start')).getByText('Current leaderboard update start')).toBeDefined();
    expect(within(screen.getByTestId('settings-service-info-row-update-status')).getByText('Leaderboard update status')).toBeDefined();
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
      expect(within(screen.getByTestId('settings-service-info-row-last-update-start')).getByText(new Date(defaultServiceInfo.lastCompletedUpdate.startedAt).toLocaleString())).toBeDefined();
    });

    expect(within(screen.getByTestId('settings-service-info-row-last-update-complete')).getByText(new Date(defaultServiceInfo.lastCompletedUpdate.completedAt).toLocaleString())).toBeDefined();
    expect(within(screen.getByTestId('settings-service-info-row-current-update-start')).getByText('N/A')).toBeDefined();
    expect(within(screen.getByTestId('settings-service-info-row-worker-status')).getByText('Online')).toBeDefined();
    expect(within(screen.getByTestId('settings-service-info-row-worker-activity')).getByText('Computing Lead Rankings')).toBeDefined();
    expect(within(screen.getByTestId('settings-service-info-row-worker-activity-start')).getByText(new Date(defaultServiceInfo.workerStatus.lastOperation.startedAt).toLocaleString())).toBeDefined();
    expect(within(screen.getByTestId('settings-service-info-row-worker-activity-update')).getByText(new Date(defaultServiceInfo.workerStatus.lastOperation.updatedAt).toLocaleString())).toBeDefined();
    expect(within(screen.getByTestId('settings-service-info-row-worker-activity-end')).getByText(new Date(defaultServiceInfo.workerStatus.lastOperation.endedAt).toLocaleString())).toBeDefined();
    expect(within(screen.getByTestId('settings-service-info-row-worker-heartbeat')).getByText(new Date(defaultServiceInfo.workerStatus.lastHeartbeatAt).toLocaleString())).toBeDefined();
    expect(within(screen.getByTestId('settings-service-info-row-update-status')).getByText('Idle')).toBeDefined();
    expect(within(screen.getByTestId('settings-service-info-row-update-sub-status')).getByText('Waiting for the next scheduled update')).toBeDefined();
    expect(within(screen.getByTestId('settings-service-info-row-update-step-position')).getByText('N/A')).toBeDefined();
    expect(within(screen.getByTestId('settings-service-info-row-update-phase-progress')).getByText('N/A')).toBeDefined();
    expect(within(screen.getByTestId('settings-service-info-row-update-overall-progress')).getByText('N/A')).toBeDefined();
    expect(within(screen.getByTestId('settings-service-info-row-update-eta')).getByText('N/A')).toBeDefined();
    expect(within(screen.getByTestId('settings-service-info-row-next-scheduled-update')).getByText(new Date(defaultServiceInfo.nextScheduledUpdateAt).toLocaleString())).toBeDefined();
  });

  it('renders purple arc spinners on Status and Step rows when update is in progress', async () => {
    const updatingServiceInfo = {
      ...defaultServiceInfo,
      currentUpdate: {
        status: 'updating',
        startedAt: '2026-04-20T12:45:00Z',
        phase: 'Scraping',
        subOperation: null,
        progressPercent: 25,
        elapsedSeconds: 180,
        estimatedRemainingSeconds: 90,
        branches: null,
      },
    };
    globalThis.fetch = vi.fn().mockImplementation((url: string) => {
      if (typeof url === 'string' && url.includes('/api/version')) {
        return Promise.resolve({ ok: true, json: () => Promise.resolve({ version: '1.0.0' }) });
      }
      if (typeof url === 'string' && url.includes('/api/service-info')) {
        return Promise.resolve({ ok: true, json: () => Promise.resolve(updatingServiceInfo) });
      }
      if (typeof url === 'string' && url.includes('/sync-status')) {
        return Promise.resolve({ ok: true, json: () => Promise.resolve(defaultSyncStatus) });
      }
      return Promise.resolve({ ok: false, statusText: 'Not Found', json: () => Promise.resolve({}) });
    }) as unknown as typeof fetch;

    renderSettings();

    const statusRow = await screen.findByTestId('settings-service-info-row-update-status');
    const stepRow = await screen.findByTestId('settings-service-info-row-update-sub-status');
    await waitFor(() => {
      expect(within(statusRow).getByTestId('arc-spinner')).toBeDefined();
      expect(within(stepRow).getByTestId('arc-spinner')).toBeDefined();
    });

    // Non-status/step rows must never show a spinner.
    expect(within(screen.getByTestId('settings-service-info-row-last-update-start')).queryByTestId('arc-spinner')).toBeNull();
    expect(within(screen.getByTestId('settings-service-info-row-current-update-start')).queryByTestId('arc-spinner')).toBeNull();
    expect(within(screen.getByTestId('settings-service-info-row-last-update-start')).getByText(new Date(defaultServiceInfo.lastCompletedUpdate.startedAt).toLocaleString())).toBeDefined();
    expect(within(screen.getByTestId('settings-service-info-row-last-update-start')).queryByText(new Date(updatingServiceInfo.currentUpdate.startedAt).toLocaleString())).toBeNull();
    expect(within(screen.getByTestId('settings-service-info-row-current-update-start')).getByText(new Date(updatingServiceInfo.currentUpdate.startedAt).toLocaleString())).toBeDefined();
    expect(within(screen.getByTestId('settings-service-info-row-next-scheduled-update')).queryByTestId('arc-spinner')).toBeNull();
    expect(within(screen.getByTestId('settings-service-info-row-update-step-position')).getByText('Step 2 of 15: Scraping')).toBeDefined();
    expect(within(screen.getByTestId('settings-service-info-row-update-phase-progress')).getByText('25.0%')).toBeDefined();
    expect(within(screen.getByTestId('settings-service-info-row-update-overall-progress')).getByText('13.3%')).toBeDefined();
    expect(within(screen.getByTestId('settings-service-info-row-update-eta')).getByText('1m 30s')).toBeDefined();
  });

  it('does not render spinners on Status/Step rows when update is idle', async () => {
    renderSettings();

    const statusRow = await screen.findByTestId('settings-service-info-row-update-status');
    const stepRow = await screen.findByTestId('settings-service-info-row-update-sub-status');
    await waitFor(() => {
      expect(within(statusRow).getByText('Idle')).toBeDefined();
    });
    expect(within(statusRow).queryByTestId('arc-spinner')).toBeNull();
    expect(within(stepRow).queryByTestId('arc-spinner')).toBeNull();
  });

  it('shows stale worker activity without treating leaderboard update as active', async () => {
    const staleServiceInfo = {
      ...defaultServiceInfo,
      workerStatus: {
        ...defaultServiceInfo.workerStatus,
        status: 'stale',
        rawStatus: 'running',
        lastHeartbeatAt: '2026-04-20T12:10:00Z',
        currentOperation: {
          operationKey: 'rankings.band.Band_Trios',
          operationLabel: 'Computing Band Trios Rankings',
          status: 'running',
          phase: 'ComputingRankings',
          subOperation: 'band_rankings',
          detail: 'Band_Trios',
          startedAt: '2026-04-20T12:00:00Z',
          updatedAt: '2026-04-20T12:10:00Z',
          endedAt: null,
          progressPercent: null,
          elapsedSeconds: 600,
          estimatedRemainingSeconds: null,
        },
        lastOperation: defaultServiceInfo.workerStatus.lastOperation,
      },
    };
    globalThis.fetch = vi.fn().mockImplementation((url: string) => {
      if (typeof url === 'string' && url.includes('/api/version')) {
        return Promise.resolve({ ok: true, json: () => Promise.resolve({ version: '1.0.0' }) });
      }
      if (typeof url === 'string' && url.includes('/api/service-info')) {
        return Promise.resolve({ ok: true, json: () => Promise.resolve(staleServiceInfo) });
      }
      if (typeof url === 'string' && url.includes('/sync-status')) {
        return Promise.resolve({ ok: true, json: () => Promise.resolve(defaultSyncStatus) });
      }
      return Promise.resolve({ ok: false, statusText: 'Not Found', json: () => Promise.resolve({}) });
    }) as unknown as typeof fetch;

    renderSettings();

    await waitFor(() => {
      expect(within(screen.getByTestId('settings-service-info-row-worker-status')).getByText('Stale')).toBeDefined();
    });

    expect(within(screen.getByTestId('settings-service-info-row-worker-activity')).getByText('Computing Band Trios Rankings')).toBeDefined();
    expect(within(screen.getByTestId('settings-service-info-row-worker-activity-start')).getByText(new Date(staleServiceInfo.workerStatus.currentOperation.startedAt).toLocaleString())).toBeDefined();
    expect(within(screen.getByTestId('settings-service-info-row-worker-activity-update')).getByText(new Date(staleServiceInfo.workerStatus.currentOperation.updatedAt).toLocaleString())).toBeDefined();
    expect(within(screen.getByTestId('settings-service-info-row-worker-activity-end')).getByText('N/A')).toBeDefined();
    expect(within(screen.getByTestId('settings-service-info-row-update-status')).queryByTestId('arc-spinner')).toBeNull();
    expect(within(screen.getByTestId('settings-service-info-row-update-sub-status')).queryByTestId('arc-spinner')).toBeNull();
  });

  it('shows tracked player rows when a player is selected', async () => {
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'tracked-player-1', displayName: 'Tracked Player' }));
    renderSettings();

    await waitFor(() => {
      expect(within(screen.getByTestId('settings-service-info-row-selected-player-id')).getByText('Selected player ID')).toBeDefined();
    });

    expect(within(screen.getByTestId('settings-service-info-row-selected-player-id')).getByText('tracked-player-1')).toBeDefined();
    expect(within(screen.getByTestId('settings-service-info-row-selected-player-rivals-status')).getByText('Selected player Rivals status')).toBeDefined();
    expect(within(screen.getByTestId('settings-service-info-row-selected-player-rivals-status')).getByText('Complete')).toBeDefined();
  });

  it('displays service version after fetch', async () => {
    renderSettings();
    await waitFor(() => {
      expect(screen.getByText('1.0.0')).toBeDefined();
    });
  });

  it('handles version fetch failure gracefully', async () => {
    globalThis.fetch = vi.fn().mockImplementation(() =>
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

  it('renders and toggles experimental leaderboard ranks for all users', () => {
    renderSettings();
    const toggle = screen.getByText('Enable Experimental Leaderboard Ranks').closest('button')!;
    expect(screen.getByText('Enable this to see more ranking mechanisms in the Leaderboards page.')).toBeDefined();

    fireEvent.click(toggle);

    const stored = JSON.parse(localStorage.getItem('fst:appSettings')!);
    expect(stored.enableExperimentalRanks).toBe(true);
  });

  it('toggles Show Buttons In Header (Mobile)', () => {
    renderSettings();
    const toggle = screen.getByText('Show Buttons In Header (Mobile)').closest('button')!;
    fireEvent.click(toggle);

    const stored = JSON.parse(localStorage.getItem('fst:appSettings')!);
    expect(stored.showButtonsInHeaderMobile).toBe(false);
  });

  it('does not render a mobile quick links header trigger when the header setting is off', () => {
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
    'Songs',
    'Song Info',
    'Statistics',
    'Suggestions',
  ] as const)('opens %s first-run replay carousel', async (label) => {
    renderSettings();
    // Some labels also appear in unrelated settings controls; select the First Run row with its Show button.
    const row = screen.getAllByText(label)
      .map(element => element.closest('button'))
      .find((button): button is HTMLButtonElement => !!button && within(button).queryByText('Show') !== null)!;
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
