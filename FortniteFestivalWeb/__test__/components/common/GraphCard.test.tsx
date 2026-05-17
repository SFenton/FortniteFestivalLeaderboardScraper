import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
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

function dispatchPointer(target: Element, type: string, props: Partial<PointerEvent> = {}) {
  const event = new Event(type, { bubbles: true, cancelable: true }) as PointerEvent;
  Object.defineProperties(event, {
    pointerId: { value: props.pointerId ?? 1 },
    pointerType: { value: props.pointerType ?? 'touch' },
    isPrimary: { value: props.isPrimary ?? true },
    button: { value: props.button ?? 0 },
    clientX: { value: props.clientX ?? 0 },
    clientY: { value: props.clientY ?? 0 },
    timeStamp: { value: props.timeStamp ?? 0 },
  });
  fireEvent(target, event);
  return event;
}

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

  it('commits chart pagination on touch pointerup', () => {
    renderGraphCard(2);
    expect(screen.getByText('Newest')).toBeInTheDocument();

    const back = screen.getByLabelText('Back one entry');
    dispatchPointer(back, 'pointerdown', { clientX: 12, clientY: 12, timeStamp: 10 });
    dispatchPointer(back, 'pointerup', { clientX: 12, clientY: 12, timeStamp: 20 });

    expect(screen.queryByText('Newest')).not.toBeInTheDocument();
    expect(screen.getByText('Oldest')).toBeInTheDocument();
  });
});
