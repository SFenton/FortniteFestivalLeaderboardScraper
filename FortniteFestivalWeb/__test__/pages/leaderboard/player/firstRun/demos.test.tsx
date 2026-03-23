import { describe, it, expect, vi, beforeEach, beforeAll } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { stubResizeObserver } from '../../../../helpers/browserStubs';
import { TestProviders } from '../../../../helpers/TestProviders';

// Controllable slide height mock
let mockSlideHeight = 400;
vi.mock('../../../../../src/firstRun/SlideHeightContext', () => ({
  SlideHeightContext: { Provider: ({ children }: any) => children },
  useSlideHeight: () => mockSlideHeight,
}));

import ScoreListDemo from '../../../../../src/pages/leaderboard/player/firstRun/demo/ScoreListDemo';
import SortControlsDemo from '../../../../../src/pages/leaderboard/player/firstRun/demo/SortControlsDemo';

beforeAll(() => {
  stubResizeObserver();
});

beforeEach(() => {
  mockSlideHeight = 400;
});

function wrap(ui: React.ReactElement) {
  return render(ui, { wrapper: TestProviders });
}

describe('ScoreListDemo', () => {
  it('renders score entries', () => {
    wrap(<ScoreListDemo />);
    expect(screen.getByText('486,500')).toBeTruthy();
    expect(screen.getByText('412,300')).toBeTruthy();
  });

  it('renders date labels with full format', () => {
    wrap(<ScoreListDemo />);
    const today = new Date().toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
    expect(screen.getByText(today)).toBeTruthy();
  });

  it('highlights the top score row', () => {
    const { container } = wrap(<ScoreListDemo />);
    const highlightRows = container.querySelectorAll('[class*="rowHighlight"]');
    expect(highlightRows.length).toBe(1);
  });

  it('renders all 5 entries at default height', () => {
    wrap(<ScoreListDemo />);
    expect(screen.getByText('218,400')).toBeTruthy();
  });

  it('uses default maxRows when slide height is 0', () => {
    mockSlideHeight = 0;
    wrap(<ScoreListDemo />);
    expect(screen.getByText('486,500')).toBeTruthy();
    expect(screen.getByText('218,400')).toBeTruthy();
  });

  it('limits rows with small slide height', () => {
    mockSlideHeight = 50;
    wrap(<ScoreListDemo />);
    expect(screen.getByText('486,500')).toBeTruthy();
  });
});

describe('SortControlsDemo', () => {
  it('renders sort mode labels', () => {
    wrap(<SortControlsDemo />);
    expect(screen.getByText('Date')).toBeTruthy();
    expect(screen.getByText('Score')).toBeTruthy();
    expect(screen.getByText('Accuracy')).toBeTruthy();
    expect(screen.getByText('Season')).toBeTruthy();
  });

  it('renders direction selector', () => {
    wrap(<SortControlsDemo />);
    expect(screen.getByText('Sort Direction')).toBeTruthy();
  });

  it('shows descending hint by default', () => {
    wrap(<SortControlsDemo />);
    expect(screen.getByText('Descending (newest first, high–low)')).toBeTruthy();
  });

  it('allows clicking sort modes', () => {
    wrap(<SortControlsDemo />);
    fireEvent.click(screen.getByText('Date'));
    const dateBtn = screen.getByText('Date').closest('button');
    expect(dateBtn?.className).toContain('Selected');
  });

  it('allows toggling direction', () => {
    wrap(<SortControlsDemo />);
    const ascBtn = screen.getByLabelText('A→Z');
    fireEvent.click(ascBtn);
    expect(screen.getByText('Ascending (oldest first, low–high)')).toBeTruthy();
  });

  it('renders with zero slide height', () => {
    mockSlideHeight = 0;
    wrap(<SortControlsDemo />);
    expect(screen.getByText('Score')).toBeTruthy();
  });

  it('adapts layout when slide height is small', () => {
    mockSlideHeight = 60;
    wrap(<SortControlsDemo />);
    // At very small height, at least 1 sort mode renders
    expect(screen.getByText('Date')).toBeTruthy();
  });
});
