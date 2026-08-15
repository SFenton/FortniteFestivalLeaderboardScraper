import { readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';

const webRoot = path.resolve(__dirname, '..');
const totalCeiling = 93;
const propertyCeilings: Record<string, number> = {
  iconAction: 19,
  iconChevron: 1,
  iconDefault: 7,
  iconFab: 30,
  iconInstrument: 5,
  iconInstrumentSm: 1,
  iconLg: 2,
  iconMd: 1,
  iconSm: 6,
  iconTab: 3,
  iconXl: 12,
  settingsSliderPadding: 1,
  thumb: 5,
};
const fileCeilings: Record<string, number> = {
  '__test__/components/display/InstrumentIcons.test.tsx': 2,
  'src/App.tsx': 24,
  'src/components/common/Accordion.tsx': 1,
  'src/components/common/GraphCard.tsx': 5,
  'src/components/common/InstrumentSelector.tsx': 4,
  'src/components/firstRun/FirstRunCarousel.tsx': 3,
  'src/components/shell/HamburgerButton.tsx': 1,
  'src/components/shell/fab/ComboInstrumentFabAccessory.tsx': 1,
  'src/components/songs/headers/SongInfoHeader.tsx': 2,
  'src/components/songs/metadata/GoldStars.tsx': 1,
  'src/components/songs/metadata/SongInfo.tsx': 1,
  'src/hooks/chart/useListAnimation.ts': 1,
  'src/pages/Page.tsx': 2,
  'src/pages/band/PlayerBandsPage.tsx': 1,
  'src/pages/compete/CompetePage.tsx': 1,
  'src/pages/leaderboard/player/PlayerHistoryPage.tsx': 1,
  'src/pages/leaderboard/player/modals/PlayerScoreSortModal.tsx': 2,
  'src/pages/leaderboards/BandRankingsPage.tsx': 1,
  'src/pages/leaderboards/FullRankingsPage.tsx': 3,
  'src/pages/leaderboards/LeaderboardsOverviewPage.tsx': 1,
  'src/pages/leaderboards/firstRun/metricInfo/MetricInfoSlide.tsx': 1,
  'src/pages/leaderboards/firstRun/metricInfo/SongDemoSlide.tsx': 1,
  'src/pages/manual/ManualPage.tsx': 5,
  'src/pages/rivals/RivalsPage.tsx': 5,
  'src/pages/settings/LicensesPage.tsx': 1,
  'src/pages/settings/SettingsPage.tsx': 2,
  'src/pages/shop/ShopPage.tsx': 2,
  'src/pages/songs/SongsPage.tsx': 3,
  'src/pages/songs/components/SongsToolbar.tsx': 7,
  'src/pages/songs/firstRun/demo/MetadataDemo.tsx': 1,
  'src/pages/songs/firstRun/demo/NavigationDemo.tsx': 4,
  'src/pages/songs/firstRun/demo/SongIconsDemo.tsx': 1,
  'src/pages/suggestions/SuggestionsPage.tsx': 1,
  'src/pages/suggestions/modals/SuggestionsFilterModal.tsx': 1,
};

describe('deprecated Size boundary', () => {
  it('allows reductions but rejects new files, properties, or count growth', () => {
    const propertyCounts = new Map<string, number>();
    const fileCounts = new Map<string, number>();

    for (const fileName of [
      ...sourceFiles(path.join(webRoot, 'src')),
      ...sourceFiles(path.join(webRoot, '__test__')),
    ]) {
      const relativePath = path.relative(webRoot, fileName).replace(/\\/g, '/');
      for (const match of readFileSync(fileName, 'utf8').matchAll(/\bSize\.([A-Za-z0-9_]+)/g)) {
        const property = match[1]!;
        propertyCounts.set(property, (propertyCounts.get(property) ?? 0) + 1);
        fileCounts.set(relativePath, (fileCounts.get(relativePath) ?? 0) + 1);
      }
    }

    const total = Array.from(propertyCounts.values())
      .reduce((sum, count) => sum + count, 0);
    expect(total).toBeLessThanOrEqual(totalCeiling);

    for (const [property, count] of propertyCounts) {
      expect(propertyCeilings[property], `new Size.${property}`).toBeDefined();
      expect(count, `Size.${property}`).toBeLessThanOrEqual(propertyCeilings[property]!);
    }
    for (const [fileName, count] of fileCounts) {
      expect(fileCeilings[fileName], `new Size consumer ${fileName}`).toBeDefined();
      expect(count, fileName).toBeLessThanOrEqual(fileCeilings[fileName]!);
    }
  });
});

function sourceFiles(root: string): string[] {
  return readdirSync(root, { withFileTypes: true }).flatMap(entry => {
    const fileName = path.join(root, entry.name);
    if (entry.isDirectory()) return sourceFiles(fileName);
    return entry.isFile() && /\.(?:ts|tsx)$/.test(entry.name) ? [fileName] : [];
  });
}
