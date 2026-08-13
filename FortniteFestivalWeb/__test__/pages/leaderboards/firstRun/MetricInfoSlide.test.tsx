import { isValidElement } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import MetricInfoSlide from '../../../../src/pages/leaderboards/firstRun/metricInfo/MetricInfoSlide';
import {
  calculateCredibilityAdjustedRating,
  FC_RATE_EXAMPLE_CATALOG_SIZE,
  FC_RATE_EXAMPLE_COUNTS,
  FC_RATE_FORMULA,
  MAX_SCORE_EXAMPLE_VALUES,
  WEIGHTED_HOW_EXAMPLES,
  getMetricInfoSlides,
} from '../../../../src/pages/leaderboards/firstRun/metricInfo';
import { SlideHeightContext } from '../../../../src/firstRun/SlideHeightContext';
import type { ExperimentalRankingMetric } from '../../../../src/pages/leaderboards/helpers/rankingHelpers';
import type { MetricInfoSlideProps } from '../../../../src/pages/leaderboards/firstRun/metricInfo/MetricInfoSlide';

const mockUseIsMobile = vi.hoisted(() => vi.fn(() => false));

vi.mock('../../../../src/hooks/ui/useIsMobile', () => ({
  useIsMobile: mockUseIsMobile,
}));

const weightedFormulas = [
  'w_i = \\log_2(N_i)',
  '\\mathrm{RWP} = \\frac{\\sum_i p_i w_i}{\\sum_i w_i}',
  '\\text{Rating} = \\frac{n \\cdot \\mathrm{RWP} + 50 \\cdot 0.5}{n + 50}',
];

function renderSlide(slideHeight = 420) {
  return render(
    <SlideHeightContext.Provider value={slideHeight}>
      <MetricInfoSlide
        paragraphs={['First, each song\'s rank percentile is weighted by leaderboard population.']}
        formulas={weightedFormulas}
        callout="This callout should be hidden when the measured slide area is too short."
      />
    </SlideHeightContext.Provider>,
  );
}

function formulaCountsFor(metric: ExperimentalRankingMetric) {
  return getMetricInfoSlides(metric).filter(slide => slide.id.includes('hood')).map(slide => {
    const { container } = render(<>{slide.render()}</>);
    const count = container.querySelectorAll('.katex-display').length;
    cleanup();
    return { id: slide.id, count };
  });
}

function renderMetricFormulaSlide(metric: ExperimentalRankingMetric, id: string) {
  const slide = getMetricInfoSlides(metric).find(candidate => candidate.id === id);
  if (!slide) throw new Error(`Slide ${id} not found`);

  return render(
    <SlideHeightContext.Provider value={420}>
      {slide.render()}
    </SlideHeightContext.Provider>,
  );
}

function renderMetricSlide(metric: ExperimentalRankingMetric, id: string, slideHeight = 520) {
  const slide = getMetricInfoSlides(metric).find(candidate => candidate.id === id);
  if (!slide) throw new Error(`Slide ${id} not found`);

  return render(
    <SlideHeightContext.Provider value={slideHeight}>
      {slide.render()}
    </SlideHeightContext.Provider>,
  );
}

const comparisonCards = [
  {
    label: 'After 3 songs',
    entries: [
      { rank: 55, displayName: 'StageKnight', ratingLabel: '52.0%' },
      { rank: 56, displayName: 'You', ratingLabel: '51.2%', isPlayer: true },
      { rank: 57, displayName: 'FretBlaze', ratingLabel: '50.8%' },
    ],
    highlight: 'Few scores — ranking is cautious',
  },
  {
    label: 'After 100 songs',
    entries: [
      { rank: 3, displayName: 'GoldStreak', ratingLabel: '79.7%' },
      { rank: 4, displayName: 'NoteHunter', ratingLabel: '79.5%' },
      { rank: 5, displayName: 'You', ratingLabel: '79.3%', isPlayer: true },
    ],
    highlight: 'Consistent accuracy across many songs',
  },
];

describe('MetricInfoSlide', () => {
  beforeEach(() => {
    mockUseIsMobile.mockReturnValue(false);
  });

  it('renders formula slides with overflow-safe wrappers', () => {
    const { container } = renderSlide();
    const formulas = Array.from(container.querySelectorAll('.katex-display'));

    expect(formulas).toHaveLength(weightedFormulas.length);
    for (const formula of formulas) {
      const mathWrapper = formula.parentElement as HTMLElement;
      const formulaWrapper = mathWrapper.parentElement as HTMLElement;

      expect(mathWrapper.style.width).toBe('100%');
      expect(mathWrapper.style.maxWidth).toBe('100%');
      expect(mathWrapper.style.minWidth).toBe('0px');
      expect(mathWrapper.style.overflowX).toBe('auto');
      expect(mathWrapper.style.overflowY).toBe('visible');
      expect(formulaWrapper.style.width).toBe('100%');
      expect(formulaWrapper.style.maxWidth).toBe('100%');
      expect(formulaWrapper.style.minWidth).toBe('0px');
      expect(formulaWrapper.style.overflowX).toBe('hidden');
      expect(formulaWrapper.style.overflowY).toBe('visible');
    }
  });

  it('keeps formula-only callouts visible instead of hiding content by estimate', () => {
    renderSlide(260);

    expect(screen.getByText('This callout should be hidden when the measured slide area is too short.')).toBeTruthy();
    expect(screen.getAllByText(/RWP|Rating/).length).toBeGreaterThan(0);
  });

  it('keeps metric-info formula slides to one display formula each', () => {
    for (const metric of ['adjusted', 'weighted', 'fcrate', 'maxscore'] as ExperimentalRankingMetric[]) {
      const counts = formulaCountsFor(metric);
      expect(counts.filter(slide => slide.count > 1), metric).toEqual([]);
    }

    expect(formulaCountsFor('weighted').filter(slide => slide.count === 1)).toHaveLength(3);
    expect(formulaCountsFor('fcrate').filter(slide => slide.count === 1)).toHaveLength(1);
    expect(formulaCountsFor('maxscore').filter(slide => slide.count === 1)).toHaveLength(2);
  });

  it('defines exactly seven reviewed TeX formulas with the production FC denominator', () => {
    const formulas = (['adjusted', 'weighted', 'fcrate', 'maxscore'] as ExperimentalRankingMetric[])
      .flatMap(metric => getMetricInfoSlides(metric))
      .flatMap(slide => {
        const element = slide.render();
        if (!isValidElement<MetricInfoSlideProps>(element)) return [];
        return element.props.formulas ?? [];
      });

    expect(formulas).toHaveLength(7);
    expect(formulas).toContain(FC_RATE_FORMULA);
    expect(FC_RATE_FORMULA).toContain('Total Charted Songs');
    expect(FC_RATE_FORMULA).not.toContain('n + 50');
  });

  it('describes the fixed FC denominator without implying attempts lower the rate', () => {
    const slide = getMetricInfoSlides('fcrate')
      .find(candidate => candidate.id === 'metric-info-fcrate-experimental');
    const { container } = render(<>{slide?.render()}</>);

    expect(container.textContent).toContain('only completed FCs change the numerator');
    expect(container.textContent).not.toContain('only attempts easy charts');
  });

  it('uses population and FC examples that discrete production values can represent', () => {
    expect(WEIGHTED_HOW_EXAMPLES.map(example => ({
      rank: example.rank,
      percentile: example.rank / example.population,
    }))).toEqual([
      { rank: 360, percentile: 0.03 },
      { rank: 3, percentile: 0.03 },
    ]);

    expect(FC_RATE_EXAMPLE_COUNTS.map(count => (
      `${((count / FC_RATE_EXAMPLE_CATALOG_SIZE) * 100).toFixed(1)}%`
    ))).toEqual([
      '3.0%', '2.0%', '1.0%',
      '66.0%', '65.0%', '64.0%',
    ]);
  });

  it('derives credibility examples from the production formula', () => {
    expect(calculateCredibilityAdjustedRating(0, 5)).toBeCloseTo(25 / 55);
    expect(calculateCredibilityAdjustedRating(1.05, 100)).toBeCloseTo(130 / 150);

    const weighted = getMetricInfoSlides('weighted')
      .find(slide => slide.id === 'metric-info-weighted-experience');
    const weightedRender = render(<>{weighted?.render()}</>);
    expect(weightedRender.container.textContent).toContain('45.7%');
    expect(weightedRender.container.textContent).not.toContain('44.8%');
    weightedRender.unmount();

    const maxScore = getMetricInfoSlides('maxscore')
      .find(slide => slide.id === 'metric-info-maxscore-experience');
    const maxScoreRender = render(<>{maxScore?.render()}</>);
    expect(maxScoreRender.container.textContent).toContain('79.7%');
    expect(maxScoreRender.container.textContent).not.toContain('94.5%');
  });

  it('documents how missing chart maxima affect Max Score credibility', () => {
    const slide = getMetricInfoSlides('maxscore')
      .find(candidate => candidate.id === 'metric-info-maxscore-experimental');
    const { container } = render(<>{slide?.render()}</>);

    expect(container.textContent).toContain('omitted from the raw per-song average');
    expect(container.textContent).toContain('still counts toward the credibility adjustment');
  });

  it('keeps the max-score example below the 105 percent eligibility cutoff', () => {
    expect(MAX_SCORE_EXAMPLE_VALUES.map(value => value.valueLines[1])).toEqual(['95.2%', '94.5%', '104.9%']);
    expect(MAX_SCORE_EXAMPLE_VALUES.some(value => value.valueLabel.includes('cap'))).toBe(false);
  });

  it('uses compact formula-slide layout and TeX labels for weighted formulas', () => {
    const { container } = renderMetricFormulaSlide('weighted', 'metric-info-weighted-hood-average');
    const wrapper = container.firstElementChild as HTMLElement;
    const formulaWrapper = container.querySelector('.katex-display')?.parentElement?.parentElement as HTMLElement;

    expect(wrapper.style.justifyContent).toBe('center');
    expect(wrapper.style.minHeight).toBe('420px');
    expect(formulaWrapper.style.fontSize).toBe('14px');
    expect(container.textContent).toContain('RWP');
    expect(container.textContent).not.toContain('Raw weighted percentile');
    expect(container.textContent).not.toContain('Weight');
  });

  it('renders song value metadata as separate blue lines', () => {
    render(
      <MetricInfoSlide
        paragraphs={[]}
        songRows={[
          {
            title: 'Very Long Song Title That Needs Marquee Space',
            artist: 'Epic Games',
            valueLabel: '12,000 players · Top 3%',
            valueLines: ['12,000 players', 'Top 3%'],
          },
        ]}
      />,
    );

    const lines = screen.getAllByTestId('metric-song-value-line');
    expect(lines.map(line => line.textContent)).toEqual(['12,000 players', 'Top 3%']);
    expect(lines[0]?.parentElement?.style.flexShrink).toBe('0');
  });

  it('stacks rank-card comparisons on mobile', () => {
    mockUseIsMobile.mockReturnValue(true);
    render(
      <SlideHeightContext.Provider value={520}>
        <MetricInfoSlide paragraphs={['Mobile comparisons should stack.']} cards={comparisonCards} />
      </SlideHeightContext.Provider>,
    );

    expect(screen.getByTestId('metric-rank-card-pair').style.flexDirection).toBe('column');
  });

  it('trims mobile rank-card demo rows while keeping player rows', () => {
    mockUseIsMobile.mockReturnValue(true);
    render(
      <SlideHeightContext.Provider value={170}>
        <MetricInfoSlide paragraphs={[]} cards={comparisonCards} />
      </SlideHeightContext.Provider>,
    );

    expect(screen.getAllByText('You')).toHaveLength(2);
    expect(screen.queryByText('StageKnight')).toBeNull();
    expect(screen.queryByText('FretBlaze')).toBeNull();
    expect(screen.queryByText('GoldStreak')).toBeNull();
    expect(screen.queryByText('NoteHunter')).toBeNull();
  });

  it('keeps blue percentage labels on score-count rank-card demos', () => {
    const cases: Array<[ExperimentalRankingMetric, string, number]> = [
      ['adjusted', 'metric-info-adjusted-experience', 8],
      ['weighted', 'metric-info-weighted-experience', 6],
      ['maxscore', 'metric-info-maxscore-experience', 6],
    ];

    for (const [metric, id, labelCount] of cases) {
      const { container } = renderMetricSlide(metric, id);
      expect(container.querySelectorAll('[data-testid="ranking-rating-label"]'), id).toHaveLength(labelCount);
      cleanup();
    }
  });
});