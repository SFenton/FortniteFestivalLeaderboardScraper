import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, act, waitFor, within } from '@testing-library/react';
import { useEffect } from 'react';
import SearchModal from '../../../src/components/search/SearchModal';
import { TestProviders } from '../../helpers/TestProviders';
import { LEGACY_TRACKED_PLAYER_STORAGE_KEY, SELECTED_PROFILE_STORAGE_KEY } from '../../../src/state/selectedProfile';

const mockNavigate = vi.fn();
vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal() as Record<string, unknown>;
  return { ...actual, useNavigate: () => mockNavigate };
});

const mockApi = vi.hoisted(() => ({
  getSongs: vi.fn(),
  getShop: vi.fn(),
  searchAccounts: vi.fn(),
  searchBands: vi.fn(),
}));

vi.mock('../../../src/api/client', () => ({ api: mockApi }));

vi.mock('../../../src/components/modals/components/ModalShell', () => ({
  default: ({ visible, title, children, onOpenComplete, onCloseComplete }: {
    visible: boolean; title: string; children: React.ReactNode;
    onOpenComplete?: () => void; onCloseComplete?: () => void;
  }) => {
    useEffect(() => {
      if (visible) onOpenComplete?.();
      else onCloseComplete?.();
    }, [visible, onOpenComplete, onCloseComplete]);
    if (!visible) return null;
    return <div role="dialog" aria-label={title}><h2>{title}</h2>{children}</div>;
  },
}));

const originalVisualViewportDescriptor = Object.getOwnPropertyDescriptor(window, 'visualViewport');
const originalInnerHeightDescriptor = Object.getOwnPropertyDescriptor(window, 'innerHeight');
const originalClientHeightDescriptor = Object.getOwnPropertyDescriptor(document.documentElement, 'clientHeight');

class MockVisualViewport extends EventTarget {
  height: number;
  offsetTop: number;

  constructor(height = 844, offsetTop = 0) {
    super();
    this.height = height;
    this.offsetTop = offsetTop;
  }

  set(height: number, offsetTop: number) {
    this.height = height;
    this.offsetTop = offsetTop;
    this.dispatchEvent(new Event('resize'));
    this.dispatchEvent(new Event('scroll'));
  }
}

function restoreProperty(target: object, key: PropertyKey, descriptor: PropertyDescriptor | undefined) {
  if (descriptor) {
    Object.defineProperty(target, key, descriptor);
  } else {
    Reflect.deleteProperty(target, key);
  }
}

function installVisualViewport({ height = 844, offsetTop = 0, innerHeight = 844, clientHeight = 844 } = {}) {
  const visualViewport = new MockVisualViewport(height, offsetTop);
  Object.defineProperty(window, 'visualViewport', { configurable: true, value: visualViewport });
  Object.defineProperty(window, 'innerHeight', { configurable: true, value: innerHeight });
  Object.defineProperty(document.documentElement, 'clientHeight', { configurable: true, value: clientHeight });
  return visualViewport;
}

function removeVisualViewport({ innerHeight = 844, clientHeight = 844 } = {}) {
  Object.defineProperty(window, 'visualViewport', { configurable: true, value: undefined });
  Object.defineProperty(window, 'innerHeight', { configurable: true, value: innerHeight });
  Object.defineProperty(document.documentElement, 'clientHeight', { configurable: true, value: clientHeight });
}

function setVisualViewport(visualViewport: MockVisualViewport, height: number, offsetTop = 0) {
  act(() => { visualViewport.set(height, offsetTop); });
}

function getModalBody(): HTMLElement {
  return screen.getByRole('dialog').querySelector('h2')?.nextElementSibling as HTMLElement;
}

function mockElementRect(element: HTMLElement, rect: Partial<DOMRect>) {
  return vi.spyOn(element, 'getBoundingClientRect').mockReturnValue({
    x: rect.x ?? 0,
    y: rect.y ?? rect.top ?? 0,
    top: rect.top ?? 0,
    right: rect.right ?? 0,
    bottom: rect.bottom ?? 0,
    left: rect.left ?? 0,
    width: rect.width ?? 0,
    height: rect.height ?? 0,
    toJSON: () => ({}),
  } as DOMRect);
}

function expectCenteredHint(element: HTMLElement) {
  expect(element.style.display).toBe('flex');
  expect(element.style.alignItems).toBe('center');
  expect(element.style.justifyContent).toBe('center');
  expect(element.style.textAlign).toBe('center');
}

function setViewportQueries({ mobile = false }: { mobile?: boolean } = {}) {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    configurable: true,
    value: vi.fn().mockImplementation((query: string) => ({
      matches: query.includes('max-width') ? mobile : false,
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

function renderModal(overrides: Partial<React.ComponentProps<typeof SearchModal>> = {}) {
  const props = {
    visible: true,
    onClose: vi.fn(),
    ...overrides,
  };
  const result = render(
    <TestProviders>
      <SearchModal {...props} />
    </TestProviders>,
  );
  return { ...result, props };
}

async function advanceAndFlush(ms: number) {
  await act(async () => { await vi.advanceTimersByTimeAsync(ms); });
  await act(async () => { await Promise.resolve(); });
}

const SEARCH_SETTLE_MS = 900;

function createDeferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}

function findAnimatedAncestor(text: string): HTMLElement | null {
  return findAnimatedAncestorFrom(screen.getByText(text) as HTMLElement | null);
}

function findSectionAnimatedAncestor(target: 'songs' | 'players' | 'bands', text: string): HTMLElement | null {
  return findAnimatedAncestorFrom(within(screen.getByTestId(`search-section-${target}`)).getByText(text) as HTMLElement | null);
}

function findAnimatedAncestorFrom(element: HTMLElement | null): HTMLElement | null {
  let el = element;
  while (el && el !== document.body) {
    if (el.style.animation.includes('fadeInUp')) return el;
    el = el.parentElement;
  }
  return null;
}

function getSectionHeading(target: 'songs' | 'players' | 'bands'): HTMLElement {
  const heading = screen.getByTestId(`search-section-${target}`).querySelector('h3');
  expect(heading).not.toBeNull();
  return heading as HTMLElement;
}

function expectFadeInDelay(element: HTMLElement | null, delayMs: number) {
  expect(element?.style.animation).toContain('fadeInUp');
  expect(element?.style.animation).toContain(`${delayMs}ms`);
}

function expectRushedFadeIn(element: HTMLElement | null) {
  expect(element?.style.animation).toContain('fadeInUp');
  expect(element?.style.animation).toMatch(/ease-out forwards$/);
}

function expectResultListEdgePadding(result: HTMLElement | null) {
  const resultList = result?.closest('[data-testid="search-result-list"]') as HTMLElement | null;
  expect(resultList?.style.paddingTop).toBe('40px');
  expect(resultList?.style.paddingBottom).toBe('40px');
  expect(resultList?.style.flexShrink).toBe('0');
}

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  vi.clearAllMocks();
  localStorage.removeItem(SELECTED_PROFILE_STORAGE_KEY);
  localStorage.removeItem(LEGACY_TRACKED_PLAYER_STORAGE_KEY);
  mockNavigate.mockClear();
  setViewportQueries();
  mockApi.getSongs.mockResolvedValue({
    count: 2,
    currentSeason: 5,
    songs: [
      { songId: 'song-1', title: 'Butter Barn Hoedown', artist: 'Epic Games' },
      { songId: 'song-2', title: "(Don't Fear) The Reaper", artist: 'Blue Öyster Cult' },
    ],
  });
  mockApi.getShop.mockResolvedValue({ songs: [] });
  mockApi.searchAccounts.mockResolvedValue({ results: [{ accountId: 'p1', displayName: 'PlayerOne' }] });
  mockApi.searchBands.mockResolvedValue({
    query: 'but', normalizedQuery: 'but', rankBy: 'appearance', page: 1, pageSize: 10, totalCount: 1,
    isAmbiguous: false, needsDisambiguation: false, interpretations: [],
    results: [{
      bandId: 'band-1',
      teamKey: 'p1,p2',
      bandType: 'Band_Duets',
      appearanceCount: 3,
      members: [
        { accountId: 'p1', displayName: 'PlayerOne', instruments: [] },
        { accountId: 'p2', displayName: 'PlayerTwo', instruments: [] },
      ],
      ranking: null,
      matchedInterpretationIds: [],
      matchedAccountIds: ['p1'],
    }],
  });
});

afterEach(() => {
  vi.runOnlyPendingTimers();
  vi.useRealTimers();
  restoreProperty(window, 'visualViewport', originalVisualViewportDescriptor);
  restoreProperty(window, 'innerHeight', originalInnerHeightDescriptor);
  restoreProperty(document.documentElement, 'clientHeight', originalClientHeightDescriptor);
});

describe('SearchModal', () => {
  it('opens with no target filter selected', () => {
    renderModal();
    expect(screen.getByRole('dialog', { name: 'Search' })).toBeDefined();
    expect(screen.getByRole('button', { name: 'Songs' })).toHaveAttribute('aria-pressed', 'false');
    expect(screen.getByRole('button', { name: 'Players' })).toHaveAttribute('aria-pressed', 'false');
    expect(screen.getByRole('button', { name: 'Bands' })).toHaveAttribute('aria-pressed', 'false');
  });

  it('restricts visible target filters and uses the configured placeholder', () => {
    renderModal({
      availableTargets: ['players', 'bands'],
      placeholderKey: 'search.placeholders.playersBands',
    });

    expect(screen.getByPlaceholderText('Search players or bands…')).toBeDefined();
    expect(screen.queryByRole('button', { name: 'Songs' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Players' })).toHaveAttribute('aria-pressed', 'false');
    expect(screen.getByRole('button', { name: 'Bands' })).toHaveAttribute('aria-pressed', 'false');
  });

  it('hides target filters and uses custom player selection for player-only scope', async () => {
    const onPlayerSelect = vi.fn();
    const { props } = renderModal({
      availableTargets: ['players'],
      onPlayerSelect,
    });

    expect(screen.getByPlaceholderText('Search players…')).toBeDefined();
    expect(screen.queryByRole('group', { name: 'Search targets' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Songs' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Players' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Bands' })).toBeNull();

    fireEvent.change(screen.getByPlaceholderText('Search players…'), { target: { value: 'pla' } });

    await advanceAndFlush(SEARCH_SETTLE_MS);

    const playerResult = await screen.findByTestId('search-player-result');
    fireEvent.click(playerResult);

    expect(mockApi.searchAccounts).toHaveBeenCalledWith('pla', 10);
    expect(mockApi.searchBands).not.toHaveBeenCalled();
    expect(props.onClose).toHaveBeenCalled();
    expect(onPlayerSelect).toHaveBeenCalledWith({ accountId: 'p1', displayName: 'PlayerOne' });
    expect(mockNavigate).not.toHaveBeenCalled();
  });

  it('toggles target filters on and off', () => {
    renderModal({
      availableTargets: ['players', 'bands'],
      placeholderKey: 'search.placeholders.playersBands',
    });

    const players = screen.getByRole('button', { name: 'Players' });
    const bands = screen.getByRole('button', { name: 'Bands' });

    fireEvent.click(players);
    expect(players).toHaveAttribute('aria-pressed', 'true');
    expect(bands).toHaveAttribute('aria-pressed', 'false');

    fireEvent.click(players);
    expect(players).toHaveAttribute('aria-pressed', 'false');

    fireEvent.click(bands);
    expect(players).toHaveAttribute('aria-pressed', 'false');
    expect(bands).toHaveAttribute('aria-pressed', 'true');
  });

  it('does not show a separator above mobile target buttons', () => {
    setViewportQueries({ mobile: true });
    renderModal();
    const filterGroup = screen.getByRole('group', { name: 'Search targets' });
    expect(filterGroup.getAttribute('style') ?? '').not.toContain('border-top');
  });

  it('pads mobile target buttons above safe-area bottoms', () => {
    setViewportQueries({ mobile: true });
    renderModal();
    const body = getModalBody();
    expect(body.style.padding).toContain('safe-area-inset-bottom');
  });

  it('lifts the mobile target buttons with a keyboard-aware transform while search is focused', async () => {
    const visualViewport = installVisualViewport();
    setViewportQueries({ mobile: true });

    renderModal();
    const filterGroup = screen.getByRole('group', { name: 'Search targets' });
    mockElementRect(filterGroup, { top: 576, bottom: 620, height: 44 });
    await advanceAndFlush(60);
    fireEvent.focus(screen.getByPlaceholderText('Search songs, players, or bands…'));
    setVisualViewport(visualViewport, 544);
    await advanceAndFlush(0);

    const body = getModalBody();
    const panel = screen.getByTestId('search-results-panel');
    expect(body.style.padding).toContain('safe-area-inset-bottom');
    expect(body.style.padding).not.toContain('88px');
    expect(body.style.padding).not.toContain('300px');
    expect(filterGroup.style.transform).toBe('translate3d(0, -88px, 0)');
    expect(panel.style.marginBottom).toBe('88px');
    expect(filterGroup.parentElement).toBe(body);
    expect(body.lastElementChild).toBe(filterGroup);
  });

  it('keeps the short-query hint centered in the keyboard-reduced result area', async () => {
    const visualViewport = installVisualViewport();
    setViewportQueries({ mobile: true });

    renderModal();
    const filterGroup = screen.getByRole('group', { name: 'Search targets' });
    mockElementRect(filterGroup, { top: 576, bottom: 620, height: 44 });
    await advanceAndFlush(60);
    fireEvent.focus(screen.getByPlaceholderText('Search songs, players, or bands…'));
    setVisualViewport(visualViewport, 544);
    await advanceAndFlush(0);

    const panel = screen.getByTestId('search-results-panel');
    const resultList = screen.getByTestId('search-result-list');
    const hint = screen.getByText('Enter at least two characters to search.');
    expect(getModalBody().style.padding).not.toContain('88px');
    expect(filterGroup.style.transform).toBe('translate3d(0, -88px, 0)');
    expect(panel.style.marginBottom).toBe('88px');
    expect(panel.style.display).toBe('flex');
    expect(panel.style.flexDirection).toBe('column');
    expect(panel.style.minHeight).toBe('0px');
    expect(resultList.style.minHeight).toBe('0px');
    expect(resultList.style.paddingTop).toBe('');
    expect(resultList.style.paddingBottom).toBe('');
    expectCenteredHint(hint);
  });

  it('uses visual viewport offsetTop when calculating the keyboard inset', async () => {
    const visualViewport = installVisualViewport();
    setViewportQueries({ mobile: true });

    renderModal();
    const filterGroup = screen.getByRole('group', { name: 'Search targets' });
    mockElementRect(filterGroup, { top: 576, bottom: 620, height: 44 });
    await advanceAndFlush(60);
    fireEvent.focus(screen.getByPlaceholderText('Search songs, players, or bands…'));
    setVisualViewport(visualViewport, 500, 44);
    await advanceAndFlush(0);

    expect(filterGroup.style.transform).toBe('translate3d(0, -88px, 0)');
    expect(screen.getByTestId('search-results-panel').style.marginBottom).toBe('88px');
  });

  it('does not reapply keyboard transform after the focused mobile search input blurs', async () => {
    const visualViewport = installVisualViewport();
    setViewportQueries({ mobile: true });

    renderModal();
    const input = screen.getByPlaceholderText('Search songs, players, or bands…');
    const filterGroup = screen.getByRole('group', { name: 'Search targets' });
    mockElementRect(filterGroup, { top: 576, bottom: 620, height: 44 });
    await advanceAndFlush(60);
    fireEvent.focus(input);
    setVisualViewport(visualViewport, 544);
    await advanceAndFlush(0);
    expect(filterGroup.style.transform).toBe('translate3d(0, -88px, 0)');

    fireEvent.blur(input);
    setVisualViewport(visualViewport, 444);

    expect(getModalBody().style.padding).toContain('safe-area-inset-bottom');
    expect(getModalBody().style.padding).not.toContain('300px');
    expect(getModalBody().style.padding).not.toContain('400px');
    expect(filterGroup.style.transform).toBe('');
    expect(screen.getByTestId('search-results-panel').style.marginBottom).toBe('0px');
  });

  it('falls back safely when visualViewport is unavailable', () => {
    removeVisualViewport();
    setViewportQueries({ mobile: true });

    renderModal();
    act(() => { window.dispatchEvent(new Event('resize')); });

    expect(getModalBody().style.padding).toContain('safe-area-inset-bottom');
    expect(getModalBody().style.padding).not.toContain('NaNpx');
  });

  it('dismisses the mobile keyboard when the Search key is pressed', () => {
    setViewportQueries({ mobile: true });
    const { props } = renderModal();
    const input = screen.getByPlaceholderText('Search songs, players, or bands…');
    input.focus();

    expect(document.activeElement).toBe(input);
    fireEvent.keyDown(input, { key: 'Enter' });

    expect(document.activeElement).not.toBe(input);
    expect(screen.getByRole('dialog', { name: 'Search' })).toBeDefined();
    expect(props.onClose).not.toHaveBeenCalled();
  });

  it('auto-focuses with preventScroll when opened on desktop', async () => {
    renderModal();
    const input = screen.getByPlaceholderText('Search songs, players, or bands…');
    const focusSpy = vi.spyOn(input, 'focus');

    await advanceAndFlush(60);

    expect(focusSpy).toHaveBeenCalledWith({ preventScroll: true });
  });

  it('auto-focuses immediately with preventScroll when opened on mobile', () => {
    setViewportQueries({ mobile: true });
    const focusSpy = vi.spyOn(HTMLInputElement.prototype, 'focus');

    renderModal();

    expect(focusSpy).toHaveBeenCalledWith({ preventScroll: true });
  });

  it('focuses with preventScroll from the modal search tap gesture', () => {
    setViewportQueries({ mobile: true });
    renderModal();
    const input = screen.getByPlaceholderText('Search songs, players, or bands…') as HTMLInputElement;
    const focusSpy = vi.spyOn(input, 'focus');

    fireEvent.pointerDown(input.parentElement as HTMLElement);

    expect(focusSpy).toHaveBeenCalledWith({ preventScroll: true });
  });

  it('keeps desktop Enter from blurring the search input', () => {
    renderModal();
    const input = screen.getByPlaceholderText('Search songs, players, or bands…');
    input.focus();

    fireEvent.keyDown(input, { key: 'Enter' });

    expect(document.activeElement).toBe(input);
  });

  it('lays out the result panel so loading spinners can center vertically', () => {
    renderModal();
    const panel = screen.getByTestId('search-results-panel');
    expect(panel.style.display).toBe('flex');
    expect(panel.style.flexDirection).toBe('column');
  });

  it('runs song, player, and band searches for the same query', async () => {
    renderModal();
    fireEvent.change(screen.getByPlaceholderText('Search songs, players, or bands…'), { target: { value: 'but' } });

    await advanceAndFlush(SEARCH_SETTLE_MS);

    await waitFor(() => {
      expect(mockApi.searchAccounts).toHaveBeenCalledWith('but', 10);
      expect(mockApi.searchBands).toHaveBeenCalledWith({ q: 'but', page: 1, pageSize: 10 });
      expect(within(screen.getByTestId('search-section-songs')).getByText('Butter Barn Hoedown')).toBeDefined();
    });

    expect(within(screen.getByTestId('search-section-players')).getByText('PlayerOne')).toBeDefined();
    expect(within(screen.getByTestId('search-section-bands')).getByText('PlayerTwo')).toBeDefined();
    expect(screen.getByTestId('search-section-songs').style.marginTop).toBe('');
    expect(screen.getByTestId('search-section-players').style.marginTop).not.toBe('');
    expect(screen.getByTestId('search-section-bands').style.marginTop).not.toBe('');

    fireEvent.click(screen.getByRole('button', { name: 'Players' }));
    expect(await screen.findByTestId('search-player-result')).toBeDefined();
    expect(screen.queryByTestId('search-section-songs')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Bands' }));
    expect(await screen.findByText('PlayerOne')).toBeDefined();
    expect(await screen.findByText('PlayerTwo')).toBeDefined();
    expect(await screen.findByText('3')).toBeDefined();
  });

  it('staggers global result section headings before their content', async () => {
    renderModal();
    fireEvent.change(screen.getByPlaceholderText('Search songs, players, or bands…'), { target: { value: 'but' } });

    await advanceAndFlush(SEARCH_SETTLE_MS);

    await waitFor(() => {
      expect(screen.getByTestId('search-section-songs')).toBeDefined();
      expect(screen.getByTestId('search-section-players')).toBeDefined();
      expect(screen.getByTestId('search-section-bands')).toBeDefined();
    });

    expectFadeInDelay(getSectionHeading('songs'), 125);
    expectFadeInDelay(findSectionAnimatedAncestor('songs', 'Butter Barn Hoedown'), 250);
    expectFadeInDelay(getSectionHeading('players'), 375);
    expectFadeInDelay(findSectionAnimatedAncestor('players', 'PlayerOne'), 500);
    expectFadeInDelay(getSectionHeading('bands'), 625);
    expectFadeInDelay(findSectionAnimatedAncestor('bands', 'PlayerTwo'), 750);
  });

  it('keeps later global section headings staggered after the visible item cap', async () => {
    mockApi.getSongs.mockResolvedValueOnce({
      count: 8,
      currentSeason: 5,
      songs: Array.from({ length: 8 }, (_, index) => ({
        songId: `but-song-${index}`,
        title: `But Song ${index + 1}`,
        artist: 'Epic Games',
      })),
    });

    renderModal();
    fireEvent.change(screen.getByPlaceholderText('Search songs, players, or bands…'), { target: { value: 'but' } });

    await advanceAndFlush(SEARCH_SETTLE_MS);

    await waitFor(() => {
      expect(screen.getByTestId('search-section-songs')).toBeDefined();
      expect(screen.getByTestId('search-section-players')).toBeDefined();
      expect(screen.getByTestId('search-section-bands')).toBeDefined();
    });

    expectFadeInDelay(getSectionHeading('players'), 1250);
    expectFadeInDelay(getSectionHeading('bands'), 1500);
  });

  it('rushes pending stagger animations when global results scroll', async () => {
    renderModal();
    fireEvent.change(screen.getByPlaceholderText('Search songs, players, or bands…'), { target: { value: 'but' } });

    await advanceAndFlush(SEARCH_SETTLE_MS);

    const bandsHeading = await waitFor(() => getSectionHeading('bands'));
    expectFadeInDelay(bandsHeading, 625);

    fireEvent.scroll(screen.getByTestId('search-results-panel'));

    expectRushedFadeIn(bandsHeading);
  });

  it('rushes pending stagger animations when filtered results scroll', async () => {
    renderModal();
    fireEvent.change(screen.getByPlaceholderText('Search songs, players, or bands…'), { target: { value: 'but' } });

    await advanceAndFlush(SEARCH_SETTLE_MS);
    fireEvent.click(screen.getByRole('button', { name: 'Players' }));

    const playerResult = await waitFor(() => findAnimatedAncestor('PlayerOne'));
    expectFadeInDelay(playerResult, 125);

    fireEvent.scroll(screen.getByTestId('search-results-panel'));

    expectRushedFadeIn(playerResult);
  });

  it('omits empty global result sections after all target searches settle', async () => {
    mockApi.searchAccounts.mockResolvedValueOnce({ results: [] });

    renderModal();
    fireEvent.change(screen.getByPlaceholderText('Search songs, players, or bands…'), { target: { value: 'but' } });

    await advanceAndFlush(SEARCH_SETTLE_MS);

    await waitFor(() => {
      expect(screen.getByTestId('search-section-songs')).toBeDefined();
      expect(screen.getByTestId('search-section-bands')).toBeDefined();
    });
    expect(screen.queryByTestId('search-section-players')).toBeNull();
    expect(screen.queryByText('No players found.')).toBeNull();
  });

  it('shows one global empty state when every target has no results', async () => {
    mockApi.searchAccounts.mockResolvedValueOnce({ results: [] });
    mockApi.searchBands.mockResolvedValueOnce({
      query: 'zzzz', normalizedQuery: 'zzzz', rankBy: 'appearance', page: 1, pageSize: 10, totalCount: 0,
      isAmbiguous: false, needsDisambiguation: false, interpretations: [], results: [],
    });

    renderModal();
    fireEvent.change(screen.getByPlaceholderText('Search songs, players, or bands…'), { target: { value: 'zzzz' } });

    await advanceAndFlush(SEARCH_SETTLE_MS);

    await waitFor(() => expect(screen.getByText('No results found.')).toBeDefined());
    expect(screen.queryByTestId('search-section-songs')).toBeNull();
    expect(screen.queryByTestId('search-section-players')).toBeNull();
    expect(screen.queryByTestId('search-section-bands')).toBeNull();
  });

  it('keeps the global empty state centered while mobile keyboard transform is active', async () => {
    const visualViewport = installVisualViewport();
    setViewportQueries({ mobile: true });
    mockApi.searchAccounts.mockResolvedValueOnce({ results: [] });
    mockApi.searchBands.mockResolvedValueOnce({
      query: 'zzzz', normalizedQuery: 'zzzz', rankBy: 'appearance', page: 1, pageSize: 10, totalCount: 0,
      isAmbiguous: false, needsDisambiguation: false, interpretations: [], results: [],
    });

    renderModal();
    const filterGroup = screen.getByRole('group', { name: 'Search targets' });
    mockElementRect(filterGroup, { top: 576, bottom: 620, height: 44 });
    await advanceAndFlush(60);
    fireEvent.focus(screen.getByPlaceholderText('Search songs, players, or bands…'));
    setVisualViewport(visualViewport, 544);
    await advanceAndFlush(0);
    fireEvent.change(screen.getByPlaceholderText('Search songs, players, or bands…'), { target: { value: 'zzzz' } });

    await advanceAndFlush(SEARCH_SETTLE_MS);

    const emptyState = await screen.findByText('No results found.');
    const resultList = screen.getByTestId('search-result-list');
    expect(getModalBody().style.padding).not.toContain('88px');
    expect(filterGroup.style.transform).toBe('translate3d(0, -88px, 0)');
    expect(screen.getByTestId('search-results-panel').style.marginBottom).toBe('88px');
    expect(resultList.style.minHeight).toBe('0px');
    expect(resultList.style.paddingTop).toBe('');
    expect(resultList.style.paddingBottom).toBe('');
    expectCenteredHint(emptyState);
  });

  it('matches songs the same way as the Songs page search', async () => {
    renderModal();
    fireEvent.change(screen.getByPlaceholderText('Search songs, players, or bands…'), { target: { value: "Don't Fear" } });

    await advanceAndFlush(SEARCH_SETTLE_MS);

    const songsSection = await screen.findByTestId('search-section-songs');
    expect(within(songsSection).getByText("(Don't Fear) The Reaper")).toBeDefined();
    expect(screen.queryByText('FC')).toBeNull();
  });

  it('pads the result list so scroll fades do not cut into edge cards', async () => {
    renderModal();
    fireEvent.change(screen.getByPlaceholderText('Search songs, players, or bands…'), { target: { value: 'but' } });

    await advanceAndFlush(SEARCH_SETTLE_MS);
    await waitFor(() => expect(screen.getByText('Butter Barn Hoedown')).toBeDefined());

    expectResultListEdgePadding(findAnimatedAncestor('Butter Barn Hoedown'));
  });

  it('uses the modal results panel as the scroll fade root for global results', async () => {
    const originalIntersectionObserver = globalThis.IntersectionObserver;
    const observerInstances: Array<{ options?: IntersectionObserverInit; observed: Element[] }> = [];

    class MockIntersectionObserver {
      readonly root: Element | Document | null;
      readonly rootMargin: string;
      readonly thresholds: readonly number[];
      private readonly instance: { options?: IntersectionObserverInit; observed: Element[] };

      constructor(_callback: IntersectionObserverCallback, options?: IntersectionObserverInit) {
        this.root = options?.root ?? null;
        this.rootMargin = options?.rootMargin ?? '';
        this.thresholds = Array.isArray(options?.threshold) ? options.threshold : [options?.threshold ?? 0];
        this.instance = { options, observed: [] };
        observerInstances.push(this.instance);
      }

      observe(target: Element) { this.instance.observed.push(target); }
      unobserve() {}
      disconnect() {}
      takeRecords(): IntersectionObserverEntry[] { return []; }
    }

    globalThis.IntersectionObserver = MockIntersectionObserver as unknown as typeof globalThis.IntersectionObserver;

    try {
      renderModal();
      fireEvent.change(screen.getByPlaceholderText('Search songs, players, or bands…'), { target: { value: 'but' } });

      await advanceAndFlush(SEARCH_SETTLE_MS);
      await waitFor(() => expect(screen.getByTestId('search-section-songs')).toBeDefined());

      const panel = screen.getByTestId('search-results-panel');
      expect(observerInstances.some(instance =>
        instance.options?.root === panel &&
        instance.observed.some(element => (element as HTMLElement).dataset.testid === 'search-section-songs'),
      )).toBe(true);
    } finally {
      globalThis.IntersectionObserver = originalIntersectionObserver;
    }
  });

  it('keeps player and band lists padded through the bottom scroll fade', async () => {
    renderModal();
    fireEvent.change(screen.getByPlaceholderText('Search songs, players, or bands…'), { target: { value: 'but' } });

    await advanceAndFlush(SEARCH_SETTLE_MS);

    fireEvent.click(screen.getByRole('button', { name: 'Players' }));
    await waitFor(() => expect(findAnimatedAncestor('PlayerOne')).not.toBeNull());
    const playerResult = findAnimatedAncestor('PlayerOne');
    expect(playerResult?.style.flexShrink).toBe('0');
    expectResultListEdgePadding(playerResult);

    fireEvent.click(screen.getByRole('button', { name: 'Bands' }));
    await waitFor(() => expect(findAnimatedAncestor('PlayerTwo')).not.toBeNull());
    const bandResult = findAnimatedAncestor('PlayerTwo');
    expect(bandResult?.style.flexShrink).toBe('0');
    expectResultListEdgePadding(bandResult);
  });

  it('staggers each target once per query when the target is viewed', async () => {
    renderModal();
    const input = screen.getByPlaceholderText('Search songs, players, or bands…');
    fireEvent.change(input, { target: { value: 'but' } });

    await advanceAndFlush(SEARCH_SETTLE_MS);

    await waitFor(() => expect(findAnimatedAncestor('Butter Barn Hoedown')).not.toBeNull());

    fireEvent.click(screen.getByRole('button', { name: 'Players' }));
    await waitFor(() => expect(findAnimatedAncestor('PlayerOne')).not.toBeNull());

    fireEvent.click(screen.getByRole('button', { name: 'Players' }));
    await waitFor(() => expect(screen.getByText('Butter Barn Hoedown')).toBeDefined());
    expect(findAnimatedAncestor('Butter Barn Hoedown')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Players' }));
    await waitFor(() => expect(screen.getByText('PlayerOne')).toBeDefined());
    expect(findAnimatedAncestor('PlayerOne')).toBeNull();

    fireEvent.change(input, { target: { value: 'fear' } });
    await advanceAndFlush(SEARCH_SETTLE_MS);

    await waitFor(() => expect(findAnimatedAncestor('PlayerOne')).not.toBeNull());
  });

  it('shows player results while band search is still pending', async () => {
    const pendingBands = createDeferred<unknown>();
    mockApi.searchBands.mockReturnValue(pendingBands.promise);

    renderModal();
    fireEvent.change(screen.getByPlaceholderText('Search songs, players, or bands…'), { target: { value: 'pla' } });

    await advanceAndFlush(SEARCH_SETTLE_MS);

    expect(screen.queryByTestId('search-section-songs')).toBeNull();
    expect(screen.queryByText('Butter Barn Hoedown')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Players' }));
    await advanceAndFlush(SEARCH_SETTLE_MS);

    await waitFor(() => {
      expect(screen.getByText('PlayerOne')).toBeDefined();
    });

    await act(async () => {
      pendingBands.resolve({
        query: 'pla', normalizedQuery: 'pla', rankBy: 'appearance', page: 1, pageSize: 10, totalCount: 0,
        isAmbiguous: false, needsDisambiguation: false, interpretations: [], results: [],
      });
      await Promise.resolve();
    });
  });

  it('navigates and closes when a player result is selected', async () => {
    const { props } = renderModal();
    fireEvent.change(screen.getByPlaceholderText('Search songs, players, or bands…'), { target: { value: 'pla' } });

    await advanceAndFlush(SEARCH_SETTLE_MS);
    fireEvent.click(screen.getByRole('button', { name: 'Players' }));
    fireEvent.click(await screen.findByTestId('search-player-result'));

    expect(props.onClose).toHaveBeenCalled();
    expect(mockNavigate).toHaveBeenCalledWith('/player/p1');
  });

  it('navigates selected player results to statistics', async () => {
    localStorage.setItem(SELECTED_PROFILE_STORAGE_KEY, JSON.stringify({ type: 'player', accountId: 'p1', displayName: 'PlayerOne' }));
    localStorage.setItem(LEGACY_TRACKED_PLAYER_STORAGE_KEY, JSON.stringify({ accountId: 'p1', displayName: 'PlayerOne' }));
    const { props } = renderModal();
    fireEvent.change(screen.getByPlaceholderText('Search songs, players, or bands…'), { target: { value: 'pla' } });

    await advanceAndFlush(SEARCH_SETTLE_MS);
    fireEvent.click(screen.getByRole('button', { name: 'Players' }));
    fireEvent.click(await screen.findByTestId('search-player-result'));

    expect(props.onClose).toHaveBeenCalled();
    expect(mockNavigate).toHaveBeenCalledWith('/statistics');
  });

  it('navigates non-selected band results to the clean band route', async () => {
    const { props } = renderModal();
    fireEvent.change(screen.getByPlaceholderText('Search songs, players, or bands…'), { target: { value: 'pla' } });

    await advanceAndFlush(SEARCH_SETTLE_MS);
    fireEvent.click(screen.getByRole('button', { name: 'Bands' }));
    await waitFor(() => expect(screen.getByText('PlayerTwo')).toBeDefined());
    fireEvent.click(screen.getByText('PlayerTwo'));

    expect(props.onClose).toHaveBeenCalled();
    expect(mockNavigate).toHaveBeenCalledWith('/bands/band-1?bandType=Band_Duets&teamKey=p1%2Cp2&names=PlayerOne%2C%20PlayerTwo');
  });

  it('navigates selected band results to statistics', async () => {
    localStorage.setItem(SELECTED_PROFILE_STORAGE_KEY, JSON.stringify({
      type: 'band',
      bandId: 'band-1',
      bandType: 'Band_Duets',
      teamKey: 'p1,p2',
      displayName: 'PlayerOne + PlayerTwo',
      members: [
        { accountId: 'p1', displayName: 'PlayerOne' },
        { accountId: 'p2', displayName: 'PlayerTwo' },
      ],
    }));
    const { props } = renderModal();
    fireEvent.change(screen.getByPlaceholderText('Search songs, players, or bands…'), { target: { value: 'pla' } });

    await advanceAndFlush(SEARCH_SETTLE_MS);
    fireEvent.click(screen.getByRole('button', { name: 'Bands' }));
    await waitFor(() => expect(screen.getByText('PlayerTwo')).toBeDefined());
    fireEvent.click(screen.getByText('PlayerTwo'));

    expect(props.onClose).toHaveBeenCalled();
    expect(mockNavigate).toHaveBeenCalledWith('/statistics');
  });
});