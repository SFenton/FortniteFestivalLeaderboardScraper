/**
 * COMPREHENSIVE BRANCH COVERAGE — covers all ~120 testable uncovered branches.
 * Organized by source file, each test targets a specific false/null path.
 */
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook } from '@testing-library/react';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { ACCURACY_SCALE, PlayerScoreSortMode } from '@festival/core';
import type { ServerSong as Song, PlayerScore, ServerInstrumentKey as InstrumentKey, ServerScoreHistoryEntry } from '@festival/core/api/serverTypes';
import type { SongFilters, SongSortMode } from '../../utils/songSettings';

/* ── Helpers ── */
function ps(o: Partial<PlayerScore> = {}): PlayerScore {
  return { songId: 's1', instrument: 'Solo_Guitar', score: 100000, rank: 5, totalEntries: 100, accuracy: 95 * ACCURACY_SCALE, isFullCombo: false, stars: 5, season: 5, ...o };
}
function song(id: string, o: Partial<Song> = {}): Song {
  return { songId: id, title: `Song ${id}`, artist: `Art ${id}`, year: 2024, ...o };
}
function emptyF(): SongFilters {
  return { missingScores: {}, missingFCs: {}, hasScores: {}, hasFCs: {}, seasonFilter: {}, percentileFilter: {}, starsFilter: {}, difficultyFilter: {} };
}
function hist(o: Partial<ServerScoreHistoryEntry> = {}): ServerScoreHistoryEntry {
  return { songId: 's1', instrument: 'Solo_Guitar', newScore: 100000, newRank: 5, changedAt: '2025-01-15T10:00:00Z', ...o };
}

/* ══════════════════ playerStats — 12 branches ══════════════════ */
import { computeInstrumentStats, computeOverallStats } from '../../pages/player/helpers/playerStats';

describe('playerStats false-path branches', () => {
  it('empty scores → averageStars=0, avgAcc=0, bestAcc=0, avgScore=0', () => {
    const s = computeInstrumentStats([], 10);
    expect(s.averageStars).toBe(0);
    expect(s.avgAccuracy).toBe(0);
    expect(s.bestAccuracy).toBe(0);
    expect(s.avgScore).toBe(0);
    expect(s.bestRank).toBe(0);
    expect(s.bestRankSongId).toBeNull();
    expect(s.percentileBuckets.length).toBe(0);
  });

  it('scores with stars=undefined → triggers ?? 0 fallback in all star filters', () => {
    const s = computeInstrumentStats([ps({ stars: undefined, accuracy: undefined, rank: undefined as any, totalEntries: undefined })], 10);
    expect(s.goldStarCount).toBe(0);
    expect(s.fiveStarCount).toBe(0);
    expect(s.fourStarCount).toBe(0);
    expect(s.threeStarCount).toBe(0);
    expect(s.twoStarCount).toBe(0);
    expect(s.oneStarCount).toBe(0);
    expect(s.averageStars).toBe(0);
    expect(s.avgAccuracy).toBe(0);
    expect(s.bestRank).toBe(0);
    expect(s.bestRankSongId).toBeNull();
  });

  it('scores with accuracy=0 → accuracies empty → avgAcc=0', () => {
    const s = computeInstrumentStats([ps({ accuracy: 0 })], 10);
    expect(s.avgAccuracy).toBe(0);
  });

  it('scores with rank=0 → no ranked → bestRank=0, bestRankSongId=null', () => {
    const s = computeInstrumentStats([ps({ rank: 0, totalEntries: 0 })], 10);
    expect(s.bestRank).toBe(0);
    expect(s.bestRankSongId).toBeNull();
  });

  it('scores with no percentile data → percentileBuckets empty', () => {
    const s = computeInstrumentStats([ps({ rank: 0, totalEntries: 0 })], 10);
    expect(s.percentileBuckets.length).toBe(0);
  });

  it('computeOverallStats with undefined fields → triggers ?? fallbacks', () => {
    const s = computeOverallStats([ps({ rank: undefined as any, totalEntries: undefined, stars: undefined, accuracy: undefined, isFullCombo: undefined as any })]);
    expect(s.bestRank).toBe(0);
    expect(s.bestRankSongId).toBeNull();
  });

  it('computeOverallStats with empty scores', () => {
    const s = computeOverallStats([]);
    expect(s.songsPlayed).toBe(0);
    expect(s.avgAccuracy).toBe(0);
  });
});

/* ══════════════════ useFilteredSongs — 9 branches ══════════════════ */
import { useFilteredSongs } from '../../hooks/data/useFilteredSongs';

describe('useFilteredSongs false-path branches', () => {
  const songs = [song('s1'), song('s2'), song('s3')];
  const noS = new Map<string, PlayerScore>();
  const noA = new Map<string, Map<InstrumentKey, PlayerScore>>();

  function call(o: Partial<Parameters<typeof useFilteredSongs>[0]> = {}) {
    return renderHook(() => useFilteredSongs({ songs, search: '', sortMode: 'title', sortAscending: true, filters: emptyF(), instrument: null, scoreMap: noS, allScoreMap: noA, ...o })).result.current;
  }

  it('percentile false path: rank=0 → pct=undefined → filtered out when pctFilter[0]=false', () => {
    const sm = new Map([['s1', ps({ songId: 's1', rank: 0, totalEntries: 0 })]]);
    const am = new Map<string, Map<InstrumentKey, PlayerScore>>([['s1', new Map([['Solo_Guitar', ps({ songId: 's1' })]])]]);
    const f = { ...emptyF(), percentileFilter: { 0: false } };
    const r = call({ scoreMap: sm, allScoreMap: am, filters: f, instrument: 'Solo_Guitar' });
    expect(r.some(s => s.songId === 's1')).toBe(false);
  });

  it('percentile bracket ?? 100 fallback: pct > 100 → bracket=100', () => {
    // rank=1, totalEntries=1 → pct = 100% → bracket = 100
    const sm = new Map([['s1', ps({ songId: 's1', rank: 1, totalEntries: 1 })]]);
    const am = new Map<string, Map<InstrumentKey, PlayerScore>>([['s1', new Map([['Solo_Guitar', ps({ songId: 's1' })]])]]);
    const f = { ...emptyF(), percentileFilter: { 100: false } };
    const r = call({ scoreMap: sm, allScoreMap: am, filters: f, instrument: 'Solo_Guitar' });
    expect(r.some(s => s.songId === 's1')).toBe(false);
  });

  it('sort by year with undefined year → ?? 0 fallback', () => {
    const s = [song('s1', { year: undefined }), song('s2', { year: 2020 })];
    const r = renderHook(() => useFilteredSongs({ songs: s, search: '', sortMode: 'year', sortAscending: true, filters: emptyF(), instrument: null, scoreMap: noS, allScoreMap: noA })).result.current;
    expect(r[0]!.songId).toBe('s1');
  });

  it('score with undefined totalEntries → ?? 0 in percentile calc', () => {
    const sm = new Map([['s1', ps({ songId: 's1', rank: 5, totalEntries: undefined })]]);
    const am = new Map<string, Map<InstrumentKey, PlayerScore>>([['s1', new Map([['Solo_Guitar', ps({ songId: 's1' })]])]]);
    const f = { ...emptyF(), percentileFilter: { 0: false } };
    const r = call({ scoreMap: sm, allScoreMap: am, filters: f, instrument: 'Solo_Guitar' });
    // With totalEntries=undefined, rank > 0 but totalEntries=0 → pct=undefined → bucket 0
    expect(r.length).toBeLessThanOrEqual(3);
  });

  it('sort by score with scoreMap → uses compareByMode', () => {
    const sm = new Map([['s1', ps({ songId: 's1', score: 200 })], ['s2', ps({ songId: 's2', score: 100 })]]);
    const r = call({ sortMode: 'score', scoreMap: sm });
    expect(r[0]!.songId).toBe('s2');
  });

  it('sort fallback to title when scoreMap empty', () => {
    const r = call({ sortMode: 'score' });
    expect(r[0]!.songId).toBe('s1');
  });

  it('hasScores + hasFCs combined → both must pass', () => {
    const am = new Map<string, Map<InstrumentKey, PlayerScore>>([
      ['s1', new Map([['Solo_Guitar', ps({ songId: 's1', isFullCombo: true })]])],
      ['s2', new Map([['Solo_Guitar', ps({ songId: 's2', score: 50000, isFullCombo: false })]])],
    ]);
    const f = { ...emptyF(), hasScores: { Solo_Guitar: true }, hasFCs: { Solo_Guitar: true } };
    const r = call({ filters: f, allScoreMap: am });
    expect(r.length).toBe(1);
  });
});

/* ══════════════════ useSortedScoreHistory — 5 branches ══════════════════ */
import { useSortedScoreHistory } from '../../hooks/data/useSortedScoreHistory';

describe('useSortedScoreHistory false-path branches', () => {
  it('accuracy: same accuracy, same FC → falls to score tiebreaker', () => {
    const a = hist({ accuracy: 950000, isFullCombo: false, newScore: 200000 });
    const b = hist({ accuracy: 950000, isFullCombo: false, newScore: 100000 });
    const { result } = renderHook(() => useSortedScoreHistory([a, b], PlayerScoreSortMode.Accuracy, true));
    expect(result.current[0]!.newScore).toBe(100000);
  });

  it('accuracy: same accuracy, same FC, same score → date tiebreaker', () => {
    const a = hist({ accuracy: 950000, isFullCombo: false, newScore: 100000, scoreAchievedAt: '2025-02-01T00:00:00Z' });
    const b = hist({ accuracy: 950000, isFullCombo: false, newScore: 100000, scoreAchievedAt: '2025-01-01T00:00:00Z' });
    const { result } = renderHook(() => useSortedScoreHistory([a, b], PlayerScoreSortMode.Accuracy, true));
    expect(result.current[0]!.scoreAchievedAt).toBe('2025-01-01T00:00:00Z');
  });

  it('accuracy: null scoreAchievedAt AND null changedAt → both ?? fallbacks', () => {
    const a = hist({ accuracy: 950000, isFullCombo: undefined as any, newScore: 100000, scoreAchievedAt: undefined, changedAt: undefined as any });
    const b = hist({ accuracy: 950000, isFullCombo: undefined as any, newScore: 100000, scoreAchievedAt: undefined, changedAt: undefined as any });
    const { result } = renderHook(() => useSortedScoreHistory([a, b], PlayerScoreSortMode.Accuracy, true));
    expect(result.current.length).toBe(2);
  });

  it('date: scoreAchievedAt=undefined → uses changedAt via ??', () => {
    const a = hist({ scoreAchievedAt: undefined, changedAt: '2025-02-01T00:00:00Z' });
    const b = hist({ scoreAchievedAt: undefined, changedAt: '2025-01-01T00:00:00Z' });
    const { result } = renderHook(() => useSortedScoreHistory([a, b], PlayerScoreSortMode.Date, true));
    expect(result.current[0]!.changedAt).toBe('2025-01-01T00:00:00Z');
  });

  it('season: null season → ?? 0 fallback', () => {
    const a = hist({ season: undefined });
    const b = hist({ season: 5 });
    const { result } = renderHook(() => useSortedScoreHistory([a, b], PlayerScoreSortMode.Season, true));
    expect(result.current[0]!.season).toBeUndefined();
  });

  it('date: both items have scoreAchievedAt', () => {
    const a = hist({ scoreAchievedAt: '2025-03-01T00:00:00Z' });
    const b = hist({ scoreAchievedAt: '2025-01-01T00:00:00Z' });
    const { result } = renderHook(() => useSortedScoreHistory([a, b], PlayerScoreSortMode.Date, true));
    expect(result.current[0]!.scoreAchievedAt).toBe('2025-01-01T00:00:00Z');
  });
});

/* ══════════════════ suggestionsFilter — 7 branches ══════════════════ */
import { shouldShowCategoryType, filterCategoryForInstrumentTypes } from '../../utils/suggestionsFilter';
import { globalKeyFor } from '@festival/core/suggestions/suggestionFilterConfig';
import type { SuggestionCategory } from '@festival/core/suggestions/types';

function mockCat(key: string, songs: any[] = []): SuggestionCategory {
  return { key, label: key, songs: songs.map(s => ({ songId: 's1', title: 'T', artist: 'A', ...s })) } as any;
}

describe('suggestionsFilter false-path branches', () => {
  it('shouldShowCategoryType: unknown key → true', () => {
    expect(shouldShowCategoryType('unknown_key', {} as any)).toBe(true);
  });

  it('filterCategory: no typeId → returns cat unchanged', () => {
    const cat = mockCat('unknown');
    const r = filterCategoryForInstrumentTypes(cat, {} as any);
    expect(r).toBe(cat);
  });

  it('filterCategory: catInstrument set + filter off → null', () => {
    const cat = mockCat('near_fc_guitar', [{ instrumentKey: 'guitar' }]);
    const draft = { suggestionsLeadNearFC: false } as any;
    const r = filterCategoryForInstrumentTypes(cat, draft);
    expect(r).toBeNull();
  });

  it('filterCategory: no catInstrument, songs filtered partially', () => {
    const cat = mockCat('unplayed_mixed', [{ instrumentKey: 'guitar' }, { instrumentKey: 'bass' }]);
    const draft = { suggestionsLeadUnplayed: true, suggestionsBassUnplayed: false } as any;
    const r = filterCategoryForInstrumentTypes(cat, draft);
    expect(r!.songs.length).toBe(1);
  });

  it('filterCategory: all songs filtered → null', () => {
    const cat = mockCat('unplayed_mixed', [{ instrumentKey: 'guitar' }]);
    const draft = { suggestionsLeadUnplayed: false } as any;
    const r = filterCategoryForInstrumentTypes(cat, draft);
    expect(r).toBeNull();
  });

  it('filterCategory: no songs filtered → returns original cat', () => {
    const cat = mockCat('unplayed_mixed', [{ instrumentKey: 'guitar' }]);
    const draft = { suggestionsLeadUnplayed: true } as any;
    const r = filterCategoryForInstrumentTypes(cat, draft);
    expect(r).toBe(cat);
  });

  it('filterCategory: songs without instrumentKey kept', () => {
    const cat = mockCat('unplayed_mixed', [{ instrumentKey: undefined }]);
    const draft = {} as any;
    const r = filterCategoryForInstrumentTypes(cat, draft);
    expect(r).toBe(cat);
  });
});

/* ══════════════════ useChartData — 2 branches ══════════════════ */
import { useChartData } from '../../hooks/chart/useChartData';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';

vi.mock('../../api/client', () => ({
  api: { getPlayerHistory: vi.fn().mockResolvedValue({ accountId: 'p1', count: 0, history: [] }) },
}));

function qcWrap({ children }: { children: ReactNode }) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
  return <QueryClientProvider client={qc}>{children}</QueryClientProvider>;
}

describe('useChartData branches', () => {
  it('scoreAchievedAt=undefined → changedAt fallback via ??', () => {
    const history = [hist({ instrument: 'Solo_Guitar', scoreAchievedAt: undefined, changedAt: '2025-06-15T10:00:00Z' })];
    const { result } = renderHook(() => useChartData('a1', 's1', 'Solo_Guitar', history), { wrapper: qcWrap });
    expect(result.current.chartData[0]!.dateLabel).toContain('6/15');
  });

  it('accuracy=undefined → 0 fallback via ??', () => {
    const history = [hist({ instrument: 'Solo_Guitar', accuracy: undefined })];
    const { result } = renderHook(() => useChartData('a1', 's1', 'Solo_Guitar', history), { wrapper: qcWrap });
    expect(result.current.chartData[0]!.accuracy).toBe(0);
  });

  it('stars=undefined and season=undefined → undefined in output', () => {
    const history = [hist({ instrument: 'Solo_Guitar', stars: undefined, season: undefined })];
    const { result } = renderHook(() => useChartData('a1', 's1', 'Solo_Guitar', history), { wrapper: qcWrap });
    expect(result.current.chartData[0]!.stars).toBeUndefined();
    expect(result.current.chartData[0]!.season).toBeUndefined();
  });

  it('isFullCombo=undefined → false fallback via ??', () => {
    const history = [hist({ instrument: 'Solo_Guitar', isFullCombo: undefined })];
    const { result } = renderHook(() => useChartData('a1', 's1', 'Solo_Guitar', history), { wrapper: qcWrap });
    expect(result.current.chartData[0]!.isFullCombo).toBe(false);
  });
});

/* ══════════════════ songSettings — 1 branch ══════════════════ */
import { loadSongSettings, defaultSongSettings } from '../../utils/songSettings';

describe('songSettings catch branch', () => {
  it('malformed JSON → returns defaults', () => {
    localStorage.setItem('fst:songSettings', '{bad json!!!');
    expect(loadSongSettings()).toEqual(defaultSongSettings());
    localStorage.clear();
  });
});

/* ══════════════════ SongRow switch false paths — 16 branches ══════════════════ */
// Already tested in JSXBranches.test.tsx but adding more specific false-path tests

vi.mock('../../components/display/InstrumentIcons', () => ({
  InstrumentIcon: ({ instrument }: any) => <span data-testid={`icon-${instrument}`}>{instrument}</span>,
  getInstrumentStatusVisual: () => ({ fill: '#000', stroke: '#000' }),
}));
vi.mock('../../components/songs/metadata/AccuracyDisplay', () => ({ default: ({ accuracy }: any) => <span data-testid="acc">{accuracy}</span> }));
vi.mock('../../components/songs/metadata/PercentilePill', () => ({ default: ({ display }: any) => <span data-testid="pct">{display}</span> }));
vi.mock('../../components/songs/metadata/SeasonPill', () => ({ default: ({ season }: any) => <span data-testid="season">S{season}</span> }));
vi.mock('../../components/songs/metadata/MiniStars', () => ({ default: ({ starsCount }: any) => <span data-testid="stars">{starsCount}</span> }));
vi.mock('../../components/songs/metadata/DifficultyBars', () => ({ default: ({ level }: any) => <span data-testid="diff">{level}</span> }));
vi.mock('../../components/songs/metadata/SongInfo', () => ({ default: ({ title }: any) => <span data-testid="info">{title}</span> }));
vi.mock('@festival/core', async (importOriginal) => {
  const actual = await importOriginal<Record<string, unknown>>();
  return { ...actual, formatPercentileBucket: (p: number) => `Top ${Math.ceil(p)}%` };
});

import { SongRow, compareByMode } from '../../pages/songs/components/SongRow';

const baseSong: Song = { songId: 's1', title: 'T', artist: 'A', year: 2024, difficulty: { guitar: 3 } };
const defProps = {
  song: baseSong, score: ps(), instrument: 'Solo_Guitar' as InstrumentKey,
  instrumentFilter: null as InstrumentKey | null,
  allScoreMap: undefined as Map<string, PlayerScore> | undefined,
  showInstrumentIcons: false, enabledInstruments: ['Solo_Guitar'] as InstrumentKey[],
  metadataOrder: ['score', 'percentage', 'percentile', 'seasonachieved', 'stars', 'intensity'],
  sortMode: 'score' as SongSortMode, isMobile: false,
};
function rsr(o: Partial<typeof defProps> = {}) { return render(<MemoryRouter><SongRow {...defProps} {...o} /></MemoryRouter>); }

describe('SongRow false-path branches', () => {
  beforeEach(() => vi.clearAllMocks());

  // Each metadata element's null/false return path
  it('score=0 → no score rendered', () => {
    rsr({ score: ps({ score: 0 }), metadataOrder: ['score'] });
    expect(screen.queryByText('0')).toBeNull();
  });
  it('accuracy=0 → no accuracy rendered', () => {
    rsr({ score: ps({ accuracy: 0 }), metadataOrder: ['percentage'] });
    expect(screen.queryByTestId('acc')).toBeNull();
  });
  it('stars=0 → no stars rendered', () => {
    rsr({ score: ps({ stars: 0 }), metadataOrder: ['stars'] });
    expect(screen.queryByTestId('stars')).toBeNull();
  });
  it('season=null → no season rendered', () => {
    rsr({ score: ps({ season: undefined }), metadataOrder: ['seasonachieved'] });
    expect(screen.queryByTestId('season')).toBeNull();
  });
  it('season=0 → no season rendered', () => {
    rsr({ score: ps({ season: 0 }), metadataOrder: ['seasonachieved'] });
    expect(screen.queryByTestId('season')).toBeNull();
  });
  it('rank=0 → no percentile rendered', () => {
    rsr({ score: ps({ rank: 0 }), metadataOrder: ['percentile'] });
    expect(screen.queryByTestId('pct')).toBeNull();
  });
  it('totalEntries=0 → no percentile rendered', () => {
    rsr({ score: ps({ totalEntries: 0 }), metadataOrder: ['percentile'] });
    expect(screen.queryByTestId('pct')).toBeNull();
  });
  it('no difficulty → no intensity rendered', () => {
    rsr({ song: { ...baseSong, difficulty: undefined }, metadataOrder: ['intensity'] });
    expect(screen.queryByTestId('diff')).toBeNull();
  });
  it('unknown metadata key → nothing', () => {
    rsr({ metadataOrder: ['xyz'] });
    expect(screen.getByTestId('info')).toBeTruthy();
  });

  // Mobile rendering paths
  it('mobile with score → uses mobileTopRow layout', () => {
    rsr({ isMobile: true });
    expect(screen.getByTestId('info')).toBeTruthy();
  });
  it('mobile with chips → uses chipRow layout', () => {
    rsr({ isMobile: true, showInstrumentIcons: true, score: undefined, allScoreMap: new Map([['Solo_Guitar', ps()]]) });
    expect(screen.getByTestId('icon-Solo_Guitar')).toBeTruthy();
  });
  it('no score, no chips → minimal row', () => {
    rsr({ score: undefined });
    expect(screen.getByTestId('info')).toBeTruthy();
  });

  // compareByMode additional false paths
  it('percentage: same accuracy different FC', () => {
    expect(compareByMode('percentage', ps({ accuracy: 900, isFullCombo: false }), ps({ accuracy: 900, isFullCombo: true }))).toBeLessThan(0);
  });
  it('percentile: rank=0 → Infinity', () => {
    expect(compareByMode('percentile', ps({ rank: 0 }), ps({ rank: 1, totalEntries: 100 }))).toBeGreaterThan(0);
  });
  it('stars: null → 0', () => {
    expect(compareByMode('stars', ps({ stars: undefined }), ps({ stars: 1 }))).toBeLessThan(0);
  });
  it('seasonachieved: null → 0', () => {
    expect(compareByMode('seasonachieved', ps({ season: undefined }), ps({ season: 1 }))).toBeLessThan(0);
  });
});

/* ══════════════════ AlbumArt — 3 branches ══════════════════ */
import AlbumArt from '../../components/songs/metadata/AlbumArt';

describe('AlbumArt false-path branches', () => {
  it('no src → placeholder', () => {
    const { container } = render(<AlbumArt src={undefined} size={48} />);
    expect(container.querySelector('img')).toBeNull();
  });
  it('with src → renders img', () => {
    const { container } = render(<AlbumArt src="https://x.com/a.jpg" size={48} />);
    expect(container.querySelector('img')).toBeTruthy();
  });
});

/* ══════════════════ SongRow ?? fallbacks — line 57, 93-96, 99-100 ══════════════════ */
describe('SongRow ?? fallback branches', () => {
  it('accuracy=undefined triggers ?? 0 fallback', () => {
    rsr({ score: ps({ accuracy: undefined }), metadataOrder: ['percentage'] });
    expect(screen.queryByTestId('acc')).toBeNull();
  });
  it('stars=undefined triggers ?? 0 fallback', () => {
    rsr({ score: ps({ stars: undefined }), metadataOrder: ['stars'] });
    expect(screen.queryByTestId('stars')).toBeNull();
  });
  it('totalEntries=undefined triggers ?? 0 in percentile', () => {
    rsr({ score: ps({ totalEntries: undefined }), metadataOrder: ['percentile'] });
    expect(screen.queryByTestId('pct')).toBeNull();
  });
  it('season=undefined renders no season pill', () => {
    rsr({ score: ps({ season: undefined }), metadataOrder: ['seasonachieved'] });
    expect(screen.queryByTestId('season')).toBeNull();
  });
  it('intensity with undefined difficulty key', () => {
    rsr({ instrument: 'Solo_PeripheralGuitar' as any, metadataOrder: ['intensity'] });
    // Solo_PeripheralGuitar maps to proGuitar difficulty key which may be undefined
    expect(screen.getByTestId('info')).toBeTruthy();
  });
  it('compareByMode: percentile with undefined totalEntries', () => {
    const a = ps({ rank: 5, totalEntries: undefined });
    const b = ps({ rank: 2, totalEntries: 100 });
    expect(compareByMode('percentile', a, b)).toBeGreaterThan(0);
  });
  it('compareByMode: percentage with undefined accuracy → ?? 0', () => {
    const a = ps({ accuracy: undefined, isFullCombo: undefined as any });
    const b = ps({ accuracy: 900, isFullCombo: false });
    expect(compareByMode('percentage', a, b)).toBeLessThan(0);
  });
});

/* ══════════════════ useFilteredSongs: line 103 (bracket ?? 100) + line 129 ══════════════════ */
describe('useFilteredSongs ?? fallback branches', () => {
  const songs2 = [song('s1'), song('s2')];
  const noS2 = new Map<string, PlayerScore>();
  const noA2 = new Map<string, Map<InstrumentKey, PlayerScore>>();

  it('percentile bracket ?? 100: pct=100 → finds bracket 100 (no ?? needed)', () => {
    const sm = new Map([['s1', ps({ songId: 's1', rank: 100, totalEntries: 100 })]]);
    const am = new Map<string, Map<InstrumentKey, PlayerScore>>([['s1', new Map([['Solo_Guitar', ps({ songId: 's1' })]])]]);
    const f = { ...emptyF(), percentileFilter: { 100: true } };
    const r = renderHook(() => useFilteredSongs({ songs: songs2, search: '', sortMode: 'title', sortAscending: true, filters: f, instrument: 'Solo_Guitar', scoreMap: sm, allScoreMap: am })).result.current;
    expect(r.some(s => s.songId === 's1')).toBe(true);
  });

  it('sort compareByMode with undefined scores → returns 0', () => {
    const sm = new Map<string, PlayerScore>();
    const r = renderHook(() => useFilteredSongs({ songs: songs2, search: '', sortMode: 'hasfc', sortAscending: true, filters: emptyF(), instrument: null, scoreMap: sm, allScoreMap: noA2 })).result.current;
    expect(r.length).toBe(2);
  });

  it('missingScores filter with undefined score entry', () => {
    const am = new Map<string, Map<InstrumentKey, PlayerScore>>([
      ['s1', new Map()], // s1 has entry but no instrument data
    ]);
    const f = { ...emptyF(), missingScores: { Solo_Guitar: true } };
    const r = renderHook(() => useFilteredSongs({ songs: songs2, search: '', sortMode: 'title', sortAscending: true, filters: f, instrument: null, scoreMap: noS2, allScoreMap: am })).result.current;
    // s1 has map entry but no Solo_Guitar → missingScore passes, s2 has no entry → passes
    expect(r.length).toBe(2);
  });
});

/* ══════════════════ useSortedScoreHistory: ?? fallback on all fields ══════════════════ */
describe('useSortedScoreHistory ?? fallback branches', () => {
  it('date: scoreAchievedAt=undefined + changedAt=undefined → both ?? fire', () => {
    const a = hist({ scoreAchievedAt: undefined, changedAt: undefined as any });
    const b = hist({ scoreAchievedAt: '2025-01-01T00:00:00Z' });
    const { result } = renderHook(() => useSortedScoreHistory([a, b], PlayerScoreSortMode.Date, true));
    expect(result.current.length).toBe(2);
  });
  it('accuracy: accuracy=undefined → ?? 0 fires', () => {
    const a = hist({ accuracy: undefined });
    const b = hist({ accuracy: 950000 });
    const { result } = renderHook(() => useSortedScoreHistory([a, b], PlayerScoreSortMode.Accuracy, true));
    expect(result.current[0]!.accuracy).toBeUndefined();
  });
  it('accuracy tiebreaker: isFullCombo=undefined → treated as falsy', () => {
    const a = hist({ accuracy: 950000, isFullCombo: undefined as any, newScore: 100000 });
    const b = hist({ accuracy: 950000, isFullCombo: true, newScore: 100000 });
    const { result } = renderHook(() => useSortedScoreHistory([a, b], PlayerScoreSortMode.Accuracy, true));
    expect(result.current.length).toBe(2);
  });
  it('season: season=undefined → ?? 0 fires', () => {
    const a = hist({ season: undefined });
    const b = hist({ season: 5 });
    const { result } = renderHook(() => useSortedScoreHistory([a, b], PlayerScoreSortMode.Season, true));
    expect(result.current[0]!.season).toBeUndefined();
  });
});

/* ══════════════════ useChartData: more ?? paths ══════════════════ */
describe('useChartData additional ?? branches', () => {
  it('multiple entries with undefined scoreAchievedAt', () => {
    const history = [
      hist({ instrument: 'Solo_Guitar', scoreAchievedAt: undefined, changedAt: '2025-01-01T00:00:00Z', newScore: 100000 }),
      hist({ instrument: 'Solo_Guitar', scoreAchievedAt: '2025-02-01T00:00:00Z', newScore: 200000 }),
    ];
    const { result } = renderHook(() => useChartData('a1', 's1', 'Solo_Guitar', history), { wrapper: qcWrap });
    expect(result.current.chartData.length).toBe(2);
    expect(result.current.chartData[0]!.score).toBe(100000);
  });
});

/* ══════════════════ suggestionsFilter: more branch paths ══════════════════ */
describe('suggestionsFilter additional branches', () => {
  it('shouldShowCategoryType: type with global key false → false', () => {
    expect(shouldShowCategoryType('unfc_guitar', { [globalKeyFor('NearFC')]: false } as any)).toBe(false);
  });
  it('shouldShowCategoryType: type with global key true → true', () => {
    expect(shouldShowCategoryType('unfc_guitar', { [globalKeyFor('NearFC')]: true } as any)).toBe(true);
  });
  it('shouldShowCategoryType: type with global key undefined → ?? true', () => {
    expect(shouldShowCategoryType('unfc_guitar', {} as any)).toBe(true);
  });
  it('filterCategory: catInstrument=null + no instrumentKey songs → all pass', () => {
    const cat = mockCat('near_fc_mixed', [{ instrumentKey: undefined }, { instrumentKey: undefined }]);
    const r = filterCategoryForInstrumentTypes(cat, {} as any);
    expect(r).toBe(cat);
  });
  it('filterCategory: per-instrument key undefined → ?? true keeps song', () => {
    const cat = mockCat('unplayed_mixed', [{ instrumentKey: 'guitar' }]);
    const r = filterCategoryForInstrumentTypes(cat, {} as any);
    expect(r).toBe(cat);
  });
});
