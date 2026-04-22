import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { RankingEntry } from '../../../../src/pages/leaderboards/components/RankingEntry';
import { TestProviders as W } from '../../../helpers/TestProviders';

describe('RankingEntry', () => {
  it('applies custom rankWidth and animates width changes', () => {
    render(
      <W>
        <RankingEntry
          rank={12345}
          displayName="Tracked Player"
          ratingLabel="1,000,000"
          rankWidth={100}
        />
      </W>,
    );

    const rankSpan = screen.getByText('#12,345');
    expect(rankSpan.style.width).toBe('100px');
    expect(rankSpan.style.transition).toBe('width 300ms ease');
  });
});