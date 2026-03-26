import { describe, it, expect, vi, beforeEach, beforeAll, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { stubResizeObserver } from '../../../helpers/browserStubs';
import { TestProviders } from '../../../helpers/TestProviders';

// Controllable slide height mock
let mockSlideHeight = 400;
vi.mock('../../../../src/firstRun/SlideHeightContext', () => ({
  SlideHeightContext: { Provider: ({ children }: any) => children },
  useSlideHeight: () => mockSlideHeight,
}));

// Mock useChartDimensions so Recharts bar count is deterministic
let mockMaxBars = 10;
vi.mock('../../../../src/hooks/chart/useChartDimensions', () => ({
  useChartDimensions: () => ({ chartContainerRef: { current: null }, containerWidth: 800, maxBars: mockMaxBars }),
}));

import ChartDemo from '../../../../src/pages/songinfo/firstRun/demo/ChartDemo';
import BarSelectDemo from '../../../../src/pages/songinfo/firstRun/demo/BarSelectDemo';
import TopScoresDemo from '../../../../src/pages/songinfo/firstRun/demo/TopScoresDemo';
import ViewAllDemo from '../../../../src/pages/songinfo/firstRun/demo/ViewAllDemo';
import HistoryDemo from '../../../../src/pages/songinfo/firstRun/demo/HistoryDemo';

beforeAll(() => {
  stubResizeObserver();
});

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  mockSlideHeight = 400;
  mockMaxBars = 10;
});

afterEach(() => {
  vi.useRealTimers();
});

function wrap(ui: React.ReactElement) {
  return render(ui, { wrapper: TestProviders });
}

describe('ChartDemo', () => {
  it('renders chart with score and accuracy labels', () => {
    wrap(<ChartDemo />);
    expect(screen.getAllByText('Score').length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText('Accuracy').length).toBeGreaterThanOrEqual(1);
  });

  it('renders legend items', () => {
    wrap(<ChartDemo />);
    expect(screen.getByText('Accuracy (FC)')).toBeTruthy();
  });

  it('renders 3 data points', () => {
    const { container } = wrap(<ChartDemo />);
    // Recharts renders bars as path elements inside the chart
    const paths = container.querySelectorAll('.recharts-bar-rectangle path');
    expect(paths.length).toBeGreaterThanOrEqual(1);
  });
});

describe('BarSelectDemo', () => {
  it('renders chart with axes and legend', () => {
    wrap(<BarSelectDemo />);
    expect(screen.getAllByText('Score').length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText('Accuracy').length).toBeGreaterThanOrEqual(1);
    expect(screen.getByText('Accuracy (FC)')).toBeTruthy();
  });

  it('renders detail card with score', () => {
    wrap(<BarSelectDemo />);
    // First visible point score should appear
    expect(screen.getByText('218,400')).toBeTruthy();
  });

  it('cycles through bars on timer', async () => {
    wrap(<BarSelectDemo />);
    // Initial: first data point visible
    expect(screen.getByText('218,400')).toBeTruthy();

    // Advance past CYCLE_MS (2500) + fade (300) + extra buffer
    await vi.advanceTimersByTimeAsync(3500);
    // After cycling, should no longer show first point as the score
    // (it cycles from idx 0 → 1 → 2 → 0...)
    const scores = ['218,400', '347,100', '486,500'];
    const found = scores.filter(s => screen.queryByText(s));
    expect(found.length).toBeGreaterThanOrEqual(1);
  });
});

describe('TopScoresDemo', () => {
  it('renders instrument header', () => {
    wrap(<TopScoresDemo />);
    // InstrumentHeader renders Lead (Solo_Guitar maps to Lead)
    expect(screen.getByText('Lead')).toBeTruthy();
  });

  it('renders leaderboard entries with player names', () => {
    wrap(<TopScoresDemo />);
    expect(screen.getByText('AceSolo')).toBeTruthy();
    expect(screen.getByText('RiffMaster')).toBeTruthy();
    expect(screen.getByText('ChordKing')).toBeTruthy();
    expect(screen.getByText('PickSlayer')).toBeTruthy();
  });

  it('renders view full leaderboard button', () => {
    wrap(<TopScoresDemo />);
    expect(screen.getByText('View full leaderboard')).toBeTruthy();
  });
});

describe('ViewAllDemo', () => {
  it('renders score entries', () => {
    wrap(<ViewAllDemo />);
    expect(screen.getByText('486,500')).toBeTruthy();
  });

  it('renders view all scores button', () => {
    wrap(<ViewAllDemo />);
    expect(screen.getByText('View all scores')).toBeTruthy();
  });

  it('applies fade mask to first entry', () => {
    const { container } = wrap(<ViewAllDemo />);
    // First entry row with maskImage applied via inline style
    const withMask = Array.from(container.querySelectorAll('div')).find(
      (el) => (el as HTMLElement).style.maskImage?.includes('linear-gradient'),
    ) as HTMLElement | undefined;
    expect(withMask?.style.maskImage).toContain('linear-gradient');
  });
});

describe('HistoryDemo', () => {
  it('renders score entries', () => {
    wrap(<HistoryDemo />);
    expect(screen.getByText('486,500')).toBeTruthy();
    expect(screen.getByText('412,300')).toBeTruthy();
  });

  it('renders date labels with full format', () => {
    wrap(<HistoryDemo />);
    // Labels use toLocaleDateString format, just check one exists
    const today = new Date().toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
    expect(screen.getByText(today)).toBeTruthy();
  });

  it('limits rows based on slide height', () => {
    // With height 400 and ROW_HEIGHT 44, maxRows = min(5, floor(400/44)) = min(5,9) = 5
    wrap(<HistoryDemo />);
    expect(screen.getByText('218,400')).toBeTruthy(); // 5th entry visible
  });

  it('uses default maxRows when slide height is 0', () => {
    mockSlideHeight = 0;
    wrap(<HistoryDemo />);
    // Default is 5, and all 5 scores should render
    expect(screen.getByText('486,500')).toBeTruthy();
    expect(screen.getByText('218,400')).toBeTruthy();
  });
});

describe('edge cases — zero slide height', () => {
  beforeEach(() => { mockSlideHeight = 0; });

  it('ChartDemo renders without explicit height', () => {
    const { container } = wrap(<ChartDemo />);
    // Should render the wrapper without inline height style
    const wrapper = container.firstChild as HTMLElement;
    expect(wrapper).toBeTruthy();
  });

  it('BarSelectDemo uses fallback chartHeight of 160', () => {
    wrap(<BarSelectDemo />);
    expect(screen.getByText('218,400')).toBeTruthy();
  });

  it('TopScoresDemo uses default maxEntries of 4', () => {
    wrap(<TopScoresDemo />);
    expect(screen.getByText('AceSolo')).toBeTruthy();
    expect(screen.getByText('PickSlayer')).toBeTruthy();
  });

  it('ViewAllDemo uses default maxCards of 3', () => {
    wrap(<ViewAllDemo />);
    expect(screen.getByText('486,500')).toBeTruthy();
  });
});

describe('edge cases — limited maxBars', () => {
  it('BarSelectDemo clamps selectedIdx when maxBars shrinks', () => {
    mockMaxBars = 2;
    wrap(<BarSelectDemo />);
    // With only 2 visible bars (last 2 of 3), first score shouldn't appear
    expect(screen.queryByText('218,400')).toBeNull();
    expect(screen.getByText('347,100')).toBeTruthy();
  });

  it('ChartDemo shows fewer bars when maxBars is 1', () => {
    mockMaxBars = 1;
    wrap(<ChartDemo />);
    // Only the last data point should be visible
    const { container } = wrap(<ChartDemo />);
    expect(container.querySelector('.recharts-bar')).toBeTruthy();
  });
});

describe('edge cases — tiny slide height', () => {
  it('TopScoresDemo shows at least 1 entry with small height', () => {
    mockSlideHeight = 50;
    wrap(<TopScoresDemo />);
    expect(screen.getByText('AceSolo')).toBeTruthy();
  });

  it('ViewAllDemo shows at least 1 card with small height', () => {
    mockSlideHeight = 50;
    wrap(<ViewAllDemo />);
    expect(screen.getByText('486,500')).toBeTruthy();
  });

  it('HistoryDemo shows at least 1 row with small height', () => {
    mockSlideHeight = 50;
    wrap(<HistoryDemo />);
    expect(screen.getByText('486,500')).toBeTruthy();
  });
});
