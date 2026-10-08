/**
 * Accessibility coverage for the Suggestions fresh-mix flow (#328 / #472):
 * the mix-limit control's name, role and description, its reading/focus
 * order after the list, keyboard activation, and where focus and scroll land
 * once the fresh mix replaces the list.
 */
import { describe, it, expect, vi, beforeAll, beforeEach } from 'vitest';
import { render, screen, waitFor, fireEvent, act } from '@testing-library/react';
import { Routes, Route } from 'react-router-dom';
import type { SuggestionCategory } from '@festival/core/types';
import SuggestionsPage from '../../../src/pages/suggestions/SuggestionsPage';
import { RouteMain } from '../../../src/components/shell/RouteAccessibility';
import { TestProviders } from '../../helpers/TestProviders';
import { stubResizeObserver, stubIntersectionObserver } from '../../helpers/browserStubs';
import { contentHash } from '../../../src/firstRun/types';
import { suggestionsSlides } from '../../../src/pages/suggestions/firstRun';

const mixState = vi.hoisted(() => ({
  canStart: true,
  startCalls: 0,
}));

const mockApi = vi.hoisted(() => {
  const fn = vi.fn;
  return {
    getSongs: fn().mockResolvedValue({ songs: [
      { songId: 's1', title: 'Alpha Song', artist: 'Artist A', year: 2024, difficulty: { guitar: 3, bass: 2, drums: 4, vocals: 1 } },
    ], count: 1, currentSeason: 5 }),
    getPlayer: fn().mockResolvedValue({
      accountId: 'test-player-1', displayName: 'TestPlayer', totalScores: 1,
      scores: [
        { songId: 's1', instrument: 'Solo_Guitar', score: 100000, rank: 5, percentile: 80, accuracy: 90, stars: 4, season: 5, isFullCombo: false },
      ],
    }),
    getSyncStatus: fn().mockResolvedValue({ accountId: 'test-player-1', isTracked: true, backfill: null, historyRecon: null }),
    getVersion: fn().mockResolvedValue({ version: '1.0.0' }),
    getRivalsAll: fn().mockResolvedValue({ accountId: 'test-player-1', songs: [], combos: [] }),
    getRivalSuggestions: fn().mockResolvedValue({ accountId: 'test-player-1', combo: '', computedAt: null, rivals: [] }),
    getShop: fn().mockResolvedValue({ songs: [] }),
    getVersions: fn().mockResolvedValue({ songs: '1' }),
    getShopSnapshot: fn().mockResolvedValue({ songIds: [] }),
    getBandSongRows: fn().mockResolvedValue({ bandType: 'Band_Duets', teamKey: '', comboId: null, count: 0, entries: [] }),
  };
});

vi.mock('../../../src/api/client', () => ({ api: mockApi }));

vi.mock('../../../src/hooks/data/useSuggestions', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../src/hooks/data/useSuggestions')>();
  const { useCallback, useMemo, useState } = await import('react');
  return {
    ...actual,
    useSuggestions: () => {
      const [mix, setMix] = useState({ key: 'solo:test-player-1:mix:1', size: actual.SUGGESTIONS_CATEGORY_LIMIT });
      const categories = useMemo(() => buildCategories(mix.size), [mix.size]);
      const startNewMix = useCallback(() => {
        mixState.startCalls += 1;
        if (!mixState.canStart) return false;
        setMix({ key: 'solo:test-player-1:mix:2', size: 10 });
        return true;
      }, []);
      return {
        categories,
        mixKey: mix.key,
        loadMore: () => {},
        hasMore: false,
        limitReached: categories.length >= actual.SUGGESTIONS_CATEGORY_LIMIT,
        loadTriggerCount: 0,
        startNewMix,
        resetScrollPosition: () => {},
      };
    },
  };
});

function buildCategories(count: number): SuggestionCategory[] {
  return Array.from({ length: count }, (_, index) => ({
    key: `near_fc_guitar_${index}`,
    title: `Category ${index + 1}`,
    description: 'Push these songs to a full combo.',
    songs: [{ songId: 's1', title: 'Alpha Song', artist: 'Artist A', instrumentKey: 'guitar' }],
  }));
}

const TABBABLE = 'a[href], button:not([disabled]), input:not([disabled]), select, textarea, [tabindex]:not([tabindex="-1"])';

beforeAll(() => {
  stubResizeObserver();
  stubIntersectionObserver();
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    configurable: true,
    value: vi.fn().mockImplementation((query: string) => ({
      matches: false, media: query, onchange: null,
      addEventListener: vi.fn(), removeEventListener: vi.fn(),
      addListener: vi.fn(), removeListener: vi.fn(), dispatchEvent: vi.fn(),
    })),
  });
});

beforeEach(() => {
  mixState.canStart = true;
  mixState.startCalls = 0;
  localStorage.clear();
  localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'test-player-1', displayName: 'TestPlayer' }));
  localStorage.setItem('fst:firstRun', JSON.stringify(Object.fromEntries(
    suggestionsSlides.map(slide => [
      slide.id,
      {
        version: slide.version,
        hash: contentHash(slide.contentKey ?? (slide.title + slide.description)),
        seenAt: new Date().toISOString(),
      },
    ]),
  )));
});

async function renderAtMixLimit() {
  const view = render(
    <TestProviders route="/suggestions" accountId="test-player-1">
      <RouteMain routeTitle="Suggestions" fallbackHeading={false}>
        <Routes>
          <Route path="/suggestions" element={<SuggestionsPage accountId="test-player-1" />} />
        </Routes>
      </RouteMain>
    </TestProviders>,
  );
  const scrollContainer = screen.getByTestId('test-scroll-container');
  scrollContainer.scrollTo = ((_x: number, y: number) => {
    scrollContainer.scrollTop = y;
  }) as typeof scrollContainer.scrollTo;
  const button = await screen.findByRole('button', { name: 'Start a new mix' }, { timeout: 5_000 });
  await screen.findAllByTestId('suggestion-category-card', undefined, { timeout: 5_000 });
  return { ...view, button, scrollContainer };
}

describe('Suggestions fresh mix accessibility', () => {
  it('exposes the fresh-mix control as a named, described button after the list', async () => {
    const { button } = await renderAtMixLimit();

    expect(button.tagName).toBe('BUTTON');
    expect(button).toHaveAttribute('type', 'button');
    expect(button).toBeEnabled();
    expect(button).toHaveAccessibleName('Start a new mix');
    expect(button).toHaveAccessibleDescription("You've reached 1,000 suggestions in this mix.");

    const message = screen.getByText("You've reached 1,000 suggestions in this mix.");
    const list = screen.getByTestId('suggestions-list');
    expect(list.compareDocumentPosition(message) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(message.compareDocumentPosition(button) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('places the fresh-mix control after every suggestion link in tab order', async () => {
    const { button } = await renderAtMixLimit();
    const main = screen.getByRole('main', { name: 'Suggestions' });
    const tabbables = Array.from(main.querySelectorAll<HTMLElement>(TABBABLE));
    const listLinks = Array.from(screen.getByTestId('suggestions-list').querySelectorAll<HTMLElement>(TABBABLE));

    expect(listLinks.length).toBeGreaterThan(0);
    expect(tabbables.filter(element => element.tabIndex > 0)).toEqual([]);
    const buttonIndex = tabbables.indexOf(button);
    expect(buttonIndex).toBeGreaterThan(-1);
    for (const link of listLinks) {
      expect(tabbables.indexOf(link)).toBeLessThan(buttonIndex);
    }
  });

  it('moves keyboard focus to the Suggestions main region and keeps the fresh mix at the top', async () => {
    const { button, scrollContainer } = await renderAtMixLimit();
    const list = screen.getByTestId('suggestions-list');
    const originalMixKey = list.getAttribute('data-suggestions-cache-key');
    scrollContainer.scrollTop = 48_000;

    button.focus();
    expect(button).toHaveFocus();
    // A keyboard (Enter/Space) activation of a native button dispatches a click with detail 0.
    act(() => {
      fireEvent.click(button, { detail: 0 });
    });

    expect(mixState.startCalls).toBe(1);
    await waitFor(() => {
      expect(screen.queryByRole('button', { name: 'Start a new mix' })).toBeNull();
    });
    expect(list.getAttribute('data-suggestions-cache-key')).not.toBe(originalMixKey);
    expect(screen.getByRole('main', { name: 'Suggestions' })).toHaveFocus();
    expect(document.activeElement).not.toBe(document.body);
    expect(scrollContainer.scrollTop).toBe(0);
  });

  it('leaves focus on the control when a fresh mix cannot start', async () => {
    mixState.canStart = false;
    const { button } = await renderAtMixLimit();

    button.focus();
    act(() => {
      fireEvent.click(button, { detail: 0 });
    });

    expect(mixState.startCalls).toBe(1);
    expect(screen.getByRole('button', { name: 'Start a new mix' })).toBe(button);
    expect(button).toHaveFocus();
  });
});
