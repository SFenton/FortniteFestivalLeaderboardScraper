

export type ChangelogSection = {
  title: string;
  items: string[];
};

export type ChangelogEntry = {
  sections: ChangelogSection[];
};

export const changelog: ChangelogEntry[] = [
  {
    sections: [
      {
        title: 'Rivals',
        items: [
          'Added a full Rivals experience with rival lists, rival detail pages, category views, and head-to-head rivalry pages.',
          'Added global leaderboard rivals and song-gap comparisons so you can see where you are gaining or losing ground.',
          'Added profile navigation from rival views.',
        ],
      },
      {
        title: 'Bands',
        items: [
          'Added band and team leaderboard views, selected-band stats, and combo-specific band detail stats.',
          'Added selected-band song rows, selected-band leaderboard footers, and selected bandmate solo rows.',
          'Added exact band instrument filters, persisted band filter state, and band intensity sort modes.',
        ],
      },
      {
        title: 'Rankings And Statistics',
        items: [
          'Added overall player rankings, all-combo rankings, and per-metric rank cards.',
          'Added rank history charts, including per-instrument charts on player pages.',
          'Added raw skill values, Bayesian-adjusted ranking views, percentile pills, and clearer rank and score displays.',
        ],
      },
      {
        title: 'Songs And Song Details',
        items: [
          'Added song intensity, duration, difficulty metadata, and Max Score Diff sorting.',
          'Added support for Karaoke, Pro Drums, Pro Drums + Cymbals, and Tap Vocals.',
          'Added CHOpt warnings, path visualization improvements, and better instrument-aware empty states.',
        ],
      },
      {
        title: 'Item Shop',
        items: [
          'Added a Jam Tracks Item Shop page.',
          'Added Item Shop filtering on Songs and contextual actions for songs currently available in the shop.',
          'Improved shop cards, images, view toggles, and mobile behavior.',
        ],
      },
      {
        title: 'Improvement Notifications',
        items: [
          'Added score-improvement notifications with click-through navigation.',
          'Improved notification copy, freshness indicators, read-state behavior, and profile-switch transitions.',
          'Reduced notification noise by grouping related improvements.',
        ],
      },
      {
        title: 'Search And Navigation',
        items: [
          'Improved global search and profile search, especially on mobile.',
          'Added shared quick links across major pages.',
          'Improved page transitions, scroll restoration, and return-position behavior.',
        ],
      },
      {
        title: 'First-Run Guides',
        items: [
          'Added guided first-run experiences across Songs, Song Info, Player History, Statistics, Shop, and related flows.',
          'Added Settings controls for replaying guides.',
          'Improved guide sequencing, transitions, demos, and mobile layouts.',
        ],
      },
      {
        title: 'Mobile And PWA',
        items: [
          'Refined the mobile shell, header actions, dock controls, search modal, and FAB behavior.',
          'Added PWA app icons.',
          'Improved iOS safe-area handling, mobile keyboard behavior, and empty-state positioning.',
        ],
      },
      {
        title: 'Leaderboard Polish',
        items: [
          'Improved compact leaderboard rows, rank columns, headers, subtitles, score cards, and percentile displays.',
          'Added Epic entry counts and tracked score counts where useful.',
          'Improved loading, stagger, and spinner behavior so leaderboard pages feel steadier.',
        ],
      },
      {
        title: 'Settings And Personalization',
        items: [
          'Added service info and guide replay controls in Settings.',
          'Improved profile deselection, profile search, selected-profile transitions, and Settings mobile layout.',
          'Added better controls for ranking display, filters, visible instruments, and shop visibility.',
        ],
      },
      {
        title: 'Faster Data Loading',
        items: [
          'Improved loading speed for leaderboards, player profiles, song details, rankings, statistics, and shop data.',
          'Reduced large profile responses so player pages feel lighter and faster.',
          'Added smarter caching so common pages stay responsive after scrapes and during busy periods.',
        ],
      },
      {
        title: 'More Accurate Rankings',
        items: [
          'Improved ranking calculations across total score, full combos, skill values, percentiles, instruments, and band/team views.',
          'Fixed cases where valid scores could be excluded or undercounted by ranking thresholds.',
          'Improved rival and leaderboard-rival calculations so comparisons update more reliably and reflect the latest available data.',
        ],
      },
    ],
  },
];

/** Simple hash of the changelog content for change detection. */
export function changelogHash(): string {
  const str = JSON.stringify(changelog);
  let hash = 0;
  for (let i = 0; i < str.length; i++) {
    const ch = str.charCodeAt(i);
    hash = ((hash << 5) - hash) + ch;
    hash |= 0;
  }
  return hash.toString(36);
}
