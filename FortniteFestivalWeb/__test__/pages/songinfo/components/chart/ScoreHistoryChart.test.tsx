import { describe, it, expect, vi, beforeEach, beforeAll } from 'vitest';
import { render, screen } from '@testing-library/react';
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

vi.mock('../../../../../src/api/client', () => ({ api: mockApi }));

// Mock the chart data hook to avoid heavy import chains
const mockChartData = vi.hoisted(() => ({
  useChartData: vi.fn().mockReturnValue({
    songHistory: [],
    chartData: [],
    loading: false,
    instrumentCounts: {} as Record<string, number>,
  }),
}));

vi.mock('../../../../../src/hooks/chart/useChartData', () => mockChartData);

// Mock recharts to avoid SVG rendering and massive bundle
vi.mock('recharts', () => ({
  ComposedChart: ({ children }: any) => <div data-testid="composed-chart">{children}</div>,
  Bar: () => <div data-testid="bar" />,
  Line: () => <div data-testid="line" />,
  XAxis: (props: any) => <div data-testid="xaxis" data-axisline={String(props.axisLine ?? true)} data-tickline={String(props.tickLine ?? true)} />,
  YAxis: (props: any) => <div data-testid={`yaxis-${props.yAxisId ?? 'default'}`} data-axisline={String(props.axisLine ?? true)} data-tickline={String(props.tickLine ?? true)} />,
  Tooltip: () => null,
  Legend: ({ content: Content }: any) => Content ? <Content /> : null,
  ResponsiveContainer: ({ children }: any) => <div data-testid="responsive-container">{children}</div>,
  CartesianGrid: () => null,
}));

import ScoreHistoryChart from '../../../../../src/pages/songinfo/components/chart/ScoreHistoryChart';

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

describe('ScoreHistoryChart', () => {
  it('shows loading text while fetching history', () => {
    mockChartData.useChartData.mockReturnValue({
      songHistory: [], chartData: [], loading: true, instrumentCounts: {},
    });
    renderChart();
    expect(screen.getByText('Loading score history…')).toBeTruthy();
  });

  it('shows no history message when data is empty', () => {
    mockChartData.useChartData.mockReturnValue({
      songHistory: [], chartData: [], loading: false, instrumentCounts: { Solo_Guitar: 0 },
    });
    renderChart();
    expect(screen.getByText(/No score history/)).toBeTruthy();
  });

  it('renders chart when history data is available', () => {
    mockChartData.useChartData.mockReturnValue({
      songHistory: [{}, {}, {}],
      chartData: [
        { date: '2024-01-01', dateLabel: 'Jan 1', timestamp: 0, score: 100000, accuracy: 95, isFullCombo: false },
        { date: '2024-02-01', dateLabel: 'Feb 1', timestamp: 1, score: 110000, accuracy: 97, isFullCombo: false },
        { date: '2024-03-01', dateLabel: 'Mar 1', timestamp: 2, score: 120000, accuracy: 99, isFullCombo: true },
      ],
      loading: false,
      instrumentCounts: { Solo_Guitar: 3 },
    });
    renderChart();
    expect(screen.getByTestId('responsive-container')).toBeTruthy();
  });

  it('renders chart title', () => {
    mockChartData.useChartData.mockReturnValue({
      songHistory: [{}],
      chartData: [
        { date: '2024-01-01', dateLabel: 'Jan 1', timestamp: 0, score: 100000, accuracy: 95, isFullCombo: false },
      ],
      loading: false,
      instrumentCounts: { Solo_Guitar: 1 },
    });
    renderChart();
    expect(screen.getByText('Score History')).toBeTruthy();
  });

  it('renders axes with visible axis lines and tick lines', () => {
    mockChartData.useChartData.mockReturnValue({
      songHistory: [{}],
      chartData: [
        { date: '2024-01-01', dateLabel: 'Jan 1', timestamp: 0, score: 100000, accuracy: 95, isFullCombo: false },
      ],
      loading: false,
      instrumentCounts: { Solo_Guitar: 1 },
    });
    renderChart();
    const xaxis = screen.getByTestId('xaxis');
    expect(xaxis.dataset.axisline).toBe('true');
    expect(xaxis.dataset.tickline).toBe('true');
    const scoreAxis = screen.getByTestId('yaxis-score');
    expect(scoreAxis.dataset.axisline).toBe('true');
    expect(scoreAxis.dataset.tickline).toBe('true');
    const accAxis = screen.getByTestId('yaxis-accuracy');
    expect(accAxis.dataset.axisline).toBe('true');
    expect(accAxis.dataset.tickline).toBe('true');
  });

  it('renders chart hint text', () => {
    mockChartData.useChartData.mockReturnValue({
      songHistory: [{}],
      chartData: [
        { date: '2024-01-01', dateLabel: 'Jan 1', timestamp: 0, score: 100000, accuracy: 95, isFullCombo: false },
      ],
      loading: false,
      instrumentCounts: { Solo_Guitar: 1 },
    });
    renderChart();
    expect(screen.getByText('Select a bar to see more score details.')).toBeTruthy();
  });

  it('renders instrument selector when multiple instruments have data', () => {
    mockChartData.useChartData.mockReturnValue({
      songHistory: [{}, {}],
      chartData: [
        { date: '2024-01-01', dateLabel: 'Jan 1', timestamp: 0, score: 100000, accuracy: 95, isFullCombo: false },
      ],
      loading: false,
      instrumentCounts: { Solo_Guitar: 1, Solo_Bass: 1 },
    });
    renderChart();
    expect(screen.getByTestId('responsive-container')).toBeTruthy();
  });

  it('shows "view all scores" button when more than 5 entries', () => {
    const chartData = Array.from({ length: 7 }, (_, i) => ({
      date: `2024-0${i + 1}-01`,
      dateLabel: `Entry ${i + 1}`,
      timestamp: i,
      score: 100000 + i * 1000,
      accuracy: 90 + i,
      isFullCombo: false,
    }));
    mockChartData.useChartData.mockReturnValue({
      songHistory: chartData,
      chartData,
      loading: false,
      instrumentCounts: { Solo_Guitar: 7 },
    });
    renderChart();
    expect(screen.getByText('View All Scores')).toBeTruthy();
  });

  it('does not show "view all scores" button with 5 or fewer entries', () => {
    const chartData = Array.from({ length: 3 }, (_, i) => ({
      date: `2024-0${i + 1}-01`,
      dateLabel: `Entry ${i + 1}`,
      timestamp: i,
      score: 100000 + i * 1000,
      accuracy: 90 + i,
      isFullCombo: false,
    }));
    mockChartData.useChartData.mockReturnValue({
      songHistory: chartData,
      chartData,
      loading: false,
      instrumentCounts: { Solo_Guitar: 3 },
    });
    renderChart();
    expect(screen.getByTestId('responsive-container')).toBeTruthy();
    expect(screen.queryByText('View All Scores')).toBeNull();
  });

  it('filters visible instruments', () => {
    mockChartData.useChartData.mockReturnValue({
      songHistory: [{}],
      chartData: [
        { date: '2024-01-01', dateLabel: 'Jan 1', timestamp: 0, score: 100000, accuracy: 95, isFullCombo: false },
      ],
      loading: false,
      instrumentCounts: { Solo_Guitar: 1, Solo_Bass: 1 },
    });
    renderChart({ visibleInstruments: ['Solo_Guitar'] });
    expect(screen.getByTestId('responsive-container')).toBeTruthy();
  });

  it('uses provided defaultInstrument', () => {
    mockChartData.useChartData.mockReturnValue({
      songHistory: [{}],
      chartData: [
        { date: '2024-01-01', dateLabel: 'Jan 1', timestamp: 0, score: 80000, accuracy: 85, isFullCombo: false },
      ],
      loading: false,
      instrumentCounts: { Solo_Bass: 1 },
    });
    renderChart({ defaultInstrument: 'Solo_Bass' });
    expect(screen.getByTestId('responsive-container')).toBeTruthy();
  });

  it('renders with pre-supplied history prop', () => {
    mockChartData.useChartData.mockReturnValue({
      songHistory: [{}],
      chartData: [
        { date: '2024-01-01', dateLabel: 'Jan 1', timestamp: 0, score: 100000, accuracy: 95, isFullCombo: false },
      ],
      loading: false,
      instrumentCounts: { Solo_Guitar: 1 },
    });
    renderChart({
      history: [
        { songId: 'song1', instrument: 'Solo_Guitar', score: 100000, accuracy: 9500, isFullCombo: false, stars: 4, season: 3, scoreAchievedAt: '2024-01-01T00:00:00Z' } as any,
      ],
    });
    expect(screen.getByTestId('responsive-container')).toBeTruthy();
  });
});
