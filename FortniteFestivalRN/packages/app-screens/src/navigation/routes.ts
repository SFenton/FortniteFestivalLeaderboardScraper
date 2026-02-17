export const Routes = {
  Songs: 'Songs',
  Suggestions: 'Suggestions',
  Statistics: 'Statistics',
  Settings: 'Settings',
} as const;

export type RouteName = (typeof Routes)[keyof typeof Routes];
