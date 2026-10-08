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
        title: 'SETTINGS',
        items: [
          'Added a Privacy Policy to Settings, also available directly at /settings/privacy.',
          'Screen readers now pair each version number with its label, and the version rows wrap instead of running off narrow screens at large text sizes.',
        ],
      },
      {
        title: 'SONGS',
        items: [
          'Added Year, Duration, Item Shop, and Double Bass filters.',
          'Song filters are now available without selecting a profile or band.',
          'General filter options can be toggled independently.',
          'Has FC sorting now requires a selected profile.',
          'Tapping your band\'s row under a Duos, Trios, or Quads preview now jumps to its spot on the full leaderboard, like your solo row.',
          'On full song leaderboards, your player and band footers jump to your position when it is on another page, and open your profile when it is already visible.',
        ],
      },
      {
        title: 'ITEM SHOP',
        items: [
          'Sort the Item Shop by Title, Artist, Year, or Duration, ascending or descending. Your choice is remembered for both grid and list views.',
          'Screen readers now announce which sort direction is selected in the sort window, and its Reset and Apply buttons are easier to tap.',
        ],
      },
      {
        title: 'SUGGESTIONS',
        items: [
          'Starting a fresh mix now keeps the list at the top while new cards finish loading.',
          'After Start a new mix, keyboard and screen reader focus moves to the top of Suggestions instead of being lost.',
        ],
      },
      {
        title: 'RIVALS',
        items: [
          'Section links on Rivals, Rival Detail, Compete, and player Bands now read "View All", matching the rest of the app.',
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
      {
        title: 'SETTINGS',
        items: [
          'App Version now increases automatically with every release and shows the build it came from.',
          'Report an Issue or Request a Feature, with media attachments, right from App Settings when feedback is enabled.',
          'The Privacy Policy now explains how feedback you send is handled.',
        ],
      },
    ],
  },
];
