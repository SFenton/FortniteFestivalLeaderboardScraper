import { beforeAll, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import RankByModal from '../../../../src/pages/leaderboards/modals/RankByModal';
import { loadMetricInfoCarousel } from '../../../../src/pages/leaderboards/firstRun/metricInfo/lazyMetricInfo';
import { TestProviders } from '../../../helpers/TestProviders';

beforeAll(async () => {
  await loadMetricInfoCarousel();
});

function renderModal(overrides: Partial<React.ComponentProps<typeof RankByModal>> = {}) {
  const props: React.ComponentProps<typeof RankByModal> = {
    visible: true,
    draft: 'totalscore',
    onDraftChange: vi.fn(),
    onClose: vi.fn(),
    onApply: vi.fn(),
    onReset: vi.fn(),
    experimentalRanksEnabled: true,
    ...overrides,
  };
  return {
    ...render(<TestProviders><RankByModal {...props} /></TestProviders>),
    props,
  };
}

describe('RankByModal', () => {
  it('preserves metric selection and apply/reset controls', () => {
    const { props } = renderModal();
    fireEvent.click(screen.getByRole('button', { name: /^Popularity-Weighted Percentile/ }));
    expect(props.onDraftChange).toHaveBeenCalledWith('weighted');

    fireEvent.click(screen.getByRole('button', { name: 'Apply' }));
    expect(props.onApply).toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Reset' }));
    expect(props.onReset).toHaveBeenCalled();
  });

  it('provides sibling accessible info actions only for per-instrument player metrics', () => {
    const { rerender } = renderModal();
    const selection = screen.getByRole('button', { name: /^FC Rate/ });
    const info = screen.getByRole('button', { name: 'Learn how FC Rate works' });
    expect(selection.contains(info)).toBe(false);
    expect(screen.getAllByRole('button', { name: /Learn how .* works/ })).toHaveLength(4);

    rerender(
      <TestProviders>
        <RankByModal
          visible
          draft="fcrate"
          onDraftChange={vi.fn()}
          onClose={vi.fn()}
          onApply={vi.fn()}
          onReset={vi.fn()}
          experimentalRanksEnabled
          subject="bands"
        />
      </TestProviders>,
    );
    expect(screen.queryByRole('button', { name: /Learn how .* works/ })).toBeNull();

    rerender(
      <TestProviders>
        <RankByModal
          visible
          draft="fcrate"
          onDraftChange={vi.fn()}
          onClose={vi.fn()}
          onApply={vi.fn()}
          onReset={vi.fn()}
          experimentalRanksEnabled
          playerScope="combo"
        />
      </TestProviders>,
    );
    expect(screen.queryByRole('button', { name: /Learn how .* works/ })).toBeNull();
    expect(screen.getByText('Full Combos divided by songs played across the instruments in this combo.')).toBeInTheDocument();

    rerender(
      <TestProviders>
        <RankByModal
          visible
          draft="maxscore"
          onDraftChange={vi.fn()}
          onClose={vi.fn()}
          onApply={vi.fn()}
          onReset={vi.fn()}
          experimentalRanksEnabled
          playerScope="family"
        />
      </TestProviders>,
    );
    expect(screen.queryByRole('button', { name: /Learn how .* works/ })).toBeNull();
    expect(screen.getByText(/Unplayed charts contribute 0%/)).toBeInTheDocument();
  });

  it('opens metric help above Rank By and restores its info trigger', async () => {
    renderModal({ draft: 'fcrate', metrics: ['fcrate'] });
    const parent = screen.getByRole('dialog', { name: 'Rank By' });
    const info = screen.getByRole('button', { name: 'Learn how FC Rate works' });
    info.focus();
    fireEvent.click(info);

    const help = await screen.findByRole('dialog', { name: 'FC Rate details' });
    await waitFor(() => expect(parent).toHaveProperty('inert', true));
    expect(parent).toHaveAttribute('aria-hidden', 'true');
    expect(help).toBeVisible();

    fireEvent.keyDown(document, { key: 'Escape' });
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'FC Rate details' })).toBeNull());
    expect(parent).toBeVisible();
    expect(info).toHaveFocus();
  });
});
