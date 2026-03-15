

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
        title: 'New Features',
        items: [
          'Added "Select Player Profile" button on desktop player pages.',
          'Clicking stat cards on the Statistics page now navigates to filtered song lists.',
          'New suggestion categories: stale songs and percentile improvements.',
          'Changelog modal that appears when new updates are available.',
          'Song year now displayed across all song cards and rows.',
        ],
      },
      {
        title: 'Filters & Sorting',
        items: [
          'Missing Score/FC and Has Score/FC filters are now per-instrument, with global override toggles.',
          'Percentile sort now properly breaks ties.',
        ],
      },
      {
        title: 'Player Profiles',
        items: [
          'Selecting a player profile now navigates to Statistics automatically.',
          'Players without display names now show "Unknown User" instead of blank.',
          'Player name now visible on the Statistics page on desktop.',
        ],
      },
      {
        title: 'Visual Improvements',
        items: [
          'Album art shows a spinner while loading, then fades in smoothly.',
          'Animated background fades in when ready.',
          'Profile icon animates when selecting or deselecting a player on desktop.',
          'Stagger animations now complete instantly on scroll for a snappier feel.',
          'Increased default modal size on desktop.',
          'Search text for player selection is now larger and easier to read.',
          'Suggestions filter button is now a pill on desktop.',
          'Updated modal header names to be more descriptive.',
          'Stars now display correctly on mobile suggestion cards.',
          'Statistics cards for a player display an icon if they are clickable to show more data.',
        ],
      },
      {
        title: 'Bug Fixes',
        items: [
          'Fixed FAB menu not closing when tapping on FAB button.',
          'Fixed song search text not persisting when navigating away and returning.',
          'Fixed empty score history card appearing when no history exists.',
          'Removed redundant "Is FC" toggle from Settings.',
          'Fixed player page not re-staggering when navigating between different players.',
          'Fixed stat cards not prompting profile switch confirmation on other players\' pages.',
          'Fixed top summary cards not respecting instrument visibility filters.',
          'Fixed auto-scroll triggering incorrectly when arriving from the Suggestions page.',
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
