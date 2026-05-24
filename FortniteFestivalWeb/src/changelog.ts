

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
        title: 'ITEM SHOP',
        items: [
          'Newly released songs in the Item Shop have a gold pulse on Songs Page and Song Details.',
          "Songs in the Item Shop that aren't leaving tomorrow now have a green pulse, to match the gold/green/red styles of the instrument chips on Songs Page.",
        ],
      },
      {
        title: 'MOBILE',
        items: [
          'FAB buttons and other dock buttons now animate in for a more visually pleasing experience.',
          'Fixed a bug in search modal where dismissing the keyboard after results show did not expand results view appropriately.',
        ],
      },
      {
        title: 'SONG DETAILS',
        items: [
          'Fixed a bug where leaderboard ranks did not reflect the actual Epic leaderboard value in some cases.',
        ],
      },
      {
        title: 'NOTIFICATIONS',
        items: [
          'Fixed a bug where notification alerts would reset when you re-open the web browser.',
          'Added support for switching profiles/bands and returning to a different profile/band and seeing the appropriate amount of unread notifications, instead of all of them.',
        ],
      },
      {
        title: 'RIVALS',
        items: [
          'Improved performance when viewing a Rival for the first time.',
          'Improved availability of Rivals during scrape.',
        ],
      },
      {
        title: 'LEADERBOARDS',
        items: [
          'Changed to instrument icons on combo leaderboards instead of "Lead + ..." text.',
          'Updated FAB dock on mobile to match other pages.',
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
