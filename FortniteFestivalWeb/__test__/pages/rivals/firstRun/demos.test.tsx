import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, render, screen } from '@testing-library/react';
import { TestProviders } from '../../../helpers/TestProviders';
import { DEMO_SWAP_INTERVAL_MS, FADE_DURATION } from '@festival/theme';

let mockSlideHeight: number | undefined = 400;
let mockContainerWidth = 700;
const mockDetailSongs = [
  { songId: 'song-1', title: 'Run It', artist: 'Epic Games', year: 2024, albumArt: 'art-1.jpg' },
  { songId: 'song-2', title: 'Beyond the Flame', artist: 'Epic Games', year: 2024, albumArt: 'art-2.jpg' },
  { songId: 'song-3', title: 'Bloom', artist: 'Epic Games', year: 2024, albumArt: 'art-3.jpg' },
  { songId: 'song-4', title: 'Best Buds', artist: 'Epic Games', year: 2024, albumArt: 'art-4.jpg' },
];

vi.mock('../../../../src/firstRun/SlideHeightContext', () => ({
  SlideHeightContext: { Provider: ({ children }: any) => children },
  useSlideHeight: () => mockSlideHeight,
}));

vi.mock('../../../../src/hooks/ui/useContainerWidth', () => ({
  useContainerWidth: () => mockContainerWidth,
}));

vi.mock('../../../../src/hooks/data/useDemoSongs', () => ({
  useDemoSongs: () => ({
    rows: mockDetailSongs,
    fadingIdx: new Set<number>(),
    initialDone: true,
    pool: mockDetailSongs,
  }),
}));

import RivalsOverviewDemo from '../../../../src/pages/rivals/firstRun/demo/RivalsOverviewDemo';
import RivalsInstrumentsDemo from '../../../../src/pages/rivals/firstRun/demo/RivalsInstrumentsDemo';
import RivalsDetailDemo from '../../../../src/pages/rivals/firstRun/demo/RivalsDetailDemo';

function wrap(ui: React.ReactElement) {
  return render(ui, { wrapper: TestProviders });
}

describe('RivalsOverviewDemo', () => {
  beforeEach(() => {
    mockSlideHeight = 400;
    mockContainerWidth = 700;
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('uses the shared container-query list wrapper for both sections on roomier heights', () => {
    wrap(<RivalsOverviewDemo />);

    expect(screen.getByTestId('rivals-fre-above-list').style.containerType).toBe('inline-size');
    expect(screen.getByTestId('rivals-fre-below-list').style.containerType).toBe('inline-size');
  });

  it('renders shared RivalRow content in both above and below sections on roomier heights', () => {
    const { container } = wrap(<RivalsOverviewDemo />);

    expect(screen.getByText('Above You')).toBeTruthy();
    expect(screen.getByText('Below You')).toBeTruthy();
    expect(container.querySelectorAll('[role="button"]').length).toBeGreaterThanOrEqual(2);
    expect(container.querySelector('.rivalRowContent, [class*="rivalRowContent"]')).toBeTruthy();
  });

  it('switches to a single-card compact mode on tight heights and alternates directions over time', () => {
    mockSlideHeight = 320;
    mockContainerWidth = 320;
    wrap(<RivalsOverviewDemo />);

    expect(screen.queryByText('Above You')).toBeNull();
    expect(screen.queryByText('Below You')).toBeNull();
    expect(screen.queryByTestId('rivals-fre-above-list')).toBeNull();
    expect(screen.queryByTestId('rivals-fre-below-list')).toBeNull();

    const compactList = screen.getByTestId('rivals-fre-compact-list');
    const initialText = screen.getByRole('button').textContent;

    expect(compactList.style.containerType).toBe('inline-size');
    expect(compactList.getAttribute('data-compact-direction')).toBe('above');
    expect(screen.getAllByRole('button')).toHaveLength(1);

    act(() => {
      vi.advanceTimersByTime(DEMO_SWAP_INTERVAL_MS + FADE_DURATION + 50);
    });

    expect(screen.getByTestId('rivals-fre-compact-list').getAttribute('data-compact-direction')).toBe('below');
    expect(screen.getByRole('button').textContent).not.toBe(initialText);
    expect(screen.getAllByRole('button')).toHaveLength(1);
  });

  it('re-engages the two-section layout when a wider row estimate makes both sections fit again', () => {
    mockSlideHeight = 320;
    mockContainerWidth = 320;

    const { rerender } = wrap(<RivalsOverviewDemo />);
    expect(screen.getByTestId('rivals-fre-compact-list')).toBeTruthy();
    expect(screen.queryByText('Above You')).toBeNull();

    mockContainerWidth = 700;
    rerender(<RivalsOverviewDemo />);

    expect(screen.queryByTestId('rivals-fre-compact-list')).toBeNull();
    expect(screen.getByText('Above You')).toBeTruthy();
    expect(screen.getByText('Below You')).toBeTruthy();
    expect(screen.getByTestId('rivals-fre-above-list')).toBeTruthy();
    expect(screen.getByTestId('rivals-fre-below-list')).toBeTruthy();
  });
});

describe('RivalsInstrumentsDemo', () => {
  beforeEach(() => {
    mockSlideHeight = 400;
    mockContainerWidth = 700;
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('keeps multiple cards per instrument on roomier layouts', () => {
    wrap(<RivalsInstrumentsDemo />);

    expect(screen.getByTestId('rivals-fre-instruments-wrapper').getAttribute('data-card-mode')).toBe('double');
    expect(screen.getByTestId('rivals-fre-instrument-section-Solo_Guitar').getAttribute('data-visible-cards')).toBe('2');
    expect(screen.getAllByRole('button').length).toBeGreaterThanOrEqual(2);
  });

  it('falls back to a single visible rival card overall on tight layouts', () => {
    mockSlideHeight = 320;
    mockContainerWidth = 320;
    wrap(<RivalsInstrumentsDemo />);

    expect(screen.getByTestId('rivals-fre-instruments-wrapper').getAttribute('data-card-mode')).toBe('single');
    expect(screen.getByTestId('rivals-fre-instrument-section-Solo_Guitar').getAttribute('data-visible-cards')).toBe('1');
    expect(screen.queryByTestId('rivals-fre-instrument-section-Solo_Drums')).toBeNull();
    expect(screen.getAllByRole('button')).toHaveLength(1);
  });

  it('alternates the single visible card direction over time in compact mode', () => {
    mockSlideHeight = 320;
    mockContainerWidth = 320;
    wrap(<RivalsInstrumentsDemo />);

    const section = screen.getByTestId('rivals-fre-instrument-section-Solo_Guitar');
    const initialText = screen.getByRole('button').textContent;

    expect(section.getAttribute('data-visible-direction')).toBe('above');

    act(() => {
      vi.advanceTimersByTime(DEMO_SWAP_INTERVAL_MS + FADE_DURATION + 50);
    });

    expect(section.getAttribute('data-visible-direction')).toBe('below');
    expect(screen.getByRole('button').textContent).not.toBe(initialText);
    expect(screen.getAllByRole('button')).toHaveLength(1);
  });
});

describe('RivalsDetailDemo', () => {
  beforeEach(() => {
    mockSlideHeight = 400;
    mockContainerWidth = 700;
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('keeps multiple visible head-to-head rows on roomier layouts', () => {
    wrap(<RivalsDetailDemo />);

    expect(screen.getByTestId('rivals-fre-detail-wrapper').getAttribute('data-row-mode')).toBe('multi');
    expect(Number(screen.getByTestId('rivals-fre-detail-list').getAttribute('data-visible-rows'))).toBeGreaterThanOrEqual(2);
    expect(screen.getAllByRole('button').length).toBeGreaterThanOrEqual(2);
  });

  it('falls back to a single visible head-to-head row on tight layouts', () => {
    mockSlideHeight = 320;
    mockContainerWidth = 320;
    wrap(<RivalsDetailDemo />);

    expect(screen.getByTestId('rivals-fre-detail-wrapper').getAttribute('data-row-mode')).toBe('single');
    expect(screen.getByTestId('rivals-fre-detail-list').getAttribute('data-visible-rows')).toBe('1');
    expect(screen.getAllByRole('button')).toHaveLength(1);
  });
});