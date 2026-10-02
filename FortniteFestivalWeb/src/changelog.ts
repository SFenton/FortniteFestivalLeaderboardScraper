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
        title: 'APP',
        items: [
          'Added in-app bug reports and feature requests from Settings when feedback is enabled.',
        ],
      },
      {
        title: 'SONGS',
        items: [
          'Added Year, Duration, Item Shop, and Double Bass filters.',
          'Song filters are now available without selecting a profile or band.',
          'General filter options can be toggled independently.',
          'Has FC sorting now requires a selected profile.',
        ],
      },
      {
        title: 'CHARTS',
        items: [
          'Maximum scores now update automatically when song charts change.',
          'Fixed chart generation for The Other Promise.',
          'Corrected Pro Drums maximum scores for I Wanna Get Better and Gangnam Style.',
        ],
      },
    ],
  },
];
