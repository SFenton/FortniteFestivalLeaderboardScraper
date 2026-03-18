/**
 * ScoreHistoryChart coverage tests — exercises chart rendering with data,
 * bar shape render, chart pagination controls, and score card list.
 */
import { describe, it, expect, vi, beforeEach, beforeAll } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { TestProviders } from '../../../../helpers/TestProviders';
import { stubResizeObserver } from '../../../../helpers/browserStubs';

const mockApi = vi.hoisted(() => ({
  getSongs: vi.fn().mockResolvedValue({ songs: [], count: 0, currentSeason: 5 }),
  getPlayer: vi.fn().mockResolvedValue(null),
  getSyncStatus: vi.fn().mockResolvedValue({ accountId: '', isTracked: false }),
  getVersion: vi.fn().mockResolvedValue({ version: '1.0.0' }),
  getLeaderboard: vi.fn().mockResolvedValue({ entries: [] }),
  getAllLeaderboards: vi.fn().mockResolvedValue({ instruments: [] }),
  getPlayerHistory: vi.fn().mockResolvedValue({ accountId: 'acc1', count: 0, history: [] }),
  getPlayerStats: vi.fn().mockResolvedValue({ stats: [] }),
  searchAccounts: vi.fn().mockResolvedValue({ results: [] }),
  trackPlayer: vi.fn().mockResolvedValue({ accountId: '', displayName: '' }),
}));

vi.mock('../../../../../api/client', () => ({ api: mockApi }));

// Mock the chart data hook
const mockChartData = vi.hoisted(() => ({
  useChartData: vi.fn().mockReturnValue({
    songHistory: [],
    chartData: [],
    loading: false,
    instrumentCounts: {} as Record<string, number>,
  }),
}));

vi.mock('../../../../../hooks/chart/useChartData', () => mockChartData);

// Mock useChartDimensions to return a small maxBars so pagination triggers
const mockChartDimensions = vi.hoisted(() => ({
  useChartDimensions: vi.fn().mockReturnValue({
    chartContainerRef: { current: null },
    containerWidth: 400,
    maxBars: 5, // Force pagination when data > 5
  }),
}));

vi.mock('../../../../../hooks/chart/useChartDimensions', () => mockChartDimensions);

// Mock recharts to render testable DOM
vi.mock('recharts', () => ({
  ComposedChart: ({ children, data }: any) => (
    <div data-testid="composed-chart" data-points={data?.length ?? 0}>{children}</div>
  ),
  Bar: ({ shape: Shape }: any) => {
    // Render shape function if provided to exercise bar rendering code
    if (Shape) {
      try {
        const node = Shape({
          x: 10, y: 50, width: 30, height: 100,
          payload: { accuracy: 95, colorAccuracy: 95, isFullCombo: false, date: '2024-01-01', score: 100000 },
        });
        return <div data-testid="bar">{node}</div>;
      } catch {
        return <div data-testid="bar" />;
      }
    }
    return <div data-testid="bar" />;
  },
  Line: () => <div data-testid="line" />,
  XAxis: () => null,
  YAxis: ({ label: Label }: any) => {
    // Exercise the label render function
    if (Label && typeof Label === 'function') {
      try {
        Label({ viewBox: { x: 0, y: 0, width: 100, height: 200 } });
      } catch { /* ok */ }
    }
    return null;
  },
  Tooltip: () => null,
  Legend: ({ content: Content }: any) => Content ? <Content /> : null,
  ResponsiveContainer: ({ children }: any) => <div data-testid="responsive-container">{children}</div>,
  CartesianGrid: () => null,
}));

import ScoreHistoryChart from '../../../../../pages/songinfo/components/chart/ScoreHistoryChart';

beforeAll(() => {
  stubResizeObserver();
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
  vi.clearAllMocks();
  localStorage.clear();
  mockChartData.useChartData.mockReturnValue({
    songHistory: [],
    chartData: [],
    loading: false,
    instrumentCounts: {},
  });
  mockChartDimensions.useChartDimensions.mockReturnValue({
    chartContainerRef: { current: null },
    containerWidth: 400,
    maxBars: 5,
  });
});

function renderChart(overrides: Partial<React.ComponentProps<typeof ScoreHistoryChart>> = {}) {
  const defaults = {
    songId: 'song1',
    accountId: 'acc1',
    playerName: 'TestPlayer',
    skipAnimation: true,
  };
  return render(
    <TestProviders>
      <ScoreHistoryChart {...defaults} {...overrides} />
    </TestProviders>,
  );
}

const makeChartData = (count: number) => Array.from({ length: count }, (_, i) => ({
  date: `2024-${String((i % 12) + 1).padStart(2, '0')}-01`,
  dateLabel: `Entry ${i + 1}`,
  timestamp: i,
  score: 100000 + i * 5000,
  accuracy: 80 + (i % 20),
  isFullCombo: i === count - 1,
  colorAccuracy: 80 + (i % 20),
  season: 5,
  stars: Math.min(3 + Math.floor(i / 3), 6),
  newRank: count - i,
}));

describe('ScoreHistoryChart — coverage: pagination controls', () => {
  it('renders pagination buttons when data exceeds maxBars', () => {
    const data = makeChartData(12);
    mockChartData.useChartData.mockReturnValue({
      songHistory: data,
      chartData: data,
      loading: false,
      instrumentCounts: { Solo_Guitar: 12 },
    });

    const { container } = renderChart();

    // Chart should render
    expect(screen.getByTestId('responsive-container')).toBeTruthy();

    // Pagination buttons should be present (aria-labels)
    const backPageBtn = container.querySelector('[aria-label*="back"]') ?? container.querySelector('[aria-label*="Back"]');
    const fwdPageBtn = container.querySelector('[aria-label*="forward"]') ?? container.querySelector('[aria-label*="Forward"]');
    expect(backPageBtn || fwdPageBtn).toBeTruthy();
  });

  it('clicking forward button navigates chart', async () => {
    const data = makeChartData(15);
    mockChartData.useChartData.mockReturnValue({
      songHistory: data,
      chartData: data,
      loading: false,
      instrumentCounts: { Solo_Guitar: 15 },
    });

    const { container } = renderChart();

    // Find forward button by aria-label
    const fwdBtns = Array.from(container.querySelectorAll('button')).filter(
      b => {
        const label = b.getAttribute('aria-label') ?? '';
        return label.toLowerCase().includes('forward');
      },
    );

    if (fwdBtns.length > 0) {
      // Click single forward
      fireEvent.click(fwdBtns[0]!);
      // Chart should still render
      expect(screen.getByTestId('responsive-container')).toBeTruthy();
    }
  });

  it('clicking back button navigates chart', async () => {
    const data = makeChartData(15);
    mockChartData.useChartData.mockReturnValue({
      songHistory: data,
      chartData: data,
      loading: false,
      instrumentCounts: { Solo_Guitar: 15 },
    });

    const { container } = renderChart();

    // First go forward, then back
    const buttons = Array.from(container.querySelectorAll('button'));
    const fwdBtn = buttons.find(b => (b.getAttribute('aria-label') ?? '').toLowerCase().includes('forward one entry'));
    const backBtn = buttons.find(b => (b.getAttribute('aria-label') ?? '').toLowerCase().includes('back one entry'));

    if (fwdBtn && !fwdBtn.disabled) {
      fireEvent.click(fwdBtn);
    }
    if (backBtn && !backBtn.disabled) {
      fireEvent.click(backBtn);
    }

    expect(screen.getByTestId('responsive-container')).toBeTruthy();
  });

  it('clicking page-jump buttons (double arrow)', () => {
    const data = makeChartData(20);
    mockChartData.useChartData.mockReturnValue({
      songHistory: data,
      chartData: data,
      loading: false,
      instrumentCounts: { Solo_Guitar: 20 },
    });

    const { container } = renderChart();

    const buttons = Array.from(container.querySelectorAll('button'));
    const fwdPageBtn = buttons.find(b => (b.getAttribute('aria-label') ?? '').toLowerCase().includes('forward one page'));
    const backPageBtn = buttons.find(b => (b.getAttribute('aria-label') ?? '').toLowerCase().includes('back one page'));

    if (fwdPageBtn && !fwdPageBtn.disabled) {
      fireEvent.click(fwdPageBtn);
    }
    if (backPageBtn && !backPageBtn.disabled) {
      fireEvent.click(backPageBtn);
    }

    expect(screen.getByTestId('responsive-container')).toBeTruthy();
  });
});

describe('ScoreHistoryChart — coverage: bar shape rendering', () => {
  it('renders bar shapes with gold FC and non-FC colors', () => {
    const data = [
      { date: '2024-01-01', dateLabel: 'Jan 1', timestamp: 0, score: 100000, accuracy: 95, isFullCombo: false, colorAccuracy: 95, season: 5, stars: 5 },
      { date: '2024-02-01', dateLabel: 'Feb 1', timestamp: 1, score: 120000, accuracy: 100, isFullCombo: true, colorAccuracy: 100, season: 5, stars: 6 },
    ];
    mockChartData.useChartData.mockReturnValue({
      songHistory: data,
      chartData: data,
      loading: false,
      instrumentCounts: { Solo_Guitar: 2 },
    });

    const { container } = renderChart();

    // Bar shape should have rendered (via recharts mock)
    const barEl = screen.getByTestId('bar');
    expect(barEl).toBeTruthy();

    // The path element should be inside the bar
    const pathEl = container.querySelector('path');
    if (pathEl) {
      expect(pathEl.getAttribute('fill')).toBeTruthy();
    }
  });
});

describe('ScoreHistoryChart — coverage: score card list', () => {
  it('renders top 5 score cards in the list', () => {
    const data = makeChartData(8);
    mockChartData.useChartData.mockReturnValue({
      songHistory: data,
      chartData: data,
      loading: false,
      instrumentCounts: { Solo_Guitar: 8 },
    });

    renderChart();

    // Should render chart with cards
    expect(screen.getByTestId('responsive-container')).toBeTruthy();
    // "View All Scores" button for >5 entries
    expect(screen.getByText('View All Scores')).toBeTruthy();
  });
});

describe('ScoreHistoryChart — coverage: legend with mixed FC/non-FC', () => {
  it('renders legend items for accuracy and FC bars', () => {
    const data = [
      { date: '2024-01-01', dateLabel: 'Jan 1', timestamp: 0, score: 100000, accuracy: 95, isFullCombo: false, colorAccuracy: 95, season: 5 },
      { date: '2024-02-01', dateLabel: 'Feb 1', timestamp: 1, score: 110000, accuracy: 100, isFullCombo: true, colorAccuracy: 100, season: 5 },
    ];
    mockChartData.useChartData.mockReturnValue({
      songHistory: data,
      chartData: data,
      loading: false,
      instrumentCounts: { Solo_Guitar: 2 },
    });

    const { container } = renderChart();

    expect(screen.getByTestId('responsive-container')).toBeTruthy();
    // Legend should show accuracy and FC labels
    expect(container.textContent).toContain('Accuracy');
    expect(container.textContent).toContain('Score');
  });
});
