import type {InstrumentKey} from '../instruments';
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

const Instruments: InstrumentKey[] = ['guitar', 'bass', 'drums', 'vocals', 'pro_guitar', 'pro_bass'];
const canon = (s: string | undefined): string => (s ?? '').trim().toLowerCase();

const pct = (t: ScoreTracker | null | undefined): number | undefined => {
  if (!t || t.percentHit <= 0) return undefined;
  return t.percentHit / 10000;
};

const stars = (t: ScoreTracker | null | undefined): number | undefined => {
  if (!t || t.numStars <= 0) return undefined;
  return t.numStars;
};

const getDecadeStart = (year: number | undefined): number | undefined => {
  if (!year || year < 1970 || year > 2099) return undefined;
  return Math.floor(year / 10) * 10;
};

const decadeLabel = (decadeStart: number): string => {
  const two = decadeStart % 100;
  if (two === 0) return "00's";
  return `${String(two).padStart(2, '0')}'s`;
};

const instrumentLabel = (instrument: InstrumentKey | 'any'): string => {
  switch (instrument) {
    case 'guitar':
      return 'Guitar';
    case 'bass':
      return 'Bass';
    case 'drums':
      return 'Drums';
    case 'vocals':
      return 'Vocals';
    case 'pro_guitar':
      return 'Pro Guitar';
    case 'pro_bass':
      return 'Pro Bass';
    default:
      return String(instrument);
  }
};

const getTracker = (board: LeaderboardData | undefined, instrument: InstrumentKey): ScoreTracker | undefined => {
  if (!board) return undefined;
  return (board as any)[instrument] as ScoreTracker | undefined;
};

export type SuggestionGeneratorOptions = {
  seed?: number;
  rng?: Rng;
  disableSkipping?: boolean;
  fixedDisplayCount?: number;
};

type SongPair = {song: Song; tracker: ScoreTracker | null; instrumentKey?: InstrumentKey};

export class SuggestionGenerator {
  private readonly rng: Rng;
  private readonly disableSkipping: boolean;
  private readonly fixedDisplayCount?: number;

  private songs: Song[] = [];
  private scoresIndex: Readonly<Record<string, LeaderboardData | undefined>> = {};

  private emitted = new Set<string>();
  private pipelines: Array<() => SuggestionCategory[]> = [];
  private initialized = false;

  private readonly sessionShownSongs = new Set<string>();
  private readonly recentSongIds: string[] = [];
  private readonly recentArtists: string[] = [];
  private readonly categorySongHistory = new Map<string, Set<string>>();
  private readonly categorySkipStreak = new Map<string, number>();
  private readonly firstPlaysMixedLastInstrument = new Map<string, InstrumentKey>();

  constructor(opts: SuggestionGeneratorOptions = {}) {
    this.rng = opts.rng ?? createSeededRng(opts.seed ?? 1);
    this.disableSkipping = opts.disableSkipping ?? false;
    this.fixedDisplayCount = opts.fixedDisplayCount;
  }

  setSource(songs: Song[], scoresIndex: Readonly<Record<string, LeaderboardData | undefined>>): void {
    this.songs = songs;
    this.scoresIndex = scoresIndex;
  }

  private shuffleInPlace<T>(arr: T[]): void {
    for (let i = arr.length - 1; i > 0; i--) {
      const j = this.rng.nextInt(i + 1);
      [arr[i], arr[j]] = [arr[j], arr[i]];
    }
  }

  private shuffleAndTake<T>(source: T[], max: number): T[] {
    const list = [...source];
    this.shuffleInPlace(list);
    return list.length > max ? list.slice(0, max) : list;
  }

  private getDisplayCount(): number {
    if (this.fixedDisplayCount != null) return Math.max(1, Math.floor(this.fixedDisplayCount));
    // 2-5 inclusive
    return 2 + this.rng.nextInt(4);
  }

  private getFreshCount(pool: SongPair[]): number {
    return pool.filter(x => !this.sessionShownSongs.has(x.song.track.su)).length;
  }

  private getFreshSongCount(pool: Song[]): number {
    return pool.filter(s => !this.sessionShownSongs.has(s.track.su)).length;
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

  private selectNewFirst(categoryKey: string, pool: SongPair[], take: number): SongPair[] {
    const list = [...pool];
    if (list.length === 0 || take <= 0) return [];

    const isFirstPlaysMixedCategory = categoryKey === 'first_plays_mixed' || categoryKey.startsWith('first_plays_mixed_');
    const historyId = (p: SongPair): string =>
      isFirstPlaysMixedCategory ? `${p.song.track.su}:${p.instrumentKey ?? 'any'}` : p.song.track.su;
    const sessionId = historyId;

    let used = this.categorySongHistory.get(categoryKey);
    if (!used) {
      used = new Set<string>();
      this.categorySongHistory.set(categoryKey, used);
    }

    if (list.every(x => used!.has(historyId(x)))) used.clear();

    const freshNew = list.filter(x => !this.sessionShownSongs.has(sessionId(x)) && !used!.has(historyId(x)));
    this.shuffleInPlace(freshNew);

    const freshNewIds = new Set(freshNew.map(x => x.song.track.su));
    const categoryNew = list.filter(x => !used!.has(historyId(x)) && !freshNewIds.has(x.song.track.su));
    this.shuffleInPlace(categoryNew);

    const oldOnes = list.filter(x => used!.has(historyId(x)));
    this.shuffleInPlace(oldOnes);

    const result: SongPair[] = [];
    const chosenSongs = new Set<string>();

    for (const x of freshNew) {
      const id = x.song.track.su;
      if (chosenSongs.has(id)) continue;
      chosenSongs.add(id);
      result.push(x);
      if (result.length === take) break;
    }
    if (result.length < take) {
      for (const x of categoryNew) {
        const id = x.song.track.su;
        if (chosenSongs.has(id)) continue;
        chosenSongs.add(id);
        result.push(x);
        if (result.length === take) break;
      }
    }
    if (result.length < take) {
      for (const x of oldOnes) {
        const id = x.song.track.su;
        if (chosenSongs.has(id)) continue;
        chosenSongs.add(id);
        result.push(x);
        if (result.length === take) break;
      }
    }

    for (const r of result) {
      used.add(historyId(r));
      this.sessionShownSongs.add(sessionId(r));
    }
    return result;
  }

  private getUnplayedInstruments(song: Song): InstrumentKey[] {
    const b = this.scoresIndex[song.track.su];
    const out: InstrumentKey[] = [];
    for (const ins of Instruments) {
      const tr = b ? getTracker(b, ins) : undefined;
      if (!tr || tr.numStars === 0) out.push(ins);
    }
    return out;
  }

  private buildFirstPlaysMixedPool(): SongPair[] {
    const pool: SongPair[] = [];
    for (const s of this.songs) {
      const unplayed = this.getUnplayedInstruments(s);
      if (unplayed.length === 0) continue;

      const last = this.firstPlaysMixedLastInstrument.get(s.track.su);
      const candidates = last && unplayed.length > 1 ? unplayed.filter(i => i !== last) : unplayed;
      for (const ins of candidates) {
        pool.push({song: s, tracker: null, instrumentKey: ins});
      }
    }
    return pool;
  }

  private findSong(id: string): Song | undefined {
    return this.songs.find(s => s.track.su === id);
  }

  private eachTracker(
    song: Song | undefined,
    board: LeaderboardData | undefined,
    predicate: (t: ScoreTracker, instrument: InstrumentKey) => boolean,
  ): SongPair[] {
    if (!board) return [];
    const resolvedSong = song ?? this.findSong(board.songId);
    if (!resolvedSong) return [];

    const out: SongPair[] = [];
    for (const instrument of Instruments) {
      const tr = getTracker(board, instrument);
      if (tr && predicate(tr, instrument)) out.push({song: resolvedSong, tracker: tr, instrumentKey: instrument});
    }
    return out;
  }

  private mapSong(pair: SongPair): SuggestionSongItem {
    const song = pair.song;
    return {
      songId: song.track.su,
      title: song.track.tt ?? song._title ?? '(unknown)',
      artist: song.track.an ?? '(unknown)',
      stars: stars(pair.tracker),
      percent: pct(pair.tracker),
      fullCombo: pair.tracker ? pair.tracker.isFullCombo : undefined,
    };
  }

  private mapSongWithInstrument(pair: SongPair): SuggestionSongItem {
    const base = this.mapSong(pair);
    return pair.instrumentKey ? {...base, instrumentKey: pair.instrumentKey} : base;
  }

  private addRecentSong(songId: string, artist: string | undefined): void {
    this.recentSongIds.push(songId);
    while (this.recentSongIds.length > 40) this.recentSongIds.shift();
    this.enqueueRecentArtist(artist);
  }

  private enqueueRecentArtist(artist: string | undefined): void {
    const a = canon(artist);
    if (!a) return;
    this.recentArtists.push(a);
    while (this.recentArtists.length > 12) this.recentArtists.shift();
  }

  private isSongRecentlyUsed(songId: string): boolean {
    return this.recentSongIds.includes(songId);
  }

  private mapUniqueSong(pair: SongPair): SuggestionSongItem {
    this.addRecentSong(pair.song.track.su, pair.song.track.an);
    return this.mapSong(pair);
  }

  private mapUniqueSongWithInstrument(pair: SongPair): SuggestionSongItem {
    this.addRecentSong(pair.song.track.su, pair.song.track.an);
    return this.mapSongWithInstrument(pair);
  }

  private buildDecadeVariant(
    baseKey: string,
    baseTitle: string,
    baseDescription: string,
    pool: SongPair[],
  ): SuggestionCategory[] {
    const valid = pool.filter(p => p.song?.track && (p.song.track.ry ?? 0) > 0);

    if (valid.length < 2) return [];

    const byDecade = new Map<number, SongPair[]>();
    for (const p of valid) {
      const dec = getDecadeStart(p.song.track.ry);
      if (dec == null) continue;
      const list = byDecade.get(dec) ?? [];
      list.push(p);
      byDecade.set(dec, list);
    }

    const decadeGroups = [...byDecade.entries()].filter(([, items]) => items.length >= 2);
    if (decadeGroups.length === 0) return [];

    this.shuffleInPlace(decadeGroups);
    const [decadeStart, chosen] = decadeGroups[0];
    const label = decadeLabel(decadeStart);
    const take = this.getDisplayCount();
    const variantKey = `${baseKey}_decade_${String(decadeStart % 100).padStart(2, '0')}`;
    const selection = this.selectNewFirst(variantKey, chosen, take);
    if (selection.length < 2) return [];

    if (baseKey === 'first_plays_mixed') {
      for (const p of selection) {
        if (p.instrumentKey) this.firstPlaysMixedLastInstrument.set(p.song.track.su, p.instrumentKey);
      }
    }

    let title = `${baseTitle} (${label})`;
    if (baseKey === 'more_stars') title = `Push These ${label} Songs to Gold Stars`;
    else if (baseKey.startsWith('unfc_')) {
      const instr = baseKey.substring(5) as InstrumentKey;
      title = `Close ${instrumentLabel(instr)} FCs on Songs From the ${label}`;
    } else if (baseKey.startsWith('unplayed_')) {
      const instr = baseKey.substring(9);
      title = instr === 'any' ? `First Plays from the ${label}` : `First ${instrumentLabel(instr as any)} Plays (${label})`;
    } else if (baseKey === 'first_plays_mixed') title = `First Plays (Mixed ${label})`;
    else if (baseKey === 'near_fc_relaxed') title = `Close to FC (92%+) - ${label}`;
    else if (baseKey === 'near_fc_any') title = `FC These Next! (${label})`;
    else if (baseKey === 'almost_six_star') title = `Push ${label} Songs to Gold Stars`;
    else if (baseKey === 'star_gains') title = `Easy Star Gains (${label})`;

    let desc = `${baseDescription} Limited to ${label} songs.`;
    if (baseKey === 'unplayed_any') desc = `Unplayed songs from the ${label}.`;
    if (baseKey.startsWith('unplayed_') && baseKey !== 'unplayed_any') {
      const instr = baseKey.substring(9) as InstrumentKey;
      desc = `Unplayed ${instrumentLabel(instr)} songs from the ${label}.`;
    }

    return [
      {
        key: variantKey,
        title,
        description: desc,
        songs: selection.map(p => (
          baseKey === 'near_fc_any' ||
          baseKey === 'near_fc_relaxed' ||
          baseKey === 'almost_six_star' ||
          baseKey === 'more_stars' ||
          baseKey === 'first_plays_mixed' ||
          baseKey === 'star_gains'
            ? this.mapUniqueSongWithInstrument(p)
            : this.mapUniqueSong(p)
        )),
      },
    ];
  }

  private ensurePipelines(): void {
    if (this.initialized) return;
    this.initialized = true;
    const list: Array<() => SuggestionCategory[]> = [
      () => this.fcTheseNext(),
      () => this.fcTheseNextDecade(),
      () => this.nearFcRelaxed(),
      () => this.nearFcRelaxedDecade(),
      () => this.almostSixStars(),
      () => this.almostSixStarsDecade(),
      () => this.starGains(),
      () => this.starGainsDecade(),
      () => this.unFcInstrument('guitar'),
      () => this.unFcInstrumentDecade('guitar'),
      () => this.unFcInstrument('bass'),
      () => this.unFcInstrumentDecade('bass'),
      () => this.unFcInstrument('drums'),
      () => this.unFcInstrumentDecade('drums'),
      () => this.unFcInstrument('vocals'),
      () => this.unFcInstrumentDecade('vocals'),
      () => this.unFcInstrument('pro_guitar'),
      () => this.unFcInstrumentDecade('pro_guitar'),
      () => this.unFcInstrument('pro_bass'),
      () => this.unFcInstrumentDecade('pro_bass'),
      () => this.firstPlaysMixed(),
      () => this.firstPlaysMixedDecade(),
      () => this.unplayedAll(),
      () => this.unplayedAllDecade(),
      () => this.varietyPack(),
      () => this.artistSamplerRotating(),
      () => this.getMoreStars(),
      () => this.getMoreStarsDecade(),
      () => this.unplayedInstrument('guitar'),
      () => this.unplayedInstrumentDecade('guitar'),
      () => this.unplayedInstrument('bass'),
      () => this.unplayedInstrumentDecade('bass'),
      () => this.unplayedInstrument('drums'),
      () => this.unplayedInstrumentDecade('drums'),
      () => this.unplayedInstrument('vocals'),
      () => this.unplayedInstrumentDecade('vocals'),
      () => this.unplayedInstrument('pro_guitar'),
      () => this.unplayedInstrumentDecade('pro_guitar'),
      () => this.unplayedInstrument('pro_bass'),
      () => this.unplayedInstrumentDecade('pro_bass'),
      () => this.artistFocusUnplayed(),
      () => this.sameNameSets(),
      () => this.sameNameNearFc(),
    ];
    this.shuffleInPlace(list);
    this.pipelines = list;
  }

  getNext(count: number, songs?: Song[], scoresIndex?: Readonly<Record<string, LeaderboardData | undefined>>): SuggestionCategory[] {
    if (songs && scoresIndex) this.setSource(songs, scoresIndex);
    this.ensurePipelines();

    const produced: SuggestionCategory[] = [];
    let safety = 0;

    while (produced.length < count && this.pipelines.length > 0 && safety < 500) {
      safety++;
      const pipe = this.pipelines.shift();
      if (!pipe) break;
      for (const cat of pipe()) {
        if (!cat.songs || cat.songs.length === 0) continue;
        if (this.emitted.has(cat.key)) continue;
        this.emitted.add(cat.key);
        produced.push(cat);
        if (produced.length >= count) break;
      }
    }

    return produced;
  }

  resetForEndless(): void {
    this.initialized = false;
    this.pipelines = [];
    this.emitted.clear();
    this.sessionShownSongs.clear();
    this.ensurePipelines();
  }

  // Back-compat helper: generate a single page of categories.
  generate(songs: Song[], scoresIndex: Readonly<Record<string, LeaderboardData | undefined>>): SuggestionCategory[] {
    // Keep existing behavior: do not auto-reset internal state.
    // Callers that want a “fresh page” should create a new generator or call resetForEndless().
    return this.getNext(50, songs, scoresIndex);
  }

  // --- Strategy implementations (ported from MAUI) ---
  private fcTheseNext(): SuggestionCategory[] {
    const pool: SongPair[] = [];
    for (const s of this.songs) {
      const board = this.scoresIndex[s.track.su];
      pool.push(
        ...this.eachTracker(s, board, t => t.numStars === 6 && !t.isFullCombo && t.percentHit >= 950000),
      );
    }
    this.shuffleInPlace(pool);
    const freshCount = this.getFreshCount(pool);
    if (!this.shouldEmit('near_fc_any', freshCount)) return [];
    const take = this.getDisplayCount();
    const final = this.selectNewFirst('near_fc_any', pool, take);
    return [
      {
        key: 'near_fc_any',
        title: 'FC These Next!',
        description: 'High accuracy Gold Star runs that just need the full combo.',
        songs: final.map(p => this.mapUniqueSongWithInstrument(p)),
      },
    ];
  }

  private fcTheseNextDecade(): SuggestionCategory[] {
    const pool: SongPair[] = [];
    for (const s of this.songs) {
      const board = this.scoresIndex[s.track.su];
      pool.push(
        ...this.eachTracker(s, board, t => t.numStars === 6 && !t.isFullCombo && t.percentHit >= 950000),
      );
    }
    const freshCount = this.getFreshCount(pool);
    if (!this.shouldEmit('near_fc_any_decade_wrap', freshCount)) return [];
    return this.buildDecadeVariant('near_fc_any', 'FC These Next!', 'High accuracy Gold Star runs that just need the full combo.', pool);
  }

  private nearFcRelaxed(): SuggestionCategory[] {
    const pool: SongPair[] = [];
    for (const s of this.songs) {
      const board = this.scoresIndex[s.track.su];
      pool.push(
        ...this.eachTracker(s, board, t => t.numStars >= 5 && t.percentHit >= 920000 && !t.isFullCombo),
      );
    }
    this.shuffleInPlace(pool);
    const freshCount = this.getFreshCount(pool);
    if (!this.shouldEmit('near_fc_relaxed', freshCount)) return [];
    const take = this.getDisplayCount();
    const final = this.selectNewFirst('near_fc_relaxed', pool, take);
    return [
      {
        key: 'near_fc_relaxed',
        title: 'Close to FC (92%+)',
        description: 'High accuracy 5★/Gold Star runs to polish.',
        songs: final.map(p => this.mapUniqueSongWithInstrument(p)),
      },
    ];
  }

  private nearFcRelaxedDecade(): SuggestionCategory[] {
    const pool: SongPair[] = [];
    for (const s of this.songs) {
      const board = this.scoresIndex[s.track.su];
      pool.push(
        ...this.eachTracker(s, board, t => t.numStars >= 5 && t.percentHit >= 920000 && !t.isFullCombo),
      );
    }
    const freshCount = this.getFreshCount(pool);
    if (!this.shouldEmit('near_fc_relaxed_decade_wrap', freshCount)) return [];
    return this.buildDecadeVariant('near_fc_relaxed', 'Close to FC (92%+)', 'High accuracy 5★/Gold Star runs to polish.', pool);
  }

  private almostSixStars(): SuggestionCategory[] {
    const list: SongPair[] = [];
    for (const s of this.songs) {
      const board = this.scoresIndex[s.track.su];
      list.push(...this.eachTracker(s, board, t => t.numStars === 5 && t.percentHit >= 900000));
    }
    this.shuffleInPlace(list);
    const freshCount = this.getFreshCount(list);
    if (!this.shouldEmit('almost_six_star', freshCount)) return [];
    const take = this.getDisplayCount();
    const final = this.selectNewFirst('almost_six_star', list, take);
    return [
      {
        key: 'almost_six_star',
        title: 'Push to Gold Stars',
        description: 'High 5★ runs close to Gold Stars.',
        songs: final.map(p => this.mapUniqueSongWithInstrument(p)),
      },
    ];
  }

  private almostSixStarsDecade(): SuggestionCategory[] {
    const list: SongPair[] = [];
    for (const s of this.songs) {
      const board = this.scoresIndex[s.track.su];
      list.push(...this.eachTracker(s, board, t => t.numStars === 5 && t.percentHit >= 900000));
    }
    const freshCount = this.getFreshCount(list);
    if (!this.shouldEmit('almost_six_star_decade_wrap', freshCount)) return [];
    return this.buildDecadeVariant('almost_six_star', 'Push to Gold Stars', 'High 5★ runs close to Gold Stars.', list);
  }

  private starGains(): SuggestionCategory[] {
    const list: SongPair[] = [];
    for (const s of this.songs) {
      const board = this.scoresIndex[s.track.su];
      list.push(...this.eachTracker(s, board, t => t.numStars >= 3 && t.numStars < 6));
    }
    this.shuffleInPlace(list);
    const freshCount = this.getFreshCount(list);
    if (!this.shouldEmit('star_gains', freshCount)) return [];
    const take = this.getDisplayCount();
    const final = this.selectNewFirst('star_gains', list, take);
    return [
      {
        key: 'star_gains',
        title: 'Easy Star Gains',
        description: 'Mid-star songs ripe for improvement.',
        songs: final.map(p => this.mapUniqueSongWithInstrument(p)),
      },
    ];
  }

  private starGainsDecade(): SuggestionCategory[] {
    const list: SongPair[] = [];
    for (const s of this.songs) {
      const board = this.scoresIndex[s.track.su];
      list.push(...this.eachTracker(s, board, t => t.numStars >= 3 && t.numStars < 6));
    }
    const freshCount = this.getFreshCount(list);
    if (!this.shouldEmit('star_gains_decade_wrap', freshCount)) return [];
    return this.buildDecadeVariant('star_gains', 'Easy Star Gains', 'Mid-star songs ripe for improvement.', list);
  }

  private unFcInstrument(instrument: InstrumentKey): SuggestionCategory[] {
    const list: SongPair[] = [];
    for (const s of this.songs) {
      const tr = getTracker(this.scoresIndex[s.track.su], instrument);
      if (tr && tr.numStars === 6 && !tr.isFullCombo) list.push({song: s, tracker: tr});
    }
    this.shuffleInPlace(list);
    const freshCount = this.getFreshCount(list);
    const key = `unfc_${instrument}`;
    if (!this.shouldEmit(key, freshCount)) return [];
    const take = this.getDisplayCount();
    const final = this.selectNewFirst(key, list, take);
    return [
      {
        key,
        title: `Finish the ${instrumentLabel(instrument)} FCs`,
        description: `Clean up these almost full combos on ${instrumentLabel(instrument)}.`,
        songs: final.map(p => this.mapUniqueSong(p)),
      },
    ];
  }

  private unFcInstrumentDecade(instrument: InstrumentKey): SuggestionCategory[] {
    const list: SongPair[] = [];
    for (const s of this.songs) {
      const tr = getTracker(this.scoresIndex[s.track.su], instrument);
      if (tr && tr.numStars === 6 && !tr.isFullCombo) list.push({song: s, tracker: tr});
    }
    const freshCount = this.getFreshCount(list);
    const wrapKey = `unfc_${instrument}_decade_wrap`;
    if (!this.shouldEmit(wrapKey, freshCount)) return [];
    return this.buildDecadeVariant(
      `unfc_${instrument}`,
      `Finish the ${instrumentLabel(instrument)} FCs`,
      `Clean up these almost full combos on ${instrumentLabel(instrument)}.`,
      list,
    );
  }

  private getMoreStars(): SuggestionCategory[] {
    const list: SongPair[] = [];
    for (const ld of Object.values(this.scoresIndex)) {
      if (!ld) continue;
      list.push(...this.eachTracker(undefined, ld, t => t.numStars >= 1 && t.numStars < 6));
    }
    this.shuffleInPlace(list);
    const freshCount = this.getFreshCount(list);
    if (!this.shouldEmit('more_stars', freshCount)) return [];
    const take = this.getDisplayCount();
    const final = this.selectNewFirst('more_stars', list, take);
    return [
      {
        key: 'more_stars',
        title: 'Push These to Gold Stars',
        description: 'Improve star ratings toward Gold Stars across any instrument.',
        songs: final.map(p => this.mapUniqueSongWithInstrument(p)),
      },
    ];
  }

  private getMoreStarsDecade(): SuggestionCategory[] {
    const list: SongPair[] = [];
    for (const ld of Object.values(this.scoresIndex)) {
      if (!ld) continue;
      list.push(...this.eachTracker(undefined, ld, t => t.numStars >= 1 && t.numStars < 6));
    }
    const freshCount = this.getFreshCount(list);
    if (!this.shouldEmit('more_stars_decade_wrap', freshCount)) return [];
    return this.buildDecadeVariant('more_stars', 'Push These to Gold Stars', 'Improve star ratings toward Gold Stars across any instrument.', list);
  }

  private unplayedAll(): SuggestionCategory[] {
    const list = this.songs.filter(s => !this.scoresIndex[s.track.su]);
    this.shuffleInPlace(list);
    const freshCount = this.getFreshSongCount(list);
    if (!this.shouldEmit('unplayed_any', freshCount)) return [];
    const take = this.getDisplayCount();
    const final = this.selectNewFirst('unplayed_any', list.map(s => ({song: s, tracker: null})), take);
    if (final.length === 0) return [];
    return [
      {
        key: 'unplayed_any',
        title: 'Try Something New',
        description: "Songs you haven't played on any instrument yet.",
        songs: final.map(p => this.mapUniqueSong(p)),
      },
    ];
  }

  private unplayedAllDecade(): SuggestionCategory[] {
    const list = this.songs.filter(s => !this.scoresIndex[s.track.su]);
    const freshCount = this.getFreshSongCount(list);
    if (!this.shouldEmit('unplayed_any_decade_wrap', freshCount)) return [];
    return this.buildDecadeVariant('unplayed_any', 'Try Something New', "Songs you haven't played on any instrument yet.", list.map(s => ({song: s, tracker: null})));
  }

  private unplayedInstrument(instrument: InstrumentKey): SuggestionCategory[] {
    const list = this.songs
      .filter(s => {
        const b = this.scoresIndex[s.track.su];
        const tr = getTracker(b, instrument);
        return !b || !tr || tr.numStars === 0;
      });
    this.shuffleInPlace(list);
    const freshCount = this.getFreshSongCount(list);
    const key = `unplayed_${instrument}`;
    if (!this.shouldEmit(key, freshCount)) return [];
    const take = this.getDisplayCount();
    const final = this.selectNewFirst(key, list.map(s => ({song: s, tracker: null})), take);
    if (final.length === 0) return [];
    return [
      {
        key,
        title: `New on ${instrumentLabel(instrument)}`,
        description: `Never attempted on ${instrumentLabel(instrument)} yet.`,
        songs: final.map(p => this.mapUniqueSong(p)),
      },
    ];
  }

  private unplayedInstrumentDecade(instrument: InstrumentKey): SuggestionCategory[] {
    const list = this.songs
      .filter(s => {
        const b = this.scoresIndex[s.track.su];
        const tr = getTracker(b, instrument);
        return !b || !tr || tr.numStars === 0;
      });
    const freshCount = this.getFreshSongCount(list);
    const wrapKey = `unplayed_${instrument}_decade_wrap`;
    if (!this.shouldEmit(wrapKey, freshCount)) return [];
    return this.buildDecadeVariant(
      `unplayed_${instrument}`,
      `New on ${instrumentLabel(instrument)}`,
      `Never attempted on ${instrumentLabel(instrument)} yet.`,
      list.map(s => ({song: s, tracker: null})),
    );
  }

  private artistSamplerRotating(): SuggestionCategory[] {
    const groups = new Map<string, Song[]>();
    for (const s of this.songs) {
      const key = canon(s.track.an);
      const list = groups.get(key) ?? [];
      list.push(s);
      groups.set(key, list);
    }
    const artistGroups = [...groups.entries()].filter(([, items]) => items.length >= 3);
    if (artistGroups.length === 0) return [];
    this.shuffleInPlace(artistGroups);
    const chosen = artistGroups[0];
    this.enqueueRecentArtist(chosen[0]);
    let picked = chosen[1].slice().sort((a, b) => a.track.su.localeCompare(b.track.su)).slice(0, 10);
    this.shuffleInPlace(picked);
    if (picked.length > 5) picked = picked.slice(0, this.getDisplayCount());

    let artistName = picked[0]?.track.an ?? chosen[0];
    if (!artistName.trim() || artistName.trim().length <= 1) artistName = 'Featured Artist';
    if (picked.length === 0 || artistName === 'Featured Artist') return [];

    return [
      {
        key: `artist_sampler_${artistName}`,
        title: `${artistName} Essentials`,
        description: `Rotating focus: songs by ${artistName} (avoids recently featured artists).`,
        songs: picked.map(s => {
          const b = this.scoresIndex[s.track.su];
          const tr = b?.guitar ?? b?.drums ?? null;
          return this.mapUniqueSong({song: s, tracker: tr});
        }),
      },
    ];
  }

  private varietyPack(): SuggestionCategory[] {
    const sorted = [...this.songs]
      .sort((a, b) => {
        const c = canon(a.track.an).localeCompare(canon(b.track.an));
        if (c !== 0) return c;
        return a.track.su.localeCompare(b.track.su);
      });
    this.shuffleInPlace(sorted);

    const usedArtists = new Set<string>();
    const picks: Song[] = [];
    for (const s of sorted) {
      const aKey = canon(s.track.an);
      if (usedArtists.has(aKey)) continue;
      if (this.isSongRecentlyUsed(s.track.su)) continue;
      if (this.sessionShownSongs.has(s.track.su)) continue;
      usedArtists.add(aKey);
      picks.push(s);
      if (picks.length === 5) break;
    }

    const freshCount = this.getFreshSongCount(picks);
    if (!this.shouldEmit('variety_pack', freshCount)) return [];
    this.shuffleInPlace(picks);

    const selectedPairs = this.selectNewFirst(
      'variety_pack',
      picks.map(s => {
        const b = this.scoresIndex[s.track.su];
        return {song: s, tracker: b?.guitar ?? b?.drums ?? null};
      }),
      this.getDisplayCount(),
    );
    const display = selectedPairs.map(p => this.mapUniqueSong(p));
    if (display.length < 2) return [];

    let varietyDesc = 'Five different artists for variety.';
    if (display.length === 2) varietyDesc = 'Two different artists for variety.';
    else if (display.length === 3) varietyDesc = 'Three different artists for variety.';
    else if (display.length === 4) varietyDesc = 'Four different artists for variety.';

    return [
      {
        key: 'variety_pack',
        title: 'Variety Pack',
        description: varietyDesc,
        songs: display,
      },
    ];
  }

  private firstPlaysMixed(): SuggestionCategory[] {
    const pool = this.buildFirstPlaysMixedPool();
    if (pool.length === 0) return [];
    this.shuffleInPlace(pool);
    const finalPairs = this.selectNewFirst(
      'first_plays_mixed',
      pool,
      this.getDisplayCount(),
    );

    for (const p of finalPairs) {
      if (p.instrumentKey) this.firstPlaysMixedLastInstrument.set(p.song.track.su, p.instrumentKey);
    }

    if (finalPairs.length === 0) return [];
    return [
      {
        key: 'first_plays_mixed',
        title: 'First Plays (Mixed)',
        description: 'Unplayed picks across instruments.',
        songs: finalPairs.map(p => this.mapUniqueSongWithInstrument(p)),
      },
    ];
  }

  private firstPlaysMixedDecade(): SuggestionCategory[] {
    const pool = this.buildFirstPlaysMixedPool();
    const freshCount = this.getFreshSongCount(pool.map(p => p.song));
    if (!this.shouldEmit('first_plays_mixed_decade_wrap', freshCount)) return [];
    return this.buildDecadeVariant(
      'first_plays_mixed',
      'First Plays (Mixed)',
      'Unplayed picks across instruments.',
      pool,
    );
  }

  private artistFocusUnplayed(): SuggestionCategory[] {
    const unplayed = this.songs.filter(s => !this.scoresIndex[s.track.su]);
    if (unplayed.length === 0) return [];

    const groups = new Map<string, Song[]>();
    for (const s of unplayed) {
      const key = canon(s.track.an);
      const list = groups.get(key) ?? [];
      list.push(s);
      groups.set(key, list);
    }
    const entries = [...groups.entries()];
    this.shuffleInPlace(entries);
    const [artistKey, songs] = entries[0];
    const displayName = songs[0].track.an ?? 'Unknown Artist';

    const picked = this.selectNewFirst(
      `artist_unplayed_${artistKey}`,
      songs.map(s => ({song: s, tracker: null})),
      this.getDisplayCount(),
    );
    return [
      {
        key: `artist_unplayed_${artistKey}`,
        title: `Discover ${displayName}`,
        description: `Unplayed songs from ${displayName}.`,
        songs: picked.map(p => this.mapUniqueSong(p)),
      },
    ];
  }

  private sameNameSets(): SuggestionCategory[] {
    const groups = new Map<string, Song[]>();
    for (const s of this.songs) {
      const key = canon(s.track.tt ?? s._title);
      const list = groups.get(key) ?? [];
      list.push(s);
      groups.set(key, list);
    }
    const dupGroups = [...groups.entries()].filter(([, items]) => items.length >= 2);
    if (dupGroups.length === 0) return [];
    this.shuffleInPlace(dupGroups);
    const [, groupSongs] = dupGroups[0];
    const selected = this.selectNewFirst('samename', groupSongs.map(s => ({song: s, tracker: null})), this.getDisplayCount());
    const songs = selected.map(p => p.song);
    const displayTitle = (songs[0].track.tt ?? songs[0]._title ?? '').trim();

    return [
      {
        key: `samename_${displayTitle}`,
        title: `Songs Named '${displayTitle}'`,
        description: 'Different tracks sharing the same title.',
        songs: songs.map(s => this.mapUniqueSong({song: s, tracker: null})),
      },
    ];
  }

  private sameNameNearFc(): SuggestionCategory[] {
    const buckets = new Map<string, Array<{song: Song; board: LeaderboardData}>>();
    for (const s of this.songs) {
      const b = this.scoresIndex[s.track.su];
      if (!b) continue;
      const key = canon(s.track.tt ?? s._title);
      const list = buckets.get(key) ?? [];
      list.push({song: s, board: b});
      buckets.set(key, list);
    }

    const groups = [...buckets.entries()].filter(([, items]) => items.length >= 2);
    if (groups.length === 0) return [];
    this.shuffleInPlace(groups);

    const pickedGroup = groups[0];
    const disp = (pickedGroup[1][0].song.track.tt ?? pickedGroup[1][0].song._title ?? '').trim();

    const poolAll: SongPair[] = [];
    for (const x of pickedGroup[1]) {
      poolAll.push(...this.eachTracker(x.song, x.board, t => t.numStars === 6 && !t.isFullCombo && t.percentHit >= 900000));
    }
    this.shuffleInPlace(poolAll);
    const trimmed = poolAll.slice(0, 30);
    const pool = this.selectNewFirst('samename_nearfc', trimmed, this.getDisplayCount());

    return [
      {
        key: `samename_nearfc_${disp}`,
        title: `Close to FC: '${disp}' Variants`,
        description: "Same-name tracks nearly full combo'd.",
        songs: pool.map(p => this.mapUniqueSongWithInstrument(p)),
      },
    ];
  }
}
