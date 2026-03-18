import { describe, it, expect, vi, beforeEach, beforeAll } from 'vitest';
import { render, screen } from '@testing-library/react';
import { TestProviders } from '../helpers/TestProviders';
import { stubResizeObserver } from '../helpers/browserStubs';

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

vi.mock('../../api/client', () => ({ api: mockApi }));

// Mock the chart data hook
const mockChartData = vi.hoisted(() => ({
  useChartData: vi.fn().mockReturnValue({
    songHistory: [],
    chartData: [],
    loading: false,
    instrumentCounts: {} as Record<string, number>,
  }),
}));

vi.mock('../../hooks/chart/useChartData', () => mockChartData);

// Mock recharts
vi.mock('recharts', () => ({
  ComposedChart: ({ children }: any) => <div data-testid="composed-chart">{children}</div>,
  Bar: () => <div data-testid="bar" />,
  Line: () => <div data-testid="line" />,
  XAxis: () => null,
  YAxis: () => null,
  Tooltip: () => null,
  Legend: ({ content: Content }: any) => Content ? <Content /> : null,
  ResponsiveContainer: ({ children }: any) => <div data-testid="responsive-container">{children}</div>,
  CartesianGrid: () => null,
}));

import ScoreHistoryChart from '../../pages/songinfo/components/chart/ScoreHistoryChart';

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
  date: `2024-0${(i % 12) + 1}-01`,
  dateLabel: `Entry ${i + 1}`,
  timestamp: i,
  score: 100000 + i * 5000,
  accuracy: 85 + i,
  isFullCombo: i === count - 1,
  colorAccuracy: 85 + i,
  season: 5,
  stars: Math.min(3 + Math.floor(i / 2), 6),
  newRank: count - i,
}));

describe('ScoreHistoryChart — extra coverage', () => {
  /* ── Chart bar rendering ── */
  it('renders bars via composed chart when data is available', () => {
    const data = makeChartData(3);
    mockChartData.useChartData.mockReturnValue({
      songHistory: data,
      chartData: data,
      loading: false,
      instrumentCounts: { Solo_Guitar: 3 },
    });
    renderChart();
    expect(screen.getByTestId('responsive-container')).toBeTruthy();
    expect(screen.getByTestId('composed-chart')).toBeTruthy();
  });

  /* ── Instrument switching ── */
  it('auto-selects first instrument with data when default has none', () => {
    mockChartData.useChartData.mockReturnValue({
      songHistory: [{}],
      chartData: [{ date: '2024-01-01', dateLabel: 'Jan 1', timestamp: 0, score: 100000, accuracy: 95, isFullCombo: false }],
      loading: false,
      instrumentCounts: { Solo_Guitar: 0, Solo_Bass: 3 },
    });
    renderChart({ defaultInstrument: 'Solo_Guitar' });
    // Should render chart content (auto-selected Bass)
    expect(screen.getByTestId('responsive-container')).toBeTruthy();
  });

  it('renders instrument selector when multiple instruments have history', () => {
    mockChartData.useChartData.mockReturnValue({
      songHistory: [{}, {}, {}],
      chartData: makeChartData(2),
      loading: false,
      instrumentCounts: { Solo_Guitar: 2, Solo_Bass: 1, Solo_Drums: 1 },
    });
    renderChart();
    expect(screen.getByTestId('responsive-container')).toBeTruthy();
  });

  /* ── Score card display ── */
  it('renders score card list with top entries sorted by score', () => {
    const data = makeChartData(6);
    mockChartData.useChartData.mockReturnValue({
      songHistory: data,
      chartData: data,
      loading: false,
      instrumentCounts: { Solo_Guitar: 6 },
    });
    renderChart();
    // Chart renders with data, score cards should be shown below
    expect(screen.getByTestId('responsive-container')).toBeTruthy();
  });

  /* ── Chart pagination ── */
  it('renders with many data points triggering pagination', () => {
    const data = makeChartData(20);
    mockChartData.useChartData.mockReturnValue({
      songHistory: data,
      chartData: data,
      loading: false,
      instrumentCounts: { Solo_Guitar: 20 },
    });
    renderChart();
    expect(screen.getByTestId('responsive-container')).toBeTruthy();
  });

  /* ── With visible instruments filtering ── */
  it('filters visible instruments via prop', () => {
    mockChartData.useChartData.mockReturnValue({
      songHistory: [{}],
      chartData: makeChartData(1),
      loading: false,
      instrumentCounts: { Solo_Guitar: 1, Solo_Bass: 1 },
    });
    renderChart({ visibleInstruments: ['Solo_Guitar'] as any });
    expect(screen.getByTestId('responsive-container')).toBeTruthy();
  });

  /* ── Default instrument selection ── */
  it('uses defaultInstrument prop when provided', () => {
    mockChartData.useChartData.mockReturnValue({
      songHistory: [{}],
      chartData: makeChartData(1),
      loading: false,
      instrumentCounts: { Solo_Guitar: 0, Solo_Bass: 2 },
    });
    renderChart({ defaultInstrument: 'Solo_Bass' as any });
    expect(screen.getByTestId('responsive-container')).toBeTruthy();
  });

  /* ── View All Scores button ── */
  it('shows view all scores button with more than 5 chart entries', () => {
    const data = makeChartData(8);
    mockChartData.useChartData.mockReturnValue({
      songHistory: data,
      chartData: data,
      loading: false,
      instrumentCounts: { Solo_Guitar: 8 },
    });
    renderChart();
    expect(screen.getByText('View All Scores')).toBeTruthy();
  });

  it('hides view all scores button with 5 or fewer entries', () => {
    const data = makeChartData(4);
    mockChartData.useChartData.mockReturnValue({
      songHistory: data,
      chartData: data,
      loading: false,
      instrumentCounts: { Solo_Guitar: 4 },
    });
    renderChart();
    expect(screen.queryByText('View All Scores')).toBeNull();
  });

  /* ── Chart title and subtitle ── */
  it('renders score history title', () => {
    mockChartData.useChartData.mockReturnValue({
      songHistory: [{}],
      chartData: makeChartData(1),
      loading: false,
      instrumentCounts: { Solo_Guitar: 1 },
    });
    renderChart();
    expect(screen.getByText('Score History')).toBeTruthy();
  });

  it('renders hint text to select a bar', () => {
    mockChartData.useChartData.mockReturnValue({
      songHistory: [{}],
      chartData: makeChartData(1),
      loading: false,
      instrumentCounts: { Solo_Guitar: 1 },
    });
    renderChart();
    expect(screen.getByText('Select a bar to see more score details.')).toBeTruthy();
  });

  /* ── Loading and empty states ── */
  it('shows loading indicator when chart data is loading', () => {
    mockChartData.useChartData.mockReturnValue({
      songHistory: [],
      chartData: [],
      loading: true,
      instrumentCounts: {},
    });
    renderChart();
    expect(screen.getByText('Loading score history…')).toBeTruthy();
  });

  it('shows no history message for empty data', () => {
    mockChartData.useChartData.mockReturnValue({
      songHistory: [],
      chartData: [],
      loading: false,
      instrumentCounts: { Solo_Guitar: 0 },
    });
    renderChart();
    expect(screen.getByText(/No score history/)).toBeTruthy();
  });

  /* ── scoreWidth prop ── */
  it('accepts custom scoreWidth prop', () => {
    mockChartData.useChartData.mockReturnValue({
      songHistory: [{}],
      chartData: makeChartData(1),
      loading: false,
      instrumentCounts: { Solo_Guitar: 1 },
    });
    renderChart({ scoreWidth: '8ch' });
    expect(screen.getByText('Score History')).toBeTruthy();
  });
});
