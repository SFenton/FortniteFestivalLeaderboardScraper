import {
  GOLD_STARS_THRESHOLD,
  InstrumentKeys,
  MAX_DISPLAY_STARS,
  displayStarCount,
  filterCategoryForInstruments,
  shouldShowCategory,
} from '../config';
import {
  ACCURACY_SCALE,
  BackfillStatus,
  Difficulty,
  DIFFICULTIES,
  Keys,
  LoadPhase,
  SyncPhase,
} from '../runtime';
import type {InstrumentShowSettings} from '../songListConfig';
import {getCategoryTypeId} from '../suggestions/suggestionFilterConfig';
import type {SuggestionCategory} from '../suggestions/types';

const visibleSettings = (): InstrumentShowSettings => ({
  showLead: true,
  showBass: true,
  showDrums: true,
  showVocals: true,
  showProLead: true,
  showProBass: true,
  showPeripheralVocals: true,
  showPeripheralCymbals: true,
  showPeripheralDrums: true,
});

describe('retained core configuration exports', () => {
  test('publishes instrument, star, key, and enum constants', () => {
    expect(InstrumentKeys).toEqual([
      'guitar',
      'bass',
      'drums',
      'vocals',
      'pro_guitar',
      'pro_bass',
      'peripheral_vocals',
      'peripheral_cymbals',
      'peripheral_drums',
    ]);
    expect(MAX_DISPLAY_STARS).toBe(5);
    expect(GOLD_STARS_THRESHOLD).toBe(6);
    expect(displayStarCount(0)).toBe(1);
    expect(displayStarCount(4)).toBe(4);
    expect(displayStarCount(6)).toBe(5);

    expect(Keys.Escape).toBe('Escape');
    expect(LoadPhase.ContentIn).toBe('contentIn');
    expect(Difficulty.Expert).toBe('expert');
    expect(DIFFICULTIES).toEqual([
      Difficulty.Easy,
      Difficulty.Medium,
      Difficulty.Hard,
      Difficulty.Expert,
    ]);
    expect(SyncPhase.PostScrape).toBe('postscrape');
    expect(BackfillStatus.InProgress).toBe('in_progress');
    expect(ACCURACY_SCALE).toBe(10_000);
  });

  test.each([
    ['peripheral_vocals', 'showPeripheralVocals'],
    ['peripheralvocals', 'showPeripheralVocals'],
    ['mic_mode', 'showPeripheralVocals'],
    ['peripheral_cymbals', 'showPeripheralCymbals'],
    ['peripheralcymbals', 'showPeripheralCymbals'],
    ['peripheral_drums', 'showPeripheralDrums'],
    ['peripheraldrums', 'showPeripheralDrums'],
    ['pro_guitar', 'showProLead'],
    ['prolead', 'showProLead'],
    ['pro_lead', 'showProLead'],
    ['pro_bass', 'showProBass'],
    ['probass', 'showProBass'],
    ['guitar', 'showLead'],
    ['lead', 'showLead'],
    ['bass', 'showBass'],
    ['drums', 'showDrums'],
    ['vocals', 'showVocals'],
    ['vocal', 'showVocals'],
  ] as const)('honors the %s category visibility setting', (categoryKey, settingKey) => {
    const settings = visibleSettings();
    settings[settingKey] = false;
    expect(shouldShowCategory(categoryKey.toUpperCase(), settings)).toBe(false);
  });

  test('keeps unscoped categories and visible instrument categories', () => {
    expect(shouldShowCategory('general', visibleSettings())).toBe(true);
    expect(shouldShowCategory('lead_scores', visibleSettings())).toBe(true);
  });

  test('filters hidden instruments without mutating unchanged categories', () => {
    const category: SuggestionCategory = {
      key: 'mixed',
      title: 'Mixed',
      description: 'Mixed instruments',
      songs: [
        {songId: 'lead', title: 'Lead', artist: 'A', instrumentKey: 'guitar'},
        {songId: 'drums', title: 'Drums', artist: 'B', instrumentKey: 'drums'},
        {songId: 'general', title: 'General', artist: 'C'},
      ],
    };

    expect(filterCategoryForInstruments(category, visibleSettings())).toBe(category);

    const leadHidden = visibleSettings();
    leadHidden.showLead = false;
    expect(filterCategoryForInstruments(category, leadHidden)?.songs.map(song => song.songId))
      .toEqual(['drums', 'general']);

    const onlyLead: SuggestionCategory = {
      ...category,
      songs: [category.songs[0]],
    };
    expect(filterCategoryForInstruments(onlyLead, leadHidden)).toBeNull();
  });

  test.each([
    ['band_unplayed', 'Unplayed'],
    ['band_near_fc', 'NearFC'],
    ['band_star_progress', 'StarProgress'],
    ['band_pct_push', 'PercentilePush'],
    ['band_rank_improve', 'PctImprove'],
    ['band_stale', 'Stale'],
    ['song_rival_lead', 'SongRivals'],
    ['lb_rival_lead', 'LeaderboardRivals'],
    ['near_fc_any', 'NearFC'],
    ['unfc_guitar', 'NearFC'],
    ['samename_nearfc_guitar', 'NearFC'],
    ['almost_six_star_guitar', 'StarProgress'],
    ['star_gains_guitar', 'StarProgress'],
    ['more_stars_guitar', 'StarProgress'],
    ['almost_elite_guitar', 'AlmostElite'],
    ['pct_push_guitar', 'PercentilePush'],
    ['stale_guitar', 'Stale'],
    ['pct_improve_guitar', 'PctImprove'],
    ['same_pct_improve_guitar', 'PctImprove'],
    ['improve_rankings_guitar', 'PctImprove'],
    ['unplayed_guitar', 'Unplayed'],
    ['first_plays_mixed', 'Unplayed'],
    ['variety_pack', 'VarietyPack'],
    ['artist_sampler_name', 'ArtistEssentials'],
    ['artist_unplayed_name', 'ArtistDiscover'],
    ['samename_group', 'SameName'],
    ['near_max_guitar', 'NearMaxScore'],
    ['unknown', null],
  ] as const)('maps %s to its retained suggestion type', (categoryKey, expected) => {
    expect(getCategoryTypeId(categoryKey)).toBe(expected);
  });
});
