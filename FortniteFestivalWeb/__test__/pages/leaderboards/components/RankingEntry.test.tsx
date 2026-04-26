import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { RankingEntry } from '../../../../src/pages/leaderboards/components/RankingEntry';
import { TestProviders as W } from '../../../helpers/TestProviders';
import { Colors, Gap } from '@festival/theme';

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

  it('renders FC fraction numerator in gold', () => {
    render(
      <W>
        <RankingEntry
          rank={17}
          displayName="Tracked Player"
          ratingLabel="98.4%"
          songsLabel="123 / 500"
          songsLabelPrimary
          songsLabelGoldPrefix
        />
      </W>,
    );

    expect(screen.getByText('123')).toHaveStyle({ color: Colors.gold });
  });

  it('bolds all visible player row values', () => {
    render(
      <W>
        <RankingEntry
          rank={17}
          displayName="Tracked Player"
          ratingLabel="1,234,567"
          songsLabel="123 / 500"
          isPlayer
        />
      </W>,
    );

    expect(screen.getByText('#17').style.fontWeight).toBe('700');
    expect(screen.getByText('Tracked Player').style.fontWeight).toBe('700');
    expect(screen.getByText('123 / 500').style.fontWeight).toBe('700');
    expect(screen.getByText('1,234,567').style.fontWeight).toBe('700');
  });

  it('renders percentile value, songs, and Bayesian rank card together', () => {
    render(
      <W>
        <RankingEntry
          rank={17}
          displayName="Tracked Player"
          ratingLabel=""
          songsLabel="123 / 500"
          percentileValueDisplay="Top 0.56%"
          bayesianRankDisplay="0.0409"
          bayesianRankColor={Colors.statusGreen}
          percentileValueMinWidth={120}
          bayesianRankMinWidth={80}
          isPlayer
        />
      </W>,
    );

    expect(screen.getByText('123 / 500')).toBeTruthy();
    expect(screen.getByText('Top 0.56%').style.fontStyle).toBe('italic');
    expect(screen.getByText('Top 0.56%').style.minWidth).toBe('120px');
    expect(screen.getByText('Bayesian-Calculated Rank:')).toBeTruthy();
    expect(screen.getByText('0.0409')).toHaveStyle({ backgroundColor: Colors.statusGreen, minWidth: '80px' });
    expect(screen.getByText('0.0409').style.fontWeight).toBe('700');
  });

  it('renders percentile metadata on a second row when requested', () => {
    const { container } = render(
      <W>
        <RankingEntry
          rank={17}
          displayName="Tracked Player"
          ratingLabel=""
          songsLabel="123 / 500"
          percentileValueDisplay="Top 0.56%"
          bayesianRankDisplay="0.0409"
          bayesianRankColor={Colors.statusGreen}
          twoRowPercentileMetadata
        />
      </W>,
    );

    expect(container.querySelector('[style*="flex-direction: column"]')).toBeTruthy();
    expect(screen.getByText('123 / 500')).toBeTruthy();
    expect(screen.getByText('Top 0.56%')).toBeTruthy();
    expect(screen.getByText('0.0409')).toBeTruthy();
    const metadata = screen.getByText('Bayesian-Calculated Rank:').parentElement;
    expect(metadata?.style.paddingTop).toBe('');
    expect(metadata?.parentElement).toHaveStyle({ gap: `${Gap.xl}px` });
  });
});