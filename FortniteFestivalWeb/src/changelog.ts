

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
          'Current season badges invert colors from old seasons to be more visually distinct.',
          'When profile is deselected or filters change, on desktop mode, instrument icon and filter button animates shut.',
          'Bar chart borders animate in.',
        ],
      },
      {
        title: 'General Improvements',
        items: [
          'The web app has been completely refactored and modularized. Users should hopefully see nothing, but you may experience performance improvements.',
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
