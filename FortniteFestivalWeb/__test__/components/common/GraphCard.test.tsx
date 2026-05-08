import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import type { ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import GraphCard from '../../../src/components/common/GraphCard';

const mockUseChartDimensions = vi.hoisted(() => vi.fn());

vi.mock('../../../src/hooks/chart/useChartDimensions', () => ({
  useChartDimensions: mockUseChartDimensions,
}));

type TestPoint = {
  id: number;
  label: string;
};

const data: TestPoint[] = [
  { id: 1, label: 'Oldest' },
  { id: 2, label: 'Middle' },
  { id: 3, label: 'Newest' },
];

function renderGraphCard(maxBars: number) {
  mockUseChartDimensions.mockReturnValue({ maxBars });

  render(
    <GraphCard<TestPoint>
      data={data}
      loading={false}
      instruments={[]}
      selected={'Solo_Guitar' as InstrumentKey}
      onInstrumentSelect={() => {}}
      title="History"
      subtitle="History subtitle"
      loadingMessage="Loading history"
      emptyMessage="No history"
      identity={(a, b) => a.id === b.id}
      renderChart={({ visibleData }) => (
        <div data-testid="chart-window">
          {visibleData.map(point => <span key={point.id}>{point.label}</span>)}
        </div>
      )}
      skipAnimation
    />,
  );
}

describe('GraphCard pagination controls', () => {
  beforeEach(() => {
    mockUseChartDimensions.mockReset();
  });

  it('hides page-jump buttons when only one bar fits', () => {
    renderGraphCard(1);

    expect(screen.queryByLabelText('Back one page')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Forward one page')).not.toBeInTheDocument();
    expect(screen.getByLabelText('Back one entry')).toBeInTheDocument();
    expect(screen.getByLabelText('Forward one entry')).toBeInTheDocument();
  });

  it('keeps page-jump buttons when multiple bars fit', () => {
    renderGraphCard(2);

    expect(screen.getByLabelText('Back one page')).toBeInTheDocument();
    expect(screen.getByLabelText('Forward one page')).toBeInTheDocument();
    expect(screen.getByLabelText('Back one entry')).toBeInTheDocument();
    expect(screen.getByLabelText('Forward one entry')).toBeInTheDocument();
  });
});
