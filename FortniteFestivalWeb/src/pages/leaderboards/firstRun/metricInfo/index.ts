import { createElement } from 'react';
import type { FirstRunSlideDef } from '../../../../firstRun/types';
import type { RankingMetric } from '@festival/core/api/serverTypes';
import MetricInfoSlide from './MetricInfoSlide';

/* ── Adjusted Skill ── */

const adjustedSlides: FirstRunSlideDef[] = [
  {
    id: 'metric-info-adjusted-what',
    version: 1,
    title: 'firstRun.leaderboards.metricInfo.adjusted.what.title',
    description: 'firstRun.leaderboards.metricInfo.adjusted.what.description',
    render: () => createElement(MetricInfoSlide, {
      paragraphs: [
        'For every song you\'ve played, your rank is compared against every other player who also played it. This gives a percentile — e.g. ranking 10th out of 1,000 entries is the 1st percentile.',
        'Your Adjusted Skill rating is the average of these percentiles across all your songs. Lower is better — a perfect player would be near 0%.',
      ],
    }),
    contentStaggerCount: 2,
  },
  {
    id: 'metric-info-adjusted-formula',
    version: 1,
    title: 'firstRun.leaderboards.metricInfo.adjusted.formula.title',
    description: 'firstRun.leaderboards.metricInfo.adjusted.formula.description',
    render: () => createElement(MetricInfoSlide, {
      paragraphs: [
        'To prevent a player with one perfect score from dominating, a Bayesian credibility adjustment is applied:',
      ],
      formulas: [
        '\\text{Rating} = \\frac{n \\cdot \\bar{x} + m \\cdot C}{n + m}',
        '\\text{where } m = 50,\\; C = 0.5',
      ],
    }),
    contentStaggerCount: 3,
  },
  {
    id: 'metric-info-adjusted-experimental',
    version: 1,
    title: 'firstRun.leaderboards.metricInfo.adjusted.experimental.title',
    description: 'firstRun.leaderboards.metricInfo.adjusted.experimental.description',
    render: () => createElement(MetricInfoSlide, {
      paragraphs: [
        'The credibility threshold (m = 50) is a tuning parameter — there\'s no objectively "correct" value. It determines how many songs you need before your rating stabilizes.',
        'Additionally, percentile-based ranking treats all songs equally regardless of difficulty. A top rank on a challenging song counts the same as an easy one.',
      ],
    }),
    contentStaggerCount: 2,
  },
];

/* ── Weighted ── */

const weightedSlides: FirstRunSlideDef[] = [
  {
    id: 'metric-info-weighted-what',
    version: 1,
    title: 'firstRun.leaderboards.metricInfo.weighted.what.title',
    description: 'firstRun.leaderboards.metricInfo.weighted.what.description',
    render: () => createElement(MetricInfoSlide, {
      paragraphs: [
        'Like Adjusted Skill, this uses your percentile on each song — but songs with more leaderboard entries count more heavily.',
        'The intuition: performing well on a popular, competitive song is a stronger signal of skill than an obscure one with few players.',
      ],
    }),
    contentStaggerCount: 2,
  },
  {
    id: 'metric-info-weighted-formula',
    version: 1,
    title: 'firstRun.leaderboards.metricInfo.weighted.formula.title',
    description: 'firstRun.leaderboards.metricInfo.weighted.formula.description',
    render: () => createElement(MetricInfoSlide, {
      paragraphs: [
        'Each song\'s percentile is weighted by the logarithm of its entry count, then the same Bayesian adjustment is applied:',
      ],
      formulas: [
        '\\text{Weight}_i = \\log_2(\\text{entries}_i)',
        '\\text{Rating} = \\frac{n \\cdot \\frac{\\sum p_i \\cdot w_i}{\\sum w_i} + m \\cdot C}{n + m}',
      ],
    }),
    contentStaggerCount: 3,
  },
  {
    id: 'metric-info-weighted-experimental',
    version: 1,
    title: 'firstRun.leaderboards.metricInfo.weighted.experimental.title',
    description: 'firstRun.leaderboards.metricInfo.weighted.experimental.description',
    render: () => createElement(MetricInfoSlide, {
      paragraphs: [
        'The choice to weight by popularity is subjective. A song with 10,000 entries isn\'t necessarily harder than one with 100 — it may just be more well-known.',
        'The logarithmic scale softens extreme differences, but the fundamental assumption that "more popular = more meaningful" is debatable.',
      ],
    }),
    contentStaggerCount: 2,
  },
];

/* ── FC Rate ── */

const fcRateSlides: FirstRunSlideDef[] = [
  {
    id: 'metric-info-fcrate-what',
    version: 1,
    title: 'firstRun.leaderboards.metricInfo.fcrate.what.title',
    description: 'firstRun.leaderboards.metricInfo.fcrate.what.description',
    render: () => createElement(MetricInfoSlide, {
      paragraphs: [
        'FC Rate measures the percentage of your played songs where you achieved a Full Combo — hitting every note without breaking your streak.',
        'Like other metrics, a Bayesian adjustment prevents players with very few songs from ranking unrealistically high:',
      ],
      formulas: [
        '\\text{FC Rate} = \\frac{n \\cdot \\frac{\\text{FCs}}{\\text{Songs}} + m \\cdot C}{n + m}',
      ],
    }),
    contentStaggerCount: 3,
  },
  {
    id: 'metric-info-fcrate-experimental',
    version: 1,
    title: 'firstRun.leaderboards.metricInfo.fcrate.experimental.title',
    description: 'firstRun.leaderboards.metricInfo.fcrate.experimental.description',
    render: () => createElement(MetricInfoSlide, {
      paragraphs: [
        'FC Rate doesn\'t account for song difficulty. A player who only FCs easy songs will rank higher than one who plays — and sometimes misses notes on — extremely hard songs.',
        'It also doesn\'t reflect how close you were to an FC. Missing one note on a 2,000-note chart counts the same as missing hundreds.',
      ],
    }),
    contentStaggerCount: 2,
  },
];

/* ── Max Score % ── */

const maxScoreSlides: FirstRunSlideDef[] = [
  {
    id: 'metric-info-maxscore-what',
    version: 1,
    title: 'firstRun.leaderboards.metricInfo.maxscore.what.title',
    description: 'firstRun.leaderboards.metricInfo.maxscore.what.description',
    render: () => createElement(MetricInfoSlide, {
      paragraphs: [
        'Max Score % measures how close your scores are to the theoretical maximum for each song. The maximum is computed by CHOpt, a tool that analyzes each song\'s note chart to find the optimal Star Power activation path.',
        'Your rating is the average of (your score ÷ CHOpt max) across all your played songs, capped at 105% per song.',
      ],
    }),
    contentStaggerCount: 2,
  },
  {
    id: 'metric-info-maxscore-formula',
    version: 1,
    title: 'firstRun.leaderboards.metricInfo.maxscore.formula.title',
    description: 'firstRun.leaderboards.metricInfo.maxscore.formula.description',
    render: () => createElement(MetricInfoSlide, {
      paragraphs: [
        'The Bayesian adjustment is applied after averaging:',
      ],
      formulas: [
        '\\bar{s} = \\text{avg}\\!\\left(\\min\\!\\left(\\frac{\\text{score}_i}{\\text{max}_i},\\; 1.05\\right)\\right)',
        '\\text{Rating} = \\frac{n \\cdot \\bar{s} + m \\cdot C}{n + m}',
      ],
    }),
    contentStaggerCount: 3,
  },
  {
    id: 'metric-info-maxscore-experimental',
    version: 1,
    title: 'firstRun.leaderboards.metricInfo.maxscore.experimental.title',
    description: 'firstRun.leaderboards.metricInfo.maxscore.experimental.description',
    render: () => createElement(MetricInfoSlide, {
      paragraphs: [
        'CHOpt\'s computed maximum can occasionally be inaccurate — some songs have edge cases where the actual achievable score differs from the computed optimum.',
        'Newly added songs may temporarily lack CHOpt data, excluding them from this metric entirely until the next path generation pass.',
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
