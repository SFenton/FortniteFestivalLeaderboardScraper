import { createElement, useCallback } from 'react';
import type { FirstRunSlideDef } from '../../../../firstRun/types';
import type { ExperimentalRankingMetric } from '../../helpers/rankingHelpers';
import MetricInfoSlide from './MetricInfoSlide';
import SongDemoSlide from './SongDemoSlide';
import FcRateHowDemo from './FcRateHowDemo';

const CREDIBILITY_THRESHOLD = 50;
const POPULATION_MEDIAN = 0.5;

export function calculateCredibilityAdjustedRating(rawRating: number, scoreCount: number): number {
  return (
    scoreCount * rawRating + CREDIBILITY_THRESHOLD * POPULATION_MEDIAN
  ) / (scoreCount + CREDIBILITY_THRESHOLD);
}

function credibilityRatingLabel(rawRating: number, scoreCount: number): string {
  return `${(calculateCredibilityAdjustedRating(rawRating, scoreCount) * 100).toFixed(1)}%`;
}

/* ── Adjusted Percentile ── */

function AdjustedHowDemo() {
  const buildRows = useCallback((songs: { albumArt?: string; title: string; artist: string }[]) => [
    { ...songs[0]!, valueLabel: 'Top 1.0%', valueLines: ['Top 1.0%'] },
    { ...songs[1]!, valueLabel: 'Top 10.0%', valueLines: ['Top 10.0%'] },
    { ...songs[2]!, valueLabel: 'Top 2.5%', valueLines: ['Top 2.5%'] },
  ], []);

  return createElement(SongDemoSlide, {
    paragraphs: [],
    buildRows,
    songSummary: 'Average rank percentile: 4.5%',
  });
}

const adjustedSlides: FirstRunSlideDef[] = [
  {
    id: 'metric-info-adjusted-how',
    version: 2,
    title: 'firstRun.leaderboards.metricInfo.adjusted.how.title',
    description: 'firstRun.leaderboards.metricInfo.adjusted.how.description',
    render: () => createElement(AdjustedHowDemo),
    contentStaggerCount: 1,
  },
  {
    id: 'metric-info-adjusted-experience',
    version: 2,
    title: 'firstRun.leaderboards.metricInfo.adjusted.experience.title',
    description: 'firstRun.leaderboards.metricInfo.adjusted.experience.description',
    render: () => createElement(MetricInfoSlide, {
      paragraphs: [
        'With only a few scores on an instrument, the rating is treated cautiously. As more scores are added, it follows the rank percentiles earned on those songs more closely.',
      ],
      cards: [
        {
          label: 'After 5 scores',
          entries: [
            { rank: 52, displayName: 'FretPhenom', ratingLabel: credibilityRatingLabel(0.04, 5) },
            { rank: 53, displayName: 'NeonPick', ratingLabel: credibilityRatingLabel(0.06, 5) },
            { rank: 54, displayName: 'You', ratingLabel: credibilityRatingLabel(0.08, 5), isPlayer: true },
            { rank: 55, displayName: 'DrumSurge', ratingLabel: credibilityRatingLabel(0.11, 5) },
          ],
          highlight: 'Few scores — ranking is cautious',
        },
        {
          label: 'After 100 scores',
          entries: [
            { rank: 7, displayName: 'BeatLegend', ratingLabel: credibilityRatingLabel(0.029, 100) },
            { rank: 8, displayName: 'TopClutch', ratingLabel: credibilityRatingLabel(0.032, 100) },
            { rank: 9, displayName: 'You', ratingLabel: credibilityRatingLabel(0.035, 100), isPlayer: true },
            { rank: 10, displayName: 'ComboKing', ratingLabel: credibilityRatingLabel(0.04, 100) },
          ],
          highlight: 'Results drive more of the rating',
        },
      ],
      callout: 'A player with only a few scores should not jump high on the leaderboard. Each additional score makes earned rank percentiles count more.',
    }),
    contentStaggerCount: 3,
  },
  {
    id: 'metric-info-adjusted-hood',
    version: 2,
    title: 'firstRun.leaderboards.metricInfo.adjusted.hood.title',
    description: 'firstRun.leaderboards.metricInfo.adjusted.hood.description',
    render: () => createElement(MetricInfoSlide, {
      paragraphs: [
        'The formula factors in how many scores the player has on that instrument. More scores means the player\'s average rank percentile determines more of the rating.',
      ],
      formulas: [
        '\\text{Rating} = \\frac{n \\cdot \\bar{p} + 50 \\cdot 0.5}{n + 50}',
      ],
      callout: 'n = scores on the instrument, p̄ = average rank percentile, and 0.5 represents a neutral middle percentile.',
    }),
    contentStaggerCount: 4,
  },
  {
    id: 'metric-info-adjusted-hood-example',
    version: 2,
    title: 'firstRun.leaderboards.metricInfo.adjusted.hoodExample.title',
    description: 'firstRun.leaderboards.metricInfo.adjusted.hoodExample.description',
    render: () => createElement(MetricInfoSlide, {
      paragraphs: [
        'With a 3% average rank percentile, more scores move the rating farther from the neutral middle and closer to the earned result.',
      ],
      callout: 'After 5 scores: (5 × 0.03 + 50 × 0.5) ÷ 55 ≈ 46%.\nAfter 100 scores: (100 × 0.03 + 50 × 0.5) ÷ 150 ≈ 19%.',
    }),
    contentStaggerCount: 2,
  },
  {
    id: 'metric-info-adjusted-experimental',
    version: 2,
    title: 'firstRun.leaderboards.metricInfo.adjusted.experimental.title',
    description: 'firstRun.leaderboards.metricInfo.adjusted.experimental.description',
    render: () => createElement(MetricInfoSlide, {
      paragraphs: [
        'All songs count equally — a top 1% rank percentile on an easy song counts the same as top 1% on a hard one.',
        'How strongly score count affects early rankings is a tuning choice, not a fixed rule. A different threshold would shift everyone\'s rankings.',
      ],
    }),
    contentStaggerCount: 2,
  },
];

/* ── Weighted ── */

export const WEIGHTED_HOW_EXAMPLES = [
  { population: 12_000, rank: 360 },
  { population: 100, rank: 3 },
] as const;

function WeightedHowDemo() {
  const buildRows = useCallback(
    (songs: { albumArt?: string; title: string; artist: string }[]) => WEIGHTED_HOW_EXAMPLES.map((example, index) => {
      const populationLabel = `${example.population.toLocaleString('en-US')} players`;
      const percentileLabel = `Top ${((example.rank / example.population) * 100).toFixed(0)}%`;
      return {
        ...songs[index]!,
        valueLabel: `${populationLabel} · ${percentileLabel}`,
        valueLines: [populationLabel, percentileLabel],
      };
    }),
    [],
  );

  return createElement(SongDemoSlide, {
    paragraphs: [
      'Like Adjusted Percentile, this uses your rank percentile on each song — but songs with larger leaderboard populations carry more influence in the average.',
    ],
    buildRows,
    maxSongs: 2,
    callout: 'Same percentile, but the popular song counts more. A larger leaderboard population can make the result less dependent on very small song leaderboards — it does not guarantee the chart is harder.',
  });
}

const weightedSlides: FirstRunSlideDef[] = [
  {
    id: 'metric-info-weighted-how',
    version: 2,
    title: 'firstRun.leaderboards.metricInfo.weighted.how.title',
    description: 'firstRun.leaderboards.metricInfo.weighted.how.description',
    render: () => createElement(WeightedHowDemo),
    contentStaggerCount: 3,
  },
  {
    id: 'metric-info-weighted-experience',
    version: 2,
    title: 'firstRun.leaderboards.metricInfo.weighted.experience.title',
    description: 'firstRun.leaderboards.metricInfo.weighted.experience.description',
    render: () => createElement(MetricInfoSlide, {
      paragraphs: [
        'It uses the same score-count rule: a few scores on an instrument are only a small sample, and more scores make your weighted percentile average count more directly.',
      ],
      cards: [
        {
          label: 'After 5 scores',
          entries: [
            { rank: 38, displayName: 'StageKnight', ratingLabel: credibilityRatingLabel(0.03, 5) },
            { rank: 39, displayName: 'You', ratingLabel: credibilityRatingLabel(0.05, 5), isPlayer: true },
            { rank: 40, displayName: 'RhythmEdge', ratingLabel: credibilityRatingLabel(0.08, 5) },
          ],
          highlight: 'Few scores — ranking is cautious',
        },
        {
          label: 'After 100 scores',
          entries: [
            { rank: 4, displayName: 'GoldStreak', ratingLabel: credibilityRatingLabel(0, 100) },
            { rank: 5, displayName: 'NoteHunter', ratingLabel: credibilityRatingLabel(0.005, 100) },
            { rank: 6, displayName: 'You', ratingLabel: credibilityRatingLabel(0.01, 100), isPlayer: true },
          ],
          highlight: 'Weighted average has more influence',
        },
      ],
    }),
    contentStaggerCount: 3,
  },
  {
    id: 'metric-info-weighted-hood-weight',
    version: 2,
    title: 'firstRun.leaderboards.metricInfo.weighted.hoodWeight.title',
    description: 'firstRun.leaderboards.metricInfo.weighted.hoodWeight.description',
    render: () => createElement(MetricInfoSlide, {
      layout: 'formula',
      paragraphs: [],
      formulas: [
        'w_i = \\log_2(N_i)',
      ],
    }),
    contentStaggerCount: 1,
  },
  {
    id: 'metric-info-weighted-hood-average',
    version: 2,
    title: 'firstRun.leaderboards.metricInfo.weighted.hoodAverage.title',
    description: 'firstRun.leaderboards.metricInfo.weighted.hoodAverage.description',
    render: () => createElement(MetricInfoSlide, {
      layout: 'formula',
      paragraphs: [],
      formulas: [
        '\\mathrm{RWP} = \\frac{\\sum_i p_i w_i}{\\sum_i w_i}',
      ],
    }),
    contentStaggerCount: 1,
  },
  {
    id: 'metric-info-weighted-hood-rating',
    version: 2,
    title: 'firstRun.leaderboards.metricInfo.weighted.hoodRating.title',
    description: 'firstRun.leaderboards.metricInfo.weighted.hoodRating.description',
    render: () => createElement(MetricInfoSlide, {
      layout: 'formula',
      paragraphs: [],
      formulas: [
        '\\text{Rating} = \\frac{n \\cdot \\mathrm{RWP} + 50 \\cdot 0.5}{n + 50}',
      ],
    }),
    contentStaggerCount: 1,
  },
  {
    id: 'metric-info-weighted-experimental',
    version: 2,
    title: 'firstRun.leaderboards.metricInfo.weighted.experimental.title',
    description: 'firstRun.leaderboards.metricInfo.weighted.experimental.description',
    render: () => createElement(MetricInfoSlide, {
      paragraphs: [
        'More players does not necessarily mean a harder chart. A viral or easy song might have many casual players, so popularity is only a weighting signal.',
        'The log₂ weighting softens extremes but is still a tuning choice — a different scale would produce different rankings.',
      ],
    }),
    contentStaggerCount: 2,
  },
];

/* ── FC Rate ── */

export const FC_RATE_FORMULA = '\\text{FC Rate} = \\frac{\\text{Full Combos}}{\\text{Total Charted Songs}}';
export const FC_RATE_EXAMPLE_CATALOG_SIZE = 100;
export const FC_RATE_EXAMPLE_COUNTS = [3, 2, 1, 66, 65, 64] as const;

function fcRateLabel(fullComboCount: number): string {
  return `${((fullComboCount / FC_RATE_EXAMPLE_CATALOG_SIZE) * 100).toFixed(1)}%`;
}

const fcRateSlides: FirstRunSlideDef[] = [
  {
    id: 'metric-info-fcrate-how',
    version: 3,
    title: 'firstRun.leaderboards.metricInfo.fcrate.how.title',
    description: 'firstRun.leaderboards.metricInfo.fcrate.how.description',
    render: () => createElement(FcRateHowDemo),
    contentStaggerCount: 3,
  },
  {
    id: 'metric-info-fcrate-experience',
    version: 3,
    title: 'firstRun.leaderboards.metricInfo.fcrate.experience.title',
    description: 'firstRun.leaderboards.metricInfo.fcrate.experience.description',
    render: () => createElement(MetricInfoSlide, {
      paragraphs: [
        'The denominator is the complete chart catalog for that instrument, not only songs the player has attempted.',
      ],
      cards: [
        {
          label: 'NovaBurst · 2 FCs / 100 charts',
          entries: [
            { rank: 92, displayName: 'SonicRush', ratingLabel: fcRateLabel(FC_RATE_EXAMPLE_COUNTS[0]) },
            { rank: 93, displayName: 'NovaBurst', ratingLabel: fcRateLabel(FC_RATE_EXAMPLE_COUNTS[1]), isPlayer: true },
            { rank: 94, displayName: 'DeepGroove', ratingLabel: fcRateLabel(FC_RATE_EXAMPLE_COUNTS[2]) },
          ],
          highlight: 'Two FCs across the full catalog = 2%',
        },
        {
          label: 'BeatLegend · 65 FCs / 100 charts',
          entries: [
            { rank: 6, displayName: 'VocalStorm', ratingLabel: fcRateLabel(FC_RATE_EXAMPLE_COUNTS[3]) },
            { rank: 7, displayName: 'BeatLegend', ratingLabel: fcRateLabel(FC_RATE_EXAMPLE_COUNTS[4]), isPlayer: true },
            { rank: 8, displayName: 'TopClutch', ratingLabel: fcRateLabel(FC_RATE_EXAMPLE_COUNTS[5]) },
          ],
          highlight: 'Catalog-wide consistency = 65%',
        },
      ],
      callout: 'Playing only a few easy songs cannot create a perfect rate because every chart remains in the denominator.',
    }),
    contentStaggerCount: 4,
  },
  {
    id: 'metric-info-fcrate-hood',
    version: 3,
    title: 'firstRun.leaderboards.metricInfo.fcrate.hood.title',
    description: 'firstRun.leaderboards.metricInfo.fcrate.hood.description',
    render: () => createElement(MetricInfoSlide, {
      paragraphs: [
        'The formula divides Full Combos by the total number of charted songs for the instrument. It does not apply the Bayesian score-count adjustment used by percentile metrics.',
      ],
      formulas: [
        FC_RATE_FORMULA,
      ],
      callout: 'NovaBurst: 2 ÷ 100 = 2%.\nBeatLegend: 65 ÷ 100 = 65%.',
    }),
    contentStaggerCount: 4,
  },
  {
    id: 'metric-info-fcrate-experimental',
    version: 3,
    title: 'firstRun.leaderboards.metricInfo.fcrate.experimental.title',
    description: 'firstRun.leaderboards.metricInfo.fcrate.experimental.description',
    render: () => createElement(MetricInfoSlide, {
      paragraphs: [
        'FC\'ing an easy song counts the same as an extremely hard one. The metric does not distinguish between charts that are unplayed and charts that were played without an FC; only completed FCs change the numerator.',
        'A near-miss doesn\'t count — missing one note out of 2,000 gets the same "no FC" result as missing hundreds.',
      ],
    }),
    contentStaggerCount: 2,
  },
];

/* ── Max Score % ── */

export const MAX_SCORE_EXAMPLE_VALUES = [
  { valueLabel: '95,210 / 100,000 → 95.2%', valueLines: ['95,210 / 100,000', '95.2%'] },
  { valueLabel: '87,400 / 92,500 → 94.5%', valueLines: ['87,400 / 92,500', '94.5%'] },
  { valueLabel: '102,800 / 98,000 → 104.9%', valueLines: ['102,800 / 98,000', '104.9%'] },
] as const;

function MaxScoreHowDemo() {
  const buildRows = useCallback(
    (songs: { albumArt?: string; title: string; artist: string }[]) => MAX_SCORE_EXAMPLE_VALUES.map((value, index) => ({
      ...songs[index]!,
      valueLabel: value.valueLabel,
      valueLines: [...value.valueLines],
    })),
    [],
  );

  return createElement(SongDemoSlide, {
    paragraphs: [
      'For each song, a tool called CHOpt computes the highest theoretically possible score. Scores above 105% of that maximum are excluded as invalid; eligible per-song ratios are capped at 105% before averaging.',
    ],
    buildRows,
    songSummary: 'Your eligible-song average: 98.2%',
  });
}

const maxScoreSlides: FirstRunSlideDef[] = [
  {
    id: 'metric-info-maxscore-how',
    version: 3,
    title: 'firstRun.leaderboards.metricInfo.maxscore.how.title',
    description: 'firstRun.leaderboards.metricInfo.maxscore.how.description',
    render: () => createElement(MaxScoreHowDemo),
    contentStaggerCount: 3,
  },
  {
    id: 'metric-info-maxscore-experience',
    version: 2,
    title: 'firstRun.leaderboards.metricInfo.maxscore.experience.title',
    description: 'firstRun.leaderboards.metricInfo.maxscore.experience.description',
    render: () => createElement(MetricInfoSlide, {
      paragraphs: [
        'A player who scored 99% on two songs has only a few scores compared with one who averages 94% across a hundred. The same score-count rule applies as the other metrics.',
      ],
      cards: [
        {
          label: 'After 3 songs',
          entries: [
            { rank: 55, displayName: 'StageKnight', ratingLabel: credibilityRatingLabel(0.85, 3) },
            { rank: 56, displayName: 'You', ratingLabel: credibilityRatingLabel(0.71, 3), isPlayer: true },
            { rank: 57, displayName: 'FretBlaze', ratingLabel: credibilityRatingLabel(0.64, 3) },
          ],
          highlight: 'Few scores — ranking is cautious',
        },
        {
          label: 'After 100 songs',
          entries: [
            { rank: 3, displayName: 'GoldStreak', ratingLabel: credibilityRatingLabel(0.945, 100) },
            { rank: 4, displayName: 'NoteHunter', ratingLabel: credibilityRatingLabel(0.942, 100) },
            { rank: 5, displayName: 'You', ratingLabel: credibilityRatingLabel(0.94, 100), isPlayer: true },
          ],
          highlight: 'Consistent accuracy across many songs',
        },
      ],
    }),
    contentStaggerCount: 3,
  },
  {
    id: 'metric-info-maxscore-hood-cap',
    version: 3,
    title: 'firstRun.leaderboards.metricInfo.maxscore.hoodCap.title',
    description: 'firstRun.leaderboards.metricInfo.maxscore.hoodCap.description',
    render: () => createElement(MetricInfoSlide, {
      layout: 'formula',
      paragraphs: [],
      formulas: [
        '\\bar{s} = \\text{avg}\\!\\left(\\min\\!\\left(\\frac{\\text{score}_i}{\\text{max}_i},\\; 1.05\\right)\\right)',
      ],
      callout: 'The raw average includes only valid scores on charts with a computed maximum.',
    }),
    contentStaggerCount: 1,
  },
  {
    id: 'metric-info-maxscore-hood-rating',
    version: 2,
    title: 'firstRun.leaderboards.metricInfo.maxscore.hoodRating.title',
    description: 'firstRun.leaderboards.metricInfo.maxscore.hoodRating.description',
    render: () => createElement(MetricInfoSlide, {
      layout: 'formula',
      paragraphs: [],
      formulas: [
        '\\text{Rating} = \\frac{n \\cdot \\bar{s} + 50 \\cdot 0.5}{n + 50}',
      ],
      callout: 'n counts all valid scores on the instrument, including scores on charts whose computed maximum is not available yet.',
    }),
    contentStaggerCount: 1,
  },
  {
    id: 'metric-info-maxscore-experimental',
    version: 2,
    title: 'firstRun.leaderboards.metricInfo.maxscore.experimental.title',
    description: 'firstRun.leaderboards.metricInfo.maxscore.experimental.description',
    render: () => createElement(MetricInfoSlide, {
      paragraphs: [
        'The computed maximum score for a song can occasionally be slightly off — advanced techniques like squeezing can push scores higher than the calculated ceiling.',
        'Newly added songs may not have a computed maximum yet. They are omitted from the raw per-song average, but a valid score on them still counts toward the credibility adjustment until the maximum is available.',
      ],
    }),
    contentStaggerCount: 2,
  },
];

/* ── Lookup ── */

const METRIC_SLIDES: Record<ExperimentalRankingMetric, FirstRunSlideDef[]> = {
  adjusted: adjustedSlides,
  weighted: weightedSlides,
  fcrate: fcRateSlides,
  maxscore: maxScoreSlides,
};

/** Get the FRE slides for a specific ranking metric. */
export function getMetricInfoSlides(metric: ExperimentalRankingMetric): FirstRunSlideDef[] {
  return METRIC_SLIDES[metric];
}
