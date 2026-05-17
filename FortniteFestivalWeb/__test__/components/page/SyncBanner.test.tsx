import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { ComponentProps } from 'react';
import { SyncPhase } from '@festival/core';
import SyncBanner from '../../../src/components/page/SyncBanner';
import { TestProviders } from '../../helpers/TestProviders';

const baseProps = {
  backfillProgress: 0,
  historyProgress: 0,
  rivalsProgress: 0,
  itemsCompleted: 0,
  totalItems: 0,
  entriesFound: 0,
  currentSongName: null,
  seasonsQueried: 0,
  rivalsFound: 0,
  isThrottled: false,
  throttleStatusKey: null,
  probeStatusKey: null,
  nextRetrySeconds: null,
};

function renderBanner(props: Partial<ComponentProps<typeof SyncBanner>>) {
  return render(
    <TestProviders>
      <SyncBanner phase={SyncPhase.Backfill} {...baseProps} {...props} />
    </TestProviders>,
  );
}

describe('SyncBanner', () => {
  it('labels score sync progress as songs checked', () => {
    renderBanner({
      phase: SyncPhase.Backfill,
      itemsCompleted: 12,
      totalItems: 656,
      entriesFound: 6264,
      backfillProgress: 12 / 656,
    });

    expect(screen.getByText('12 / 656 songs checked')).toBeTruthy();
    expect(screen.queryByText(/new scores found/i)).toBeNull();
    expect(screen.queryByText(/score entries found/i)).toBeNull();
  });

  it('labels rivals progress as rival groups checked', () => {
    renderBanner({
      phase: SyncPhase.Rivals,
      itemsCompleted: 3,
      totalItems: 16,
      rivalsFound: 42,
      rivalsProgress: 3 / 16,
    });

    expect(screen.getByText('3 / 16 rival groups checked')).toBeTruthy();
    expect(screen.getByText('42 rivals found')).toBeTruthy();
  });

  it('renders queued as a waiting rail rather than determinate progress', () => {
    renderBanner({ phase: SyncPhase.Queued });

    expect(screen.getByTestId('sync-progress-queued-rail')).toBeTruthy();
    expect(screen.getByTestId('sync-progress-queued-highlight')).toBeTruthy();
    expect(screen.queryByTestId('sync-progress-inner')).toBeNull();
  });

  it('renders zero checked backfill as determinate zero progress', () => {
    renderBanner({
      phase: SyncPhase.Backfill,
      itemsCompleted: 0,
      totalItems: 656,
      backfillProgress: 0,
    });

    const progress = screen.getByTestId('sync-progress-inner');
    expect(screen.queryByTestId('sync-progress-queued-rail')).toBeNull();
    expect(progress).toHaveStyle({ width: '0%' });
  });
});