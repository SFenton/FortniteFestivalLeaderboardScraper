import type {InstrumentKey} from '../instruments';
import {InstrumentKeys} from '../instruments';
import type {LeaderboardData, ScoreTracker, Song} from '../models';
import type {SuggestionCategory, SuggestionSongItem} from './types';

export type Rng = {nextInt: (maxExclusive: number) => number; nextDouble: () => number};

export const createSeededRng = (seed: number): Rng => {
  // Mulberry32
  let a = seed >>> 0;
  const next = () => {
    a |= 0;
    a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
  return {
    nextDouble: next,
    nextInt: (maxExclusive: number) => Math.floor(next() * maxExclusive),
  };
};

const canon = (s: string | undefined): string => (s ?? '').trim().toLowerCase();

const getDecadeStart = (year: number | undefined): number | undefined => {
  if (!year || year < 1970 || year > 2099) return undefined;
  return Math.floor(year / 10) * 10;
};

const decadeLabel = (decadeStart: number): string => {
  const two = decadeStart % 100;
  if (two === 0) return "00's";
  return `${String(two).padStart(2, '0')}'s`;
};

const pct = (t?: ScoreTracker): number | undefined => {
  if (!t || t.percentHit <= 0) return undefined;
  return t.percentHit / 10000;
};

const stars = (t?: ScoreTracker): number | undefined => {
  if (!t || t.numStars <= 0) return undefined;
  return t.numStars;
};

const selectTracker = (ld: LeaderboardData, key: InstrumentKey): ScoreTracker | undefined => {
  return (ld as any)[key] as ScoreTracker | undefined;
};

const toItem = (song: Song, tracker?: ScoreTracker): SuggestionSongItem => ({
  songId: song.track.su,
  title: song.track.tt ?? song._title ?? '(unknown)',
  artist: song.track.an ?? '(unknown)',
  stars: stars(tracker),
  percent: pct(tracker),
  fullCombo: tracker ? tracker.isFullCombo : undefined,
});

export type SuggestionGeneratorOptions = {
  seed?: number;
  rng?: Rng;
  disableSkipping?: boolean;
  fixedDisplayCount?: number;
};

export class SuggestionGenerator {
  private readonly rng: Rng;
  private readonly disableSkipping: boolean;
  private readonly fixedDisplayCount?: number;
  private readonly sessionShownSongs = new Set<string>();
  private readonly recentSongIds: string[] = [];
  private readonly recentArtists: string[] = [];
  private readonly categorySkipStreak = new Map<string, number>();

  constructor(opts: SuggestionGeneratorOptions = {}) {
    this.rng = opts.rng ?? createSeededRng(opts.seed ?? 1);
    this.disableSkipping = opts.disableSkipping ?? false;
    this.fixedDisplayCount = opts.fixedDisplayCount;
  }

  private getDisplayCount(): number {
    if (this.fixedDisplayCount != null) return Math.max(1, Math.floor(this.fixedDisplayCount));
    // 2-5 inclusive
    return 2 + this.rng.nextInt(4);
  }

  private shuffleInPlace<T>(arr: T[]): void {
    for (let i = arr.length - 1; i > 0; i--) {
      const j = this.rng.nextInt(i + 1);
      [arr[i], arr[j]] = [arr[j], arr[i]];
    }
  }

  private shouldEmit(key: string, candidateCount: number): boolean {
    if (this.disableSkipping) return candidateCount > 0;

    const table: Array<{min: number; prob: number}> = [
      {min: 80, prob: 1.0},
      {min: 50, prob: 0.98},
      {min: 35, prob: 0.95},
      {min: 25, prob: 0.9},
      {min: 18, prob: 0.85},
      {min: 12, prob: 0.75},
      {min: 8, prob: 0.62},
      {min: 5, prob: 0.5},
      {min: 0, prob: 0.38},
    ];
    let prob = 0.38;
    for (const row of table) {
      if (candidateCount >= row.min) {
        prob = row.prob;
        break;
      }
    }
    const skipped = this.categorySkipStreak.get(key) ?? 0;
    if (skipped >= 2) {
      this.categorySkipStreak.set(key, 0);
      return true;
    }
    const emit = this.rng.nextDouble() < prob;
    this.categorySkipStreak.set(key, emit ? 0 : skipped + 1);
    return emit;
  }

  private pushRecent(song: Song): void {
    const id = song.track.su;
    const artist = canon(song.track.an);

    this.recentSongIds.push(id);
    if (this.recentSongIds.length > 40) this.recentSongIds.shift();

    if (artist) {
      this.recentArtists.push(artist);
      if (this.recentArtists.length > 12) this.recentArtists.shift();
    }
  }

  private avoidRecentSongs(songs: Song[]): Song[] {
    const recent = new Set(this.recentSongIds);
    const fresh = songs.filter(s => !recent.has(s.track.su));
    return fresh.length > 0 ? fresh : songs;
  }

  private avoidRecentArtists(songs: Song[]): Song[] {
    const recent = new Set(this.recentArtists);
    const fresh = songs.filter(s => !recent.has(canon(s.track.an)));
    return fresh.length > 0 ? fresh : songs;
  }

  private pickSongs(songs: Song[], take: number): Song[] {
    const filtered = songs.filter(s => !this.sessionShownSongs.has(s.track.su));
    if (filtered.length === 0) return [];
    const shuffled = [...filtered];
    this.shuffleInPlace(shuffled);
    return shuffled.slice(0, take);
  }

  private buildDecadeVariant(
    baseKey: string,
    baseTitle: string,
    baseDescription: string,
    songs: Song[],
    scoresIndex: Record<string, LeaderboardData | undefined>,
    picker: (songsInDecade: Song[]) => Song[],
  ): SuggestionCategory[] {
    const byDecade = new Map<number, Song[]>();
    for (const s of songs) {
      const decade = getDecadeStart(s.track.ry);
      if (decade == null) continue;
      const list = byDecade.get(decade) ?? [];
      list.push(s);
      byDecade.set(decade, list);
    }
    const decades = [...byDecade.keys()].sort((a, b) => a - b);
    const out: SuggestionCategory[] = [];
    for (const decade of decades) {
      const decadeSongs = byDecade.get(decade) ?? [];
      const picked = picker(decadeSongs);
      if (picked.length === 0) continue;
      const key = `${baseKey}_${decade}`;
      if (!this.shouldEmit(key, decadeSongs.length)) continue;
      out.push({
        key,
        title: `${baseTitle} (${decadeLabel(decade)})`,
        description: baseDescription,
        songs: picked.map(s => toItem(s, scoresIndex[s.track.su]?.guitar)),
      });

      // Only after we actually emit, mark as shown + update recents.
      picked.forEach(s => this.sessionShownSongs.add(s.track.su));
      picked.forEach(s => this.pushRecent(s));
    }
    return out;
  }

  generate(songs: Song[], scoresIndex: Record<string, LeaderboardData | undefined>): SuggestionCategory[] {
    const categories: SuggestionCategory[] = [];
    const displayCount = this.getDisplayCount();

    const allSongs = songs.filter(s => s?.track?.su);

    // Category: “FC These Next” (lead/guitar focused, mirrors C# concept)
    {
      const pool = allSongs
        .map(s => ({song: s, tr: scoresIndex[s.track.su]?.guitar}))
        .filter(x => x.tr && !x.tr.isFullCombo && (pct(x.tr) ?? 0) >= 98)
        .map(x => x.song);
      const picked = this.pickSongs(this.avoidRecentSongs(pool), displayCount);
      if (picked.length > 0 && this.shouldEmit('fc_next', pool.length)) {
        const cat: SuggestionCategory = {
          key: 'fc_next',
          title: 'FC These Next',
          description: 'High accuracy, not FC yet — finish them off.',
          songs: picked.map(s => toItem(s, scoresIndex[s.track.su]?.guitar)),
        };
        categories.push(cat);
        picked.forEach(s => this.sessionShownSongs.add(s.track.su));
        picked.forEach(s => this.pushRecent(s));
      }
    }

    // Category: “Near FC (Relaxed)”
    {
      const pool = allSongs
        .map(s => ({song: s, tr: scoresIndex[s.track.su]?.guitar}))
        .filter(x => x.tr && !x.tr.isFullCombo && (pct(x.tr) ?? 0) >= 95)
        .map(x => x.song);
      const picked = this.pickSongs(this.avoidRecentSongs(pool), displayCount);
      if (picked.length > 0 && this.shouldEmit('near_fc', pool.length)) {
        categories.push({
          key: 'near_fc',
          title: 'Near FC',
          description: 'Close to full combo — practice these.',
          songs: picked.map(s => toItem(s, scoresIndex[s.track.su]?.guitar)),
        });
        picked.forEach(s => this.sessionShownSongs.add(s.track.su));
        picked.forEach(s => this.pushRecent(s));
      }
    }

    // Category: “Almost 6 Stars”
    {
      const pool = allSongs
        .map(s => ({song: s, tr: scoresIndex[s.track.su]?.guitar}))
        .filter(x => x.tr && (stars(x.tr) ?? 0) === 5 && (pct(x.tr) ?? 0) >= 98)
        .map(x => x.song);
      const picked = this.pickSongs(this.avoidRecentSongs(pool), displayCount);
      if (picked.length > 0 && this.shouldEmit('almost_six', pool.length)) {
        categories.push({
          key: 'almost_six',
          title: 'Almost 6 Stars',
          description: 'You’re extremely close to 6 stars on these.',
          songs: picked.map(s => toItem(s, scoresIndex[s.track.su]?.guitar)),
        });
        picked.forEach(s => this.sessionShownSongs.add(s.track.su));
        picked.forEach(s => this.pushRecent(s));
      }
    }

    // Category: “Unplayed (All Instruments)”
    {
      const pool = allSongs.filter(s => {
        const ld = scoresIndex[s.track.su];
        if (!ld) return true;
        return InstrumentKeys.every(k => {
          const tr = selectTracker(ld, k);
          return !tr || !tr.initialized;
        });
      });
      const picked = this.pickSongs(this.avoidRecentSongs(pool), displayCount);
      if (picked.length > 0 && this.shouldEmit('unplayed_all', pool.length)) {
        categories.push({
          key: 'unplayed_all',
          title: 'Unplayed Songs',
          description: 'Fresh songs with no recorded scores yet.',
          songs: picked.map(s => toItem(s)),
        });
        picked.forEach(s => this.sessionShownSongs.add(s.track.su));
        picked.forEach(s => this.pushRecent(s));
      }
    }

    // Category: “Artist Sampler” (avoid repeating artists)
    {
      const pool = this.avoidRecentArtists(allSongs);
      const shuffled = [...pool];
      this.shuffleInPlace(shuffled);
      const picked: Song[] = [];
      const usedArtists = new Set<string>();
      for (const s of shuffled) {
        const a = canon(s.track.an);
        if (a && usedArtists.has(a)) continue;
        if (this.sessionShownSongs.has(s.track.su)) continue;
        picked.push(s);
        if (a) usedArtists.add(a);
        if (picked.length >= displayCount) break;
      }
      if (picked.length > 0 && this.shouldEmit('artist_sampler', pool.length)) {
        categories.push({
          key: 'artist_sampler',
          title: 'Artist Sampler',
          description: 'A spread of different artists to keep things fresh.',
          songs: picked.map(s => toItem(s, scoresIndex[s.track.su]?.guitar)),
        });
        picked.forEach(s => this.sessionShownSongs.add(s.track.su));
        picked.forEach(s => this.pushRecent(s));
      }
    }

    // Decade variants for “FC These Next” (based on release year)
    categories.push(
      ...this.buildDecadeVariant(
        'fc_next_decade',
        'FC These Next',
        'High accuracy, not FC yet — finish them off.',
        allSongs,
        scoresIndex,
        decadeSongs => {
          const pool = decadeSongs
            .map(s => ({song: s, tr: scoresIndex[s.track.su]?.guitar}))
            .filter(x => x.tr && !x.tr.isFullCombo && (pct(x.tr) ?? 0) >= 98)
            .map(x => x.song);
          return this.pickSongs(this.avoidRecentSongs(pool), Math.min(displayCount, 4));
        },
      ),
    );

    return categories;
  }
}
