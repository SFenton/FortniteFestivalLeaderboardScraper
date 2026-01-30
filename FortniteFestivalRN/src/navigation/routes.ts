export const Routes = {
  Sync: 'Sync',
  Songs: 'Songs',
  Suggestions: 'Suggestions',
  Statistics: 'Statistics',
  Settings: 'Settings',
} as const;

export type RouteName = (typeof Routes)[keyof typeof Routes];
