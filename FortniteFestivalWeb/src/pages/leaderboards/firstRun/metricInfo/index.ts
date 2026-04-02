import { createElement, useCallback } from 'react';
import type { FirstRunSlideDef } from '../../../../firstRun/types';
import type { RankingMetric } from '@festival/core/api/serverTypes';
import MetricInfoSlide from './MetricInfoSlide';
import SongDemoSlide from './SongDemoSlide';
import FcRateHowDemo from './FcRateHowDemo';

/* ── Adjusted Skill ── */

function AdjustedHowDemo() {
  const buildRows = useCallback((songs: { albumArt?: string; title: string; artist: string }[]) => [
    { ...songs[0]!, valueLabel: '#10 of 1,000 → Top 1.0%' },
    { ...songs[1]!, valueLabel: '#50 of 500 → Top 10.0%' },
    { ...songs[2]!, valueLabel: '#5 of 200 → Top 2.5%' },
  ], []);

  return createElement(SongDemoSlide, {
    paragraphs: [
      'Your rank on each song is turned into a percentile — how you compare to everyone else who played it. Adjusted Skill averages these across all your songs.',
    ],
    buildRows,
    songSummary: 'Your average: 4.5% — lower is better',
  });
}

const adjustedSlides: FirstRunSlideDef[] = [
  {
    id: 'metric-info-adjusted-how',
    version: 2,
    title: 'firstRun.leaderboards.metricInfo.adjusted.how.title',
    description: 'firstRun.leaderboards.metricInfo.adjusted.how.description',
    render: () => createElement(AdjustedHowDemo),
    contentStaggerCount: 3,
  },
  {
    id: 'metric-info-adjusted-experience',
    version: 2,
    title: 'firstRun.leaderboards.metricInfo.adjusted.experience.title',
    description: 'firstRun.leaderboards.metricInfo.adjusted.experience.description',
    render: () => createElement(MetricInfoSlide, {
      paragraphs: [
        'With few songs played, the system isn\'t sure how good you are — so it keeps your rating close to 50% (average). As you play more, your rating reflects your true skill.',
      ],
      cards: [
        {
          label: 'After 5 songs',
          entries: [
            { rank: 52, displayName: 'FretPhenom' },
            { rank: 53, displayName: 'NeonPick' },
            { rank: 54, displayName: 'You', isPlayer: true },
            { rank: 55, displayName: 'DrumSurge' },
          ],
          highlight: 'Still near average — unproven',
        },
        {
          label: 'After 100 songs',
          entries: [
            { rank: 7, displayName: 'BeatLegend' },
            { rank: 8, displayName: 'TopClutch' },
            { rank: 9, displayName: 'You', isPlayer: true },
            { rank: 10, displayName: 'ComboKing' },
          ],
          highlight: 'True skill shines through',
        },
      ],
      callout: 'Everyone starts near 50%. Each song you play pulls your rating closer to what you\'ve actually earned.',
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
        'The formula blends your actual average with a 50% starting point. The more songs you play, the less the starting point matters.',
      ],
      formulas: [
        '\\text{Rating} = \\frac{n \\cdot \\bar{x} + 50 \\cdot 0.5}{n + 50}',
      ],
      callout: 'n = songs played, x̄ = your real average percentile.\nAfter 5 songs: (5 × 0.03 + 50 × 0.5) ÷ 55 ≈ 46%.\nAfter 100 songs: (100 × 0.03 + 50 × 0.5) ÷ 150 ≈ 19%.',
    }),
    contentStaggerCount: 4,
  },
  {
    id: 'metric-info-adjusted-experimental',
    version: 2,
    title: 'firstRun.leaderboards.metricInfo.adjusted.experimental.title',
    description: 'firstRun.leaderboards.metricInfo.adjusted.experimental.description',
    render: () => createElement(MetricInfoSlide, {
      paragraphs: [
        'All songs count equally — finishing top 1% on an easy song counts the same as a hard one.',
        'The "experience threshold" of 50 songs is a tuning choice, not a scientifically proven number. A different threshold would shift everyone\'s rankings.',
      ],
    }),
    contentStaggerCount: 2,
  },
];

/* ── Weighted ── */

function WeightedHowDemo() {
  const buildRows = useCallback((songs: { albumArt?: string; title: string; artist: string }[]) => [
    { ...songs[0]!, valueLabel: '12,000 players · Top 3%' },
    { ...songs[1]!, valueLabel: '80 players · Top 3%' },
  ], []);

  return createElement(SongDemoSlide, {
    paragraphs: [
      'Like Adjusted Skill, this uses your percentile on each song — but songs with more players on the leaderboard carry more weight.',
    ],
    buildRows,
    maxSongs: 2,
    callout: 'Same percentile, but the popular song counts much more — doing well where competition is fierce is a stronger signal of skill.',
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
        'New players start near 50% and converge toward their true rating as they play more — the same experience boost as Adjusted Skill.',
      ],
      cards: [
        {
          label: 'After 5 songs',
          entries: [
            { rank: 38, displayName: 'StageKnight' },
            { rank: 39, displayName: 'You', isPlayer: true },
            { rank: 40, displayName: 'RhythmEdge' },
          ],
          highlight: 'Still near average',
        },
        {
          label: 'After 100 songs',
          entries: [
            { rank: 4, displayName: 'GoldStreak' },
            { rank: 5, displayName: 'NoteHunter' },
            { rank: 6, displayName: 'You', isPlayer: true },
          ],
          highlight: 'True skill shines through',
        },
      ],
    }),
    contentStaggerCount: 3,
  },
  {
    id: 'metric-info-weighted-hood',
    version: 2,
    title: 'firstRun.leaderboards.metricInfo.weighted.hood.title',
    description: 'firstRun.leaderboards.metricInfo.weighted.hood.description',
    render: () => createElement(MetricInfoSlide, {
      paragraphs: [
        'Each song\'s percentile is multiplied by its weight before averaging. Then the same experience adjustment applies.',
      ],
      formulas: [
        '\\text{Weight}_i = \\log_2(\\text{players}_i)',
        '\\text{Rating} = \\frac{n \\cdot \\overline{w} + 50 \\cdot 0.5}{n + 50}',
      ],
      callout: 'A song with 10,000 players has ~13× the weight of one with 10 players. The logarithm keeps things from getting too extreme.',
    }),
    contentStaggerCount: 4,
  },
  {
    id: 'metric-info-weighted-experimental',
    version: 2,
    title: 'firstRun.leaderboards.metricInfo.weighted.experimental.title',
    description: 'firstRun.leaderboards.metricInfo.weighted.experimental.description',
    render: () => createElement(MetricInfoSlide, {
      paragraphs: [
        'More players doesn\'t necessarily mean harder. A viral song might have 10,000 casual players, making it easier to rank highly.',
        'The logarithmic weighting softens extremes but is still a subjective choice — a different scale would produce different rankings.',
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
          highlight: 'Raw: 65% — proven consistency',
        },
      ],
      callout: 'Sustained consistency across many songs is more trustworthy than a perfect record on just a few.',
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
        'The formula is the same experience adjustment used by all metrics — blending your real FC rate with a 50% starting point.',
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
    { ...songs[0]!, valueLabel: '95,210 / 100,000 → 95.2%' },
    { ...songs[1]!, valueLabel: '87,400 / 92,500 → 94.5%' },
    { ...songs[2]!, valueLabel: '103,200 / 98,000 → 105% cap' },
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
        'A player who scored 99% on two songs isn\'t as proven as one who averages 94% across a hundred. Same experience boost as the other metrics.',
      ],
      cards: [
        {
          label: 'After 3 songs',
          entries: [
            { rank: 55, displayName: 'StageKnight', ratingLabel: '52.0%' },
            { rank: 56, displayName: 'You', ratingLabel: '51.2%', isPlayer: true },
            { rank: 57, displayName: 'FretBlaze', ratingLabel: '50.8%' },
          ],
          highlight: 'Still near 50% — unproven',
        },
        {
          label: 'After 100 songs',
          entries: [
            { rank: 3, displayName: 'GoldStreak', ratingLabel: '94.5%' },
            { rank: 4, displayName: 'NoteHunter', ratingLabel: '94.3%' },
            { rank: 5, displayName: 'You', ratingLabel: '94.1%', isPlayer: true },
          ],
          highlight: 'True accuracy proven',
        },
      ],
    }),
    contentStaggerCount: 3,
  },
  {
    id: 'metric-info-maxscore-hood',
    version: 2,
    title: 'firstRun.leaderboards.metricInfo.maxscore.hood.title',
    description: 'firstRun.leaderboards.metricInfo.maxscore.hood.description',
    render: () => createElement(MetricInfoSlide, {
      paragraphs: [
        'Scores are capped at 105% per song to limit the impact of any inaccuracies in the computed maximum.',
      ],
      formulas: [
        '\\bar{s} = \\text{avg}\\!\\left(\\min\\!\\left(\\frac{\\text{score}_i}{\\text{max}_i},\\; 1.05\\right)\\right)',
        '\\text{Rating} = \\frac{n \\cdot \\bar{s} + 50 \\cdot 0.5}{n + 50}',
      ],
      callout: 'After 3 songs (avg 95%): (3 × 0.95 + 50 × 0.5) ÷ 53 ≈ 53%.\nAfter 100 songs (avg 95%): (100 × 0.95 + 50 × 0.5) ÷ 150 ≈ 80%.',
    }),
    contentStaggerCount: 4,
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
