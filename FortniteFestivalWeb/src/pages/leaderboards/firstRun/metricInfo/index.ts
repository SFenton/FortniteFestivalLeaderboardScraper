import { createElement, useCallback } from 'react';
import type { FirstRunSlideDef } from '../../../../firstRun/types';
import type { RankingMetric } from '@festival/core/api/serverTypes';
import MetricInfoSlide from './MetricInfoSlide';
import SongDemoSlide from './SongDemoSlide';
import FcRateHowDemo from './FcRateHowDemo';

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
            { rank: 52, displayName: 'FretPhenom', ratingLabel: '45.8%' },
            { rank: 53, displayName: 'NeonPick', ratingLabel: '46.0%' },
            { rank: 54, displayName: 'You', ratingLabel: '46.2%', isPlayer: true },
            { rank: 55, displayName: 'DrumSurge', ratingLabel: '46.5%' },
          ],
          highlight: 'Few scores — ranking is cautious',
        },
        {
          label: 'After 100 scores',
          entries: [
            { rank: 7, displayName: 'BeatLegend', ratingLabel: '18.6%' },
            { rank: 8, displayName: 'TopClutch', ratingLabel: '18.8%' },
            { rank: 9, displayName: 'You', ratingLabel: '19.0%', isPlayer: true },
            { rank: 10, displayName: 'ComboKing', ratingLabel: '19.3%' },
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

function WeightedHowDemo() {
  const buildRows = useCallback((songs: { albumArt?: string; title: string; artist: string }[]) => [
    { ...songs[0]!, valueLabel: '12,000 players · Top 3%', valueLines: ['12,000 players', 'Top 3%'] },
    { ...songs[1]!, valueLabel: '80 players · Top 3%', valueLines: ['80 players', 'Top 3%'] },
  ], []);

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
            { rank: 38, displayName: 'StageKnight', ratingLabel: '44.8%' },
            { rank: 39, displayName: 'You', ratingLabel: '45.1%', isPlayer: true },
            { rank: 40, displayName: 'RhythmEdge', ratingLabel: '45.5%' },
          ],
          highlight: 'Few scores — ranking is cautious',
        },
        {
          label: 'After 100 scores',
          entries: [
            { rank: 4, displayName: 'GoldStreak', ratingLabel: '16.7%' },
            { rank: 5, displayName: 'NoteHunter', ratingLabel: '17.0%' },
            { rank: 6, displayName: 'You', ratingLabel: '17.3%', isPlayer: true },
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

const fcRateSlides: FirstRunSlideDef[] = [
  {
    id: 'metric-info-fcrate-how',
    version: 2,
    title: 'firstRun.leaderboards.metricInfo.fcrate.how.title',
    description: 'firstRun.leaderboards.metricInfo.fcrate.how.description',
    render: () => createElement(FcRateHowDemo),
    contentStaggerCount: 3,
  },
  {
    id: 'metric-info-fcrate-experience',
    version: 2,
    title: 'firstRun.leaderboards.metricInfo.fcrate.experience.title',
    description: 'firstRun.leaderboards.metricInfo.fcrate.experience.description',
    render: () => createElement(MetricInfoSlide, {
      paragraphs: [
        'Without the experience boost, anyone could play two easy songs, FC both, and claim a perfect 100% rate.',
      ],
      cards: [
        {
          label: 'NovaBurst · 2 songs, 2 FCs',
          entries: [
            { rank: 40, displayName: 'SonicRush', ratingLabel: '51.1%' },
            { rank: 41, displayName: 'NovaBurst', ratingLabel: '50.9%', isPlayer: true },
            { rank: 42, displayName: 'DeepGroove', ratingLabel: '50.7%' },
          ],
          highlight: 'Raw: 100% — but adjusted to ~51%',
        },
        {
          label: 'BeatLegend · 100 songs, 65 FCs',
          entries: [
            { rank: 6, displayName: 'VocalStorm', ratingLabel: '48.6%' },
            { rank: 7, displayName: 'TopClutch', ratingLabel: '48.5%' },
            { rank: 8, displayName: 'BeatLegend', ratingLabel: '48.3%', isPlayer: true },
          ],
          highlight: 'Raw: 65% — consistent across many songs',
        },
      ],
      callout: 'Sustained consistency across many songs counts more than a perfect record on just a few.',
    }),
    contentStaggerCount: 4,
  },
  {
    id: 'metric-info-fcrate-hood',
    version: 2,
    title: 'firstRun.leaderboards.metricInfo.fcrate.hood.title',
    description: 'firstRun.leaderboards.metricInfo.fcrate.hood.description',
    render: () => createElement(MetricInfoSlide, {
      paragraphs: [
        'The formula applies the same score-count rule used by all metrics: your real FC rate counts more as you add more scores on the instrument.',
      ],
      formulas: [
        '\\text{Rating} = \\frac{n \\cdot \\frac{\\text{FCs}}{\\text{Songs}} + 50 \\cdot 0.5}{n + 50}',
      ],
      callout: 'NovaBurst (2/2): (2 × 1.0 + 50 × 0.5) ÷ 52 ≈ 52%.\nBeatLegend (65/100): (100 × 0.65 + 50 × 0.5) ÷ 150 ≈ 60%.',
    }),
    contentStaggerCount: 4,
  },
  {
    id: 'metric-info-fcrate-experimental',
    version: 2,
    title: 'firstRun.leaderboards.metricInfo.fcrate.experimental.title',
    description: 'firstRun.leaderboards.metricInfo.fcrate.experimental.description',
    render: () => createElement(MetricInfoSlide, {
      paragraphs: [
        'FC\'ing an easy song counts the same as an extremely hard one. A player who only attempts easy charts will have a higher FC rate than an ambitious player who tackles everything.',
        'A near-miss doesn\'t count — missing one note out of 2,000 gets the same "no FC" result as missing hundreds.',
      ],
    }),
    contentStaggerCount: 2,
  },
];

/* ── Max Score % ── */

function MaxScoreHowDemo() {
  const buildRows = useCallback((songs: { albumArt?: string; title: string; artist: string }[]) => [
    { ...songs[0]!, valueLabel: '95,210 / 100,000 → 95.2%', valueLines: ['95,210 / 100,000', '95.2%'] },
    { ...songs[1]!, valueLabel: '87,400 / 92,500 → 94.5%', valueLines: ['87,400 / 92,500', '94.5%'] },
    { ...songs[2]!, valueLabel: '103,200 / 98,000 → 105% cap', valueLines: ['103,200 / 98,000', '105% cap'] },
  ], []);

  return createElement(SongDemoSlide, {
    paragraphs: [
      'For each song, a tool called CHOpt computes the highest theoretically possible score. Max Score % is how close you get to that ceiling, averaged across all your songs. Scores above the max are capped at 105%.',
    ],
    buildRows,
    songSummary: 'Your average: 94.9% (third song capped)',
  });
}

const maxScoreSlides: FirstRunSlideDef[] = [
  {
    id: 'metric-info-maxscore-how',
    version: 2,
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
            { rank: 55, displayName: 'StageKnight', ratingLabel: '52.0%' },
            { rank: 56, displayName: 'You', ratingLabel: '51.2%', isPlayer: true },
            { rank: 57, displayName: 'FretBlaze', ratingLabel: '50.8%' },
          ],
          highlight: 'Few scores — ranking is cautious',
        },
        {
          label: 'After 100 songs',
          entries: [
            { rank: 3, displayName: 'GoldStreak', ratingLabel: '94.5%' },
            { rank: 4, displayName: 'NoteHunter', ratingLabel: '94.3%' },
            { rank: 5, displayName: 'You', ratingLabel: '94.1%', isPlayer: true },
          ],
          highlight: 'Consistent accuracy across many songs',
        },
      ],
    }),
    contentStaggerCount: 3,
  },
  {
    id: 'metric-info-maxscore-hood-cap',
    version: 2,
    title: 'firstRun.leaderboards.metricInfo.maxscore.hoodCap.title',
    description: 'firstRun.leaderboards.metricInfo.maxscore.hoodCap.description',
    render: () => createElement(MetricInfoSlide, {
      layout: 'formula',
      paragraphs: [],
      formulas: [
        '\\bar{s} = \\text{avg}\\!\\left(\\min\\!\\left(\\frac{\\text{score}_i}{\\text{max}_i},\\; 1.05\\right)\\right)',
      ],
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
        'Newly added songs may not have a computed maximum yet, so they\'re temporarily excluded from this metric until the next processing pass.',
      ],
    }),
    contentStaggerCount: 2,
  },
];

/* ── Lookup ── */

const METRIC_SLIDES: Record<string, FirstRunSlideDef[]> = {
  adjusted: adjustedSlides,
  weighted: weightedSlides,
  fcrate: fcRateSlides,
  maxscore: maxScoreSlides,
};

/** Get the FRE slides for a specific ranking metric. */
export function getMetricInfoSlides(metric: RankingMetric): FirstRunSlideDef[] {
  return METRIC_SLIDES[metric] ?? [];
}
