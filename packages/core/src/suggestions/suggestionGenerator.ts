import type {InstrumentKey} from '../instruments';
import type {LeaderboardData, ScoreTracker, Song} from '../models';
import {songSupportsInstrument} from '../songAvailability';
import type {RivalDataIndex, SuggestionCategory, SuggestionSongItem} from './types';

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

const Instruments: InstrumentKey[] = ['guitar', 'bass', 'drums', 'vocals', 'pro_guitar', 'pro_bass', 'peripheral_vocals', 'peripheral_cymbals', 'peripheral_drums'];
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
      return 'Tap Vocals';
    case 'pro_guitar':
      return 'Pro Lead';
    case 'pro_bass':
      return 'Pro Bass';
    case 'peripheral_vocals':
      return 'Mic Mode';
    case 'peripheral_cymbals':
      return 'Pro Drums + Cymbals';
    case 'peripheral_drums':
      return 'Pro Drums';
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
  currentSeason?: number;
};

type SongPair = {song: Song; tracker: ScoreTracker | null; instrumentKey?: InstrumentKey};

export class SuggestionGenerator {
  private readonly rng: Rng;
  private readonly disableSkipping: boolean;
  private readonly fixedDisplayCount?: number;
  private readonly currentSeason: number;

  private songs: Song[] = [];
  private scoresIndex: Readonly<Record<string, LeaderboardData | undefined>> = {};
  private rivalData: RivalDataIndex | null = null;

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
    this.currentSeason = opts.currentSeason ?? 0;
  }

  setSource(songs: Song[], scoresIndex: Readonly<Record<string, LeaderboardData | undefined>>): void {
    this.songs = songs;
    this.scoresIndex = scoresIndex;
  }

  /** Inject rival data for rivalry-aware suggestion strategies. Pass null to disable. */
  setRivalData(data: RivalDataIndex | null): void {
    this.rivalData = data;
  }

  private shuffleInPlace<T>(arr: T[]): void {
    for (let i = arr.length - 1; i > 0; i--) {
      const j = this.rng.nextInt(i + 1);
      [arr[i], arr[j]] = [arr[j]!, arr[i]!];
    }
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
      if (!songSupportsInstrument(song, ins)) continue;
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
    const pctDisplay = pair.tracker?.leaderboardPercentileFormatted;
    return {
      songId: song.track.su,
      title: song.track.tt ?? song._title ?? '(unknown)',
      artist: song.track.an ?? '(unknown)',
      year: song.track.ry,
      stars: stars(pair.tracker),
      percent: pct(pair.tracker),
      fullCombo: pair.tracker ? pair.tracker.isFullCombo : undefined,
      ...(pctDisplay ? {percentileDisplay: pctDisplay} : undefined),
    };
  }

  private mapSongWithInstrument(pair: SongPair): SuggestionSongItem {
    const base = this.mapSong(pair);
    const withInstrument = pair.instrumentKey ? {...base, instrumentKey: pair.instrumentKey} : base;
    // Cross-pollination: annotate with closest rival if available
    const ann = this.annotateWithRival(pair.song.track.su, pair.instrumentKey);
    return Object.keys(ann).length > 0 ? {...withInstrument, ...ann} : withInstrument;
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
    const [decadeStart, chosen] = decadeGroups[0]!;
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
    if (baseKey === 'more_stars') title = `Push ${label} to Gold`;
    else if (baseKey.startsWith('unfc_')) {
      const instr = baseKey.substring(5) as InstrumentKey;
      title = `Close ${instrumentLabel(instr)} FCs (${label})`;
    } else if (baseKey.startsWith('unplayed_')) {
      const instr = baseKey.substring(9);
      title = instr === 'any' ? `First Plays (${label})` : `First ${instrumentLabel(instr as any)} Plays (${label})`;
    } else if (baseKey === 'first_plays_mixed') title = `First Plays (Mixed ${label})`;
    else if (baseKey === 'near_fc_relaxed') title = `Close to FC (92%+) - ${label}`;
    else if (baseKey === 'near_fc_any') title = `FC These Next! (${label})`;
    else if (baseKey === 'almost_six_star') title = `Push ${label} to Gold`;
    else if (baseKey === 'star_gains') title = `Easy Star Gains (${label})`;
    else if (baseKey === 'almost_elite') title = `Almost Elite (${label})`;
    else if (baseKey.startsWith('almost_elite_')) {
      const instr = baseKey.substring('almost_elite_'.length) as InstrumentKey;
      title = `Almost Elite on ${instrumentLabel(instr)} (${label})`;
    } else if (baseKey === 'pct_push') title = `Percentile Push (${label})`;
    else if (baseKey.startsWith('pct_push_')) {
      const instr = baseKey.substring('pct_push_'.length) as InstrumentKey;
      title = `Percentile Push: ${instrumentLabel(instr)} (${label})`;
    }

    let desc = `${baseDescription} Limited to ${label} songs.`;
    if (baseKey === 'almost_elite') desc = `You're in the top 5% on these ${label} songs — one good run could crack the top 1%.`;
    else if (baseKey.startsWith('almost_elite_')) {
      const instr = baseKey.substring('almost_elite_'.length) as InstrumentKey;
      desc = `Your ${instrumentLabel(instr)} scores on these ${label} songs are in the top 5% — push them into the top 1%.`;
    } else if (baseKey === 'pct_push') desc = `These ${label} scores are close to the next percentile bracket — replay them to climb.`;
    else if (baseKey.startsWith('pct_push_')) {
      const instr = baseKey.substring('pct_push_'.length) as InstrumentKey;
      desc = `Replay these ${label} ${instrumentLabel(instr)} songs to jump to the next percentile bracket.`;
    }
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
          baseKey === 'star_gains' ||
          baseKey === 'almost_elite' ||
          baseKey === 'pct_push' ||
          baseKey.startsWith('near_max_')
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
      () => this.unFcInstrument('peripheral_vocals'),
      () => this.unFcInstrumentDecade('peripheral_vocals'),
      () => this.unFcInstrument('peripheral_cymbals'),
      () => this.unFcInstrumentDecade('peripheral_cymbals'),
      () => this.unFcInstrument('peripheral_drums'),
      () => this.unFcInstrumentDecade('peripheral_drums'),
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
      () => this.unplayedInstrument('peripheral_vocals'),
      () => this.unplayedInstrumentDecade('peripheral_vocals'),
      () => this.unplayedInstrument('peripheral_cymbals'),
      () => this.unplayedInstrumentDecade('peripheral_cymbals'),
      () => this.unplayedInstrument('peripheral_drums'),
      () => this.unplayedInstrumentDecade('peripheral_drums'),
      () => this.almostElite(),
      () => this.almostEliteDecade(),
      () => this.almostEliteInstrument('guitar'),
      () => this.almostEliteInstrumentDecade('guitar'),
      () => this.almostEliteInstrument('bass'),
      () => this.almostEliteInstrumentDecade('bass'),
      () => this.almostEliteInstrument('drums'),
      () => this.almostEliteInstrumentDecade('drums'),
      () => this.almostEliteInstrument('vocals'),
      () => this.almostEliteInstrumentDecade('vocals'),
      () => this.almostEliteInstrument('pro_guitar'),
      () => this.almostEliteInstrumentDecade('pro_guitar'),
      () => this.almostEliteInstrument('pro_bass'),
      () => this.almostEliteInstrumentDecade('pro_bass'),
      () => this.almostEliteInstrument('peripheral_vocals'),
      () => this.almostEliteInstrumentDecade('peripheral_vocals'),
      () => this.almostEliteInstrument('peripheral_cymbals'),
      () => this.almostEliteInstrumentDecade('peripheral_cymbals'),
      () => this.almostEliteInstrument('peripheral_drums'),
      () => this.almostEliteInstrumentDecade('peripheral_drums'),
      () => this.percentilePush(),
      () => this.percentilePushDecade(),
      () => this.percentilePushInstrument('guitar'),
      () => this.percentilePushInstrumentDecade('guitar'),
      () => this.percentilePushInstrument('bass'),
      () => this.percentilePushInstrumentDecade('bass'),
      () => this.percentilePushInstrument('drums'),
      () => this.percentilePushInstrumentDecade('drums'),
      () => this.percentilePushInstrument('vocals'),
      () => this.percentilePushInstrumentDecade('vocals'),
      () => this.percentilePushInstrument('pro_guitar'),
      () => this.percentilePushInstrumentDecade('pro_guitar'),
      () => this.percentilePushInstrument('pro_bass'),
      () => this.percentilePushInstrumentDecade('pro_bass'),
      () => this.percentilePushInstrument('peripheral_vocals'),
      () => this.percentilePushInstrumentDecade('peripheral_vocals'),
      () => this.percentilePushInstrument('peripheral_cymbals'),
      () => this.percentilePushInstrumentDecade('peripheral_cymbals'),
      () => this.percentilePushInstrument('peripheral_drums'),
      () => this.percentilePushInstrumentDecade('peripheral_drums'),
      () => this.artistFocusUnplayed(),
      () => this.sameNameSets(),
      () => this.sameNameNearFc(),
      // Stale / untouched songs
      () => this.staleGlobal(1),
      () => this.staleGlobal(2),
      () => this.staleGlobal(3),
      () => this.staleGlobal(4),
      () => this.staleGlobal(5),
      ...Instruments.flatMap(ins => [
        () => this.staleInstrument(ins, 1),
        () => this.staleInstrument(ins, 2),
        () => this.staleInstrument(ins, 3),
        () => this.staleInstrument(ins, 4),
        () => this.staleInstrument(ins, 5),
      ]),
      // Percentile improvements
      () => this.samePercentileBucket(),
      ...[2, 3, 4, 5, 10, 15, 20, 25, 30, 40, 50].map(b => () => this.samePercentileBucketSpecific(b)),
      ...[2, 3, 4, 5, 10, 15, 20, 25, 30, 40, 50].map(b => () => this.percentileImproveBucket(b)),
      ...Instruments.flatMap(ins => [
        () => this.improveInstrumentRankings(ins),
        ...[2, 3, 4, 5, 10, 15, 20, 25, 30, 40, 50].map(b => () => this.percentileImproveInstrument(ins, b)),
      ]),
      // ─── Near max score strategies ─────
      () => this.nearMaxScore(0, 5000, '5k'),
      () => this.nearMaxScoreDecade(0, 5000, '5k'),
      () => this.nearMaxScore(5000, 10000, '10k'),
      () => this.nearMaxScoreDecade(5000, 10000, '10k'),
      () => this.nearMaxScore(10000, 15000, '15k'),
      () => this.nearMaxScoreDecade(10000, 15000, '15k'),
      // ─── Rival strategies (no-op when rivalData is null) ─────
      () => this.songRivalBattleground(),
      () => this.songRivalNearFc(),
      () => this.songRivalStale(),
      () => this.songRivalStarGains(),
      () => this.songRivalPctPush(),
      ...(this.rivalData?.songRivals ?? []).flatMap(r => [
        () => this.songRivalGap(r.accountId),
        () => this.songRivalProtect(r.accountId),
        () => this.songRivalSpotlight(r.accountId),
        () => this.songRivalSlipping(r.accountId),
        () => this.songRivalDominate(r.accountId),
      ]),
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
        description: 'If you can get gold stars, you can FC it!',
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
    return this.buildDecadeVariant('near_fc_any', 'FC These Next!', 'If you can get gold stars, you can FC it!', pool);
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
        description: 'Great runs to try and FC next!',
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
    return this.buildDecadeVariant('near_fc_relaxed', 'Close to FC (92%+)', 'Great runs to try and FC next!', pool);
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
        description: 'Push these five-star runs to gold stars!',
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
    return this.buildDecadeVariant('almost_six_star', 'Push to Gold Stars', 'Push these five-star runs to gold stars!', list);
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
        description: 'Hit a new high score to get even more stars on these songs!',
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
    return this.buildDecadeVariant('star_gains', 'Easy Star Gains', 'Hit a new high score to get even more stars on these songs!', list);
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
        description: `Play these songs again on ${instrumentLabel(instrument)} and grab an FC!`,
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
      `Play these songs again on ${instrumentLabel(instrument)} and grab an FC!`,
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
        description: 'Try gold-starring this selection of tracks!',
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
    return this.buildDecadeVariant('more_stars', 'Push These to Gold Stars', 'Try gold-starring this selection of tracks!', list);
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
        if (!songSupportsInstrument(s, instrument)) return false;
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
        description: `Songs you haven't played on ${instrumentLabel(instrument)} yet.`,
        songs: final.map(p => this.mapUniqueSong(p)),
      },
    ];
  }

  private unplayedInstrumentDecade(instrument: InstrumentKey): SuggestionCategory[] {
    const list = this.songs
      .filter(s => {
        if (!songSupportsInstrument(s, instrument)) return false;
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
      `Songs you haven't played on ${instrumentLabel(instrument)} yet.`,
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
    const chosen = artistGroups[0]!;
    this.enqueueRecentArtist(chosen[0]);
    let picked = chosen[1].slice().sort((a: Song, b: Song) => a.track.su.localeCompare(b.track.su)).slice(0, 10);
    this.shuffleInPlace(picked);
    if (picked.length > 5) picked = picked.slice(0, this.getDisplayCount());

    let artistName = picked[0]?.track.an ?? chosen[0];
    if (!artistName.trim() || artistName.trim().length <= 1) artistName = 'Featured Artist';
    if (picked.length === 0 || artistName === 'Featured Artist') return [];

    return [
      {
        key: `artist_sampler_${artistName}`,
        title: `${artistName} Essentials`,
        description: `A selection of songs by ${artistName}.`,
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
    const [artistKey, songs] = entries[0]!;
    const displayName = songs[0]!.track.an ?? 'Unknown Artist';

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
    const [, groupSongs] = dupGroups[0]!;
    const selected = this.selectNewFirst('samename', groupSongs.map((s: Song) => ({song: s, tracker: null})), this.getDisplayCount());
    const songs = selected.map(p => p.song);
    const displayTitle = (songs[0]!.track.tt ?? songs[0]!._title ?? '').trim();

    return [
      {
        key: `samename_${displayTitle}`,
        title: `Songs Named '${displayTitle}'`,
        description: 'Different tracks sharing the same title.',
        songs: songs.map(s => this.mapUniqueSong({song: s, tracker: null})),
      },
    ];
  }

  // ── Percentile tier helpers ────────────────────────────────────────

  private static readonly PERCENTILE_THRESHOLDS = [1, 2, 3, 4, 5, 10, 15, 20, 25, 30, 40, 50, 60, 70, 80, 90, 100];

  /** Convert rawPercentile (fraction) to its bucket ceiling, e.g. 0.0144 → 2. */
  private static percentileBucket(rawPct: number): number | undefined {
    if (rawPct <= 0) return undefined;
    let topPct = rawPct * 100;
    if (topPct > 100) topPct = 100;
    if (topPct < 1) topPct = 1;
    return SuggestionGenerator.PERCENTILE_THRESHOLDS.find(t => topPct <= t) ?? 100;
  }

  /** Return the next lower threshold for a given bucket. E.g. bucket 5 → next target 4, bucket 10 → next target 5. */
  private static nextLowerThreshold(bucket: number): number | undefined {
    const idx = SuggestionGenerator.PERCENTILE_THRESHOLDS.indexOf(bucket);
    if (idx <= 0) return undefined;
    return SuggestionGenerator.PERCENTILE_THRESHOLDS[idx - 1];
  }

  // ── Almost Elite ──────────────────────────────────────────────────
  //
  // Songs in the top 5% (bucket 2..5) that could be pushed to top 1%.

  private almostElite(): SuggestionCategory[] {
    const pool: SongPair[] = [];
    for (const s of this.songs) {
      const board = this.scoresIndex[s.track.su];
      pool.push(
        ...this.eachTracker(s, board, t => {
          const b = SuggestionGenerator.percentileBucket(t.rawPercentile);
          return b != null && b >= 2 && b <= 5;
        }),
      );
    }
    this.shuffleInPlace(pool);
    const freshCount = this.getFreshCount(pool);
    if (!this.shouldEmit('almost_elite', freshCount)) return [];
    const take = this.getDisplayCount();
    const final = this.selectNewFirst('almost_elite', pool, take);
    if (final.length === 0) return [];
    return [
      {
        key: 'almost_elite',
        title: 'Almost Elite',
        description: "You're in the top 5% on these — one good run could crack the top 1%.",
        songs: final.map(p => this.mapUniqueSongWithInstrument(p)),
      },
    ];
  }

  private almostEliteDecade(): SuggestionCategory[] {
    const pool: SongPair[] = [];
    for (const s of this.songs) {
      const board = this.scoresIndex[s.track.su];
      pool.push(
        ...this.eachTracker(s, board, t => {
          const b = SuggestionGenerator.percentileBucket(t.rawPercentile);
          return b != null && b >= 2 && b <= 5;
        }),
      );
    }
    const freshCount = this.getFreshCount(pool);
    if (!this.shouldEmit('almost_elite_decade_wrap', freshCount)) return [];
    return this.buildDecadeVariant(
      'almost_elite',
      'Almost Elite',
      "You're in the top 5% on these — one good run could crack the top 1%.",
      pool,
    );
  }

  private almostEliteInstrument(instrument: InstrumentKey): SuggestionCategory[] {
    const list: SongPair[] = [];
    for (const s of this.songs) {
      const tr = getTracker(this.scoresIndex[s.track.su], instrument);
      if (tr) {
        const b = SuggestionGenerator.percentileBucket(tr.rawPercentile);
        if (b != null && b >= 2 && b <= 5) list.push({song: s, tracker: tr});
      }
    }
    this.shuffleInPlace(list);
    const key = `almost_elite_${instrument}`;
    const freshCount = this.getFreshCount(list);
    if (!this.shouldEmit(key, freshCount)) return [];
    const take = this.getDisplayCount();
    const final = this.selectNewFirst(key, list, take);
    if (final.length === 0) return [];
    return [
      {
        key,
        title: `Almost Elite on ${instrumentLabel(instrument)}`,
        description: `Your ${instrumentLabel(instrument)} scores are in the top 5% — push them into the top 1%.`,
        songs: final.map(p => this.mapUniqueSong(p)),
      },
    ];
  }

  private almostEliteInstrumentDecade(instrument: InstrumentKey): SuggestionCategory[] {
    const list: SongPair[] = [];
    for (const s of this.songs) {
      const tr = getTracker(this.scoresIndex[s.track.su], instrument);
      if (tr) {
        const b = SuggestionGenerator.percentileBucket(tr.rawPercentile);
        if (b != null && b >= 2 && b <= 5) list.push({song: s, tracker: tr});
      }
    }
    const freshCount = this.getFreshCount(list);
    const wrapKey = `almost_elite_${instrument}_decade_wrap`;
    if (!this.shouldEmit(wrapKey, freshCount)) return [];
    return this.buildDecadeVariant(
      `almost_elite_${instrument}`,
      `Almost Elite on ${instrumentLabel(instrument)}`,
      `Your ${instrumentLabel(instrument)} scores are in the top 5% — push them into the top 1%.`,
      list,
    );
  }

  // ── Percentile Push ───────────────────────────────────────────────
  //
  // Songs close to jumping up to the next percentile bracket.
  // E.g. top 10% → top 5%, top 25% → top 20%, etc.

  private static isNearNextBracket(rawPercentile: number): boolean {
    const bucket = SuggestionGenerator.percentileBucket(rawPercentile);
    if (bucket == null || bucket <= 1) return false; // Already top 1% — nothing to push to
    const next = SuggestionGenerator.nextLowerThreshold(bucket);
    if (next == null) return false;
    // "Near" means the raw percentile is in the lower half of the current bucket
    // e.g. bucket 10 (range 6-10%), raw ~7% → within reach.
    const topPct = rawPercentile * 100;
    const midpoint = next + (bucket - next) / 2;
    return topPct <= midpoint;
  }

  private percentilePush(): SuggestionCategory[] {
    const pool: SongPair[] = [];
    for (const s of this.songs) {
      const board = this.scoresIndex[s.track.su];
      pool.push(
        ...this.eachTracker(s, board, t => SuggestionGenerator.isNearNextBracket(t.rawPercentile)),
      );
    }
    this.shuffleInPlace(pool);
    const freshCount = this.getFreshCount(pool);
    if (!this.shouldEmit('pct_push', freshCount)) return [];
    const take = this.getDisplayCount();
    const final = this.selectNewFirst('pct_push', pool, take);
    if (final.length === 0) return [];
    return [
      {
        key: 'pct_push',
        title: 'Percentile Push',
        description: 'These scores are close to the next percentile bracket — replay them to climb.',
        songs: final.map(p => this.mapUniqueSongWithInstrument(p)),
      },
    ];
  }

  private percentilePushDecade(): SuggestionCategory[] {
    const pool: SongPair[] = [];
    for (const s of this.songs) {
      const board = this.scoresIndex[s.track.su];
      pool.push(
        ...this.eachTracker(s, board, t => SuggestionGenerator.isNearNextBracket(t.rawPercentile)),
      );
    }
    const freshCount = this.getFreshCount(pool);
    if (!this.shouldEmit('pct_push_decade_wrap', freshCount)) return [];
    return this.buildDecadeVariant(
      'pct_push',
      'Percentile Push',
      'These scores are close to the next percentile bracket — replay them to climb.',
      pool,
    );
  }

  private percentilePushInstrument(instrument: InstrumentKey): SuggestionCategory[] {
    const list: SongPair[] = [];
    for (const s of this.songs) {
      const tr = getTracker(this.scoresIndex[s.track.su], instrument);
      if (tr && SuggestionGenerator.isNearNextBracket(tr.rawPercentile)) {
        list.push({song: s, tracker: tr});
      }
    }
    this.shuffleInPlace(list);
    const key = `pct_push_${instrument}`;
    const freshCount = this.getFreshCount(list);
    if (!this.shouldEmit(key, freshCount)) return [];
    const take = this.getDisplayCount();
    const final = this.selectNewFirst(key, list, take);
    if (final.length === 0) return [];
    return [
      {
        key,
        title: `Percentile Push: ${instrumentLabel(instrument)}`,
        description: `Replay these ${instrumentLabel(instrument)} songs to jump to the next percentile bracket.`,
        songs: final.map(p => this.mapUniqueSong(p)),
      },
    ];
  }

  private percentilePushInstrumentDecade(instrument: InstrumentKey): SuggestionCategory[] {
    const list: SongPair[] = [];
    for (const s of this.songs) {
      const tr = getTracker(this.scoresIndex[s.track.su], instrument);
      if (tr && SuggestionGenerator.isNearNextBracket(tr.rawPercentile)) {
        list.push({song: s, tracker: tr});
      }
    }
    const freshCount = this.getFreshCount(list);
    const wrapKey = `pct_push_${instrument}_decade_wrap`;
    if (!this.shouldEmit(wrapKey, freshCount)) return [];
    return this.buildDecadeVariant(
      `pct_push_${instrument}`,
      `Percentile Push: ${instrumentLabel(instrument)}`,
      `Replay these ${instrumentLabel(instrument)} songs to jump to the next percentile bracket.`,
      list,
    );
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

    const pickedGroup = groups[0]!;
    const disp = (pickedGroup[1][0]!.song.track.tt ?? pickedGroup[1][0]!.song._title ?? '').trim();

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
        description: 'FC these same-name songs for a unique achievement!',
        songs: pool.map(p => this.mapUniqueSongWithInstrument(p)),
      },
    ];
  }

  // ── Stale / Untouched Songs ─────────────────────────────────────

  /** Get the most recent season any instrument was played for this song. */
  private latestSeason(songId: string): number {
    const board = this.scoresIndex[songId];
    if (!board) return 0;
    let best = 0;
    for (const ins of Instruments) {
      const tr = getTracker(board, ins);
      if (tr && tr.seasonAchieved > best) best = tr.seasonAchieved;
    }
    return best;
  }

  /** Get the season a specific instrument was last played. */
  private instrumentSeason(songId: string, instrument: InstrumentKey): number {
    const tr = getTracker(this.scoresIndex[songId], instrument);
    return tr?.seasonAchieved ?? 0;
  }

  private staleGlobal(minSeasonsAgo: number): SuggestionCategory[] {
    if (this.currentSeason <= 0) return [];
    const pool: SongPair[] = [];
    for (const s of this.songs) {
      const latest = this.latestSeason(s.track.su);
      if (latest <= 0) continue; // never played — different category
      const ago = this.currentSeason - latest;
      const match = minSeasonsAgo === 0 ? ago > 0
        : minSeasonsAgo >= 5 ? ago >= 5
        : ago >= minSeasonsAgo;
      if (match) {
        pool.push({song: s, tracker: null});
      }
    }
    this.shuffleInPlace(pool);
    const suffix = minSeasonsAgo >= 5 ? '5plus' : String(minSeasonsAgo);
    const key = `stale_global_${suffix}`;
    const freshCount = this.getFreshCount(pool);
    if (!this.shouldEmit(key, freshCount)) return [];
    const take = this.getDisplayCount();
    const final = this.selectNewFirst(key, pool, take);
    if (final.length === 0) return [];
    const label = minSeasonsAgo === 1 ? 'Play This Season'
      : minSeasonsAgo >= 5 ? 'Untouched for 5+ Seasons'
      : `Untouched for ${minSeasonsAgo} Seasons`;
    const desc = minSeasonsAgo === 1
      ? "Songs you haven't played on any instrument this season."
      : minSeasonsAgo >= 5
        ? "Songs you haven't played on any instrument in 5 or more seasons."
        : `Songs you haven't played on any instrument in at least ${minSeasonsAgo} seasons.`;
    return [{key, title: label, description: desc, songs: final.map(p => this.mapUniqueSong(p))}];
  }

  private staleInstrument(instrument: InstrumentKey, minSeasonsAgo: number): SuggestionCategory[] {
    if (this.currentSeason <= 0) return [];
    const pool: SongPair[] = [];
    for (const s of this.songs) {
      const season = this.instrumentSeason(s.track.su, instrument);
      if (season <= 0) continue;
      const ago = this.currentSeason - season;
      const match = minSeasonsAgo === 0 ? ago > 0
        : minSeasonsAgo >= 5 ? ago >= 5
        : ago >= minSeasonsAgo;
      if (match) {
        const tr = getTracker(this.scoresIndex[s.track.su], instrument);
        pool.push({song: s, tracker: tr ?? null, instrumentKey: instrument});
      }
    }
    this.shuffleInPlace(pool);
    const suffix = minSeasonsAgo >= 5 ? '5plus' : String(minSeasonsAgo);
    const key = `stale_${instrument}_${suffix}`;
    const freshCount = this.getFreshCount(pool);
    if (!this.shouldEmit(key, freshCount)) return [];
    const take = this.getDisplayCount();
    const final = this.selectNewFirst(key, pool, take);
    if (final.length === 0) return [];
    const instName = instrumentLabel(instrument);
    const label = minSeasonsAgo === 1 ? `Play ${instName} This Season`
      : minSeasonsAgo >= 5 ? `${instName} Untouched for 5+ Seasons`
      : `${instName} Untouched for ${minSeasonsAgo} Seasons`;
    const desc = minSeasonsAgo === 1
      ? `Songs you haven't played on ${instName} this season.`
      : minSeasonsAgo >= 5
        ? `Songs you haven't played on ${instName} in 5 or more seasons.`
        : `Songs you haven't played on ${instName} in at least ${minSeasonsAgo} seasons.`;
    return [{key, title: label, description: desc, songs: final.map(p => this.mapUniqueSongWithInstrument(p))}];
  }

  // ── Percentile Improvement Categories ──────────────────────────

  /** Songs where ALL played instruments are in the same percentile bucket (not Top 1%). */
  private samePercentileBucket(): SuggestionCategory[] {
    const pool: SongPair[] = [];
    const songBucket = new Map<string, number>();
    for (const s of this.songs) {
      const board = this.scoresIndex[s.track.su];
      if (!board) continue;
      const buckets: number[] = [];
      for (const ins of Instruments) {
        const tr = getTracker(board, ins);
        if (tr && tr.rawPercentile > 0) {
          const b = SuggestionGenerator.percentileBucket(tr.rawPercentile);
          if (b != null) buckets.push(b);
        }
      }
      if (buckets.length >= 2 && buckets.every(b => b === buckets[0]) && buckets[0]! > 1) {
        pool.push({song: s, tracker: null});
        songBucket.set(s.track.su, buckets[0]!);
      }
    }
    this.shuffleInPlace(pool);
    const key = 'same_pct_improve';
    const freshCount = this.getFreshCount(pool);
    if (!this.shouldEmit(key, freshCount)) return [];
    const take = this.getDisplayCount();
    const final = this.selectNewFirst(key, pool, take);
    if (final.length === 0) return [];
    return [{
      key,
      title: 'Competitive Improvements',
      description: 'Songs where your percentile is the same across all instruments. An improvement on any instrument moves you up everywhere.',
      songs: final.map(p => {
        const item = this.mapUniqueSong(p);
        const b = songBucket.get(p.song.track.su);
        if (b != null) item.percentileDisplay = `Top ${b}%`;
        return item;
      }),
    }];
  }

  /** Songs where ALL played instruments are in the same specific bucket (not Top 1%). */
  private samePercentileBucketSpecific(bucket: number): SuggestionCategory[] {
    const pool: SongPair[] = [];
    for (const s of this.songs) {
      const board = this.scoresIndex[s.track.su];
      if (!board) continue;
      const buckets: number[] = [];
      for (const ins of Instruments) {
        const tr = getTracker(board, ins);
        if (tr && tr.rawPercentile > 0) {
          const b = SuggestionGenerator.percentileBucket(tr.rawPercentile);
          if (b != null) buckets.push(b);
        }
      }
      if (buckets.length >= 2 && buckets.every(b => b === bucket)) {
        pool.push({song: s, tracker: null});
      }
    }
    this.shuffleInPlace(pool);
    const key = `same_pct_${bucket}`;
    const freshCount = this.getFreshCount(pool);
    if (!this.shouldEmit(key, freshCount)) return [];
    const take = this.getDisplayCount();
    const final = this.selectNewFirst(key, pool, take);
    if (final.length === 0) return [];
    const next = SuggestionGenerator.nextLowerThreshold(bucket);
    const target = next != null ? `Top ${next}%` : 'a higher bracket';
    return [{
      key,
      title: `Break Into ${target}`,
      description: `Songs where all your instruments are ranked Top ${bucket}%. Improve any instrument to break the tie and climb to ${target}.`,
      songs: final.map(p => {
        const item = this.mapUniqueSong(p);
        item.percentileDisplay = `Top ${bucket}%`;
        return item;
      }),
    }];
  }

  /** Per-bucket: songs in a specific percentile bucket across any instrument (not Top 1%). */
  private percentileImproveBucket(bucket: number): SuggestionCategory[] {
    const pool: SongPair[] = [];
    for (const s of this.songs) {
      const board = this.scoresIndex[s.track.su];
      pool.push(
        ...this.eachTracker(s, board, t => {
          const b = SuggestionGenerator.percentileBucket(t.rawPercentile);
          return b === bucket && bucket > 1;
        }),
      );
    }
    this.shuffleInPlace(pool);
    const key = `pct_improve_${bucket}`;
    const freshCount = this.getFreshCount(pool);
    if (!this.shouldEmit(key, freshCount)) return [];
    const take = this.getDisplayCount();
    const final = this.selectNewFirst(key, pool, take);
    if (final.length === 0) return [];
    return [{
      key,
      title: `Top ${bucket}% Push`,
      description: `Songs with at least one instrument ranked Top ${bucket}%. A small score bump could push you higher.`,
      songs: final.map(p => this.mapUniqueSongWithInstrument(p)),
    }];
  }

  /** Per-instrument: songs in a specific percentile bucket (not Top 1%). */
  private percentileImproveInstrument(instrument: InstrumentKey, bucket: number): SuggestionCategory[] {
    const pool: SongPair[] = [];
    for (const s of this.songs) {
      const tr = getTracker(this.scoresIndex[s.track.su], instrument);
      if (tr) {
        const b = SuggestionGenerator.percentileBucket(tr.rawPercentile);
        if (b === bucket && bucket > 1) {
          pool.push({song: s, tracker: tr, instrumentKey: instrument});
        }
      }
    }
    this.shuffleInPlace(pool);
    const key = `pct_improve_${instrument}_${bucket}`;
    const freshCount = this.getFreshCount(pool);
    if (!this.shouldEmit(key, freshCount)) return [];
    const take = this.getDisplayCount();
    const final = this.selectNewFirst(key, pool, take);
    if (final.length === 0) return [];
    return [{
      key,
      title: `Top ${bucket}% Push`,
      description: `Songs with ${instrumentLabel(instrument)} scores ranked Top ${bucket}%. A small score bump could push you higher.`,
      songs: final.map(p => this.mapUniqueSong(p)),
    }];
  }

  /** Per-instrument: varied non-Top-1% songs — only show if we can get 3+ entries with all different buckets. */
  private improveInstrumentRankings(instrument: InstrumentKey): SuggestionCategory[] {
    // Group by bucket
    const byBucket = new Map<number, SongPair[]>();
    for (const s of this.songs) {
      const tr = getTracker(this.scoresIndex[s.track.su], instrument);
      if (tr && tr.rawPercentile > 0) {
        const b = SuggestionGenerator.percentileBucket(tr.rawPercentile);
        if (b != null && b > 1) {
          const list = byBucket.get(b) ?? [];
          list.push({song: s, tracker: tr, instrumentKey: instrument});
          byBucket.set(b, list);
        }
      }
    }
    // Need 3+ distinct buckets to show variety
    if (byBucket.size < 3) return [];

    // Pick one song from each bucket, shuffle
    const candidates: SongPair[] = [];
    for (const [, pairs] of byBucket) {
      this.shuffleInPlace(pairs);
      candidates.push(pairs[0]!);
    }
    this.shuffleInPlace(candidates);

    const key = `improve_rankings_${instrument}`;
    const freshCount = this.getFreshCount(candidates);
    if (!this.shouldEmit(key, freshCount)) return [];
    const take = Math.min(this.getDisplayCount(), candidates.length);
    const final = this.selectNewFirst(key, candidates, take);
    if (final.length < 3) return [];
    return [{
      key,
      title: `Improve ${instrumentLabel(instrument)} Rankings`,
      description: `A varied mix of ${instrumentLabel(instrument)} songs across different percentile brackets — all with room to grow.`,
      songs: final.map(p => this.mapUniqueSong(p)),
    }];
  }

  // ─── Near max score strategies ───────────────────────────────

  /**
   * Songs where the player's score is within a gap range of the CHOpt max score.
   * Exclusive tiers: (0, 5000], (5000, 10000], (10000, 15000].
   */
  private nearMaxScore(minGap: number, maxGap: number, tierLabel: string): SuggestionCategory[] {
    const pool: SongPair[] = [];
    for (const s of this.songs) {
      if (!s.maxScores) continue;
      const board = this.scoresIndex[s.track.su];
      pool.push(
        ...this.eachTracker(s, board, (t, instrument) => {
          const choptMax = s.maxScores?.[instrument];
          if (choptMax == null || choptMax <= 0 || !t.initialized || t.maxScore <= 0) return false;
          const gap = choptMax - t.maxScore;
          return gap > minGap && gap <= maxGap;
        }),
      );
    }
    this.shuffleInPlace(pool);
    const key = `near_max_${tierLabel.toLowerCase()}`;
    const freshCount = this.getFreshCount(pool);
    if (!this.shouldEmit(key, freshCount)) return [];
    const take = this.getDisplayCount();
    const final = this.selectNewFirst(key, pool, take);

    const titles: Record<string, string> = {
      '5k': 'Almost Perfect (Within 5k)',
      '10k': 'Close to Max (Within 10k)',
      '15k': 'Approaching Max (Within 15k)',
    };
    const descriptions: Record<string, string> = {
      '5k': 'Scores within 5,000 of the theoretical max. You\'re almost there!',
      '10k': 'Scores within 10,000 of the theoretical max. A great run could close the gap.',
      '15k': 'Scores within 15,000 of the theoretical max. Keep pushing!',
    };

    return [{
      key,
      title: titles[tierLabel.toLowerCase()] ?? `Near Max Score (${tierLabel})`,
      description: descriptions[tierLabel.toLowerCase()] ?? `Scores within ${tierLabel} of the CHOpt theoretical max.`,
      songs: final.map(p => this.mapUniqueSongWithInstrument(p)),
    }];
  }

  private nearMaxScoreDecade(minGap: number, maxGap: number, tierLabel: string): SuggestionCategory[] {
    const pool: SongPair[] = [];
    for (const s of this.songs) {
      if (!s.maxScores) continue;
      const board = this.scoresIndex[s.track.su];
      pool.push(
        ...this.eachTracker(s, board, (t, instrument) => {
          const choptMax = s.maxScores?.[instrument];
          if (choptMax == null || choptMax <= 0 || !t.initialized || t.maxScore <= 0) return false;
          const gap = choptMax - t.maxScore;
          return gap > minGap && gap <= maxGap;
        }),
      );
    }
    const key = `near_max_${tierLabel.toLowerCase()}`;
    const freshCount = this.getFreshCount(pool);
    if (!this.shouldEmit(`${key}_decade_wrap`, freshCount)) return [];

    const titles: Record<string, string> = {
      '5k': 'Almost Perfect (Within 5k)',
      '10k': 'Close to Max (Within 10k)',
      '15k': 'Approaching Max (Within 15k)',
    };
    const descriptions: Record<string, string> = {
      '5k': 'Scores within 5,000 of the theoretical max. You\'re almost there!',
      '10k': 'Scores within 10,000 of the theoretical max. A great run could close the gap.',
      '15k': 'Scores within 15,000 of the theoretical max. Keep pushing!',
    };

    return this.buildDecadeVariant(
      key,
      titles[tierLabel.toLowerCase()] ?? `Near Max Score (${tierLabel})`,
      descriptions[tierLabel.toLowerCase()] ?? `Scores within ${tierLabel} of the CHOpt theoretical max.`,
      pool,
    );
  }

  // ─── Rival strategies ────────────────────────────────────────

  /**
   * Annotates a song item with closest rival info (for cross-pollination).
   * Returns partial fields to spread onto SuggestionSongItem.
   */
  private annotateWithRival(songId: string, instrument: InstrumentKey | undefined): Partial<SuggestionSongItem> {
    if (!this.rivalData) return {};
    const key = instrument ? `${songId}:${instrument}` : undefined;
    const match = key ? this.rivalData.closestRivalBySong.get(key) : undefined;
    if (!match) return {};
    return {
      rivalName: match.rival.displayName,
      rivalAccountId: match.rival.accountId,
      rivalRankDelta: match.rankDelta,
      rivalSource: match.rival.source,
    };
  }

  /** Map a SongPair with rival annotation. Used by rival strategies. */
  private mapRivalSong(pair: SongPair, rival: {displayName: string; accountId: string; source: 'song' | 'leaderboard'}, rankDelta: number): SuggestionSongItem {
    const base = this.mapSongWithInstrument(pair);
    this.addRecentSong(pair.song.track.su, pair.song.track.an);
    return {
      ...base,
      rivalName: rival.displayName,
      rivalAccountId: rival.accountId,
      rivalRankDelta: rankDelta,
      rivalSource: rival.source,
    };
  }

  /** Close the Gap vs {rival}: songs where rival barely leads (rankDelta < 0, sorted by |delta| asc) */
  private songRivalGap(rivalId: string): SuggestionCategory[] {
    if (!this.rivalData) return [];
    const matches = this.rivalData.byRival.get(rivalId);
    if (!matches || matches.length === 0) return [];
    const rival = matches[0]!.rival;

    // Songs where rival leads (negative delta), sorted by smallest gap
    const pool: SongPair[] = [];
    for (const m of matches) {
      if (m.rankDelta >= 0) continue;
      const song = this.findSong(m.songId);
      if (!song) continue;
      const board = this.scoresIndex[m.songId];
      const tracker = board ? getTracker(board, m.instrument) ?? null : null;
      pool.push({song, tracker, instrumentKey: m.instrument});
    }
    if (pool.length === 0) return [];
    // Sort by |rankDelta| ascending for closest gaps
    pool.sort((a, b) => {
      const ma = matches.find(m => m.songId === a.song.track.su && m.instrument === a.instrumentKey);
      const mb = matches.find(m => m.songId === b.song.track.su && m.instrument === b.instrumentKey);
      return Math.abs(ma?.rankDelta ?? 999) - Math.abs(mb?.rankDelta ?? 999);
    });

    const key = `song_rival_gap_${rivalId}`;
    const freshCount = this.getFreshCount(pool);
    if (!this.shouldEmit(key, freshCount)) return [];
    const take = this.getDisplayCount();
    const final = this.selectNewFirst(key, pool, take);
    if (final.length === 0) return [];
    return [{
      key,
      title: `Close the Gap vs ${rival.displayName}`,
      description: `Songs where ${rival.displayName} barely leads you. One good run could overtake them.`,
      songs: final.map(p => {
        const m = matches.find(x => x.songId === p.song.track.su && x.instrument === p.instrumentKey);
        return this.mapRivalSong(p, rival, m?.rankDelta ?? 0);
      }),
    }];
  }

  /** Protect Your Lead vs {rival}: songs where you barely lead (rankDelta > 0, sorted asc) */
  private songRivalProtect(rivalId: string): SuggestionCategory[] {
    if (!this.rivalData) return [];
    const matches = this.rivalData.byRival.get(rivalId);
    if (!matches || matches.length === 0) return [];
    const rival = matches[0]!.rival;

    const pool: SongPair[] = [];
    for (const m of matches) {
      if (m.rankDelta <= 0) continue;
      const song = this.findSong(m.songId);
      if (!song) continue;
      const board = this.scoresIndex[m.songId];
      const tracker = board ? getTracker(board, m.instrument) ?? null : null;
      pool.push({song, tracker, instrumentKey: m.instrument});
    }
    if (pool.length === 0) return [];
    pool.sort((a, b) => {
      const ma = matches.find(m => m.songId === a.song.track.su && m.instrument === a.instrumentKey);
      const mb = matches.find(m => m.songId === b.song.track.su && m.instrument === b.instrumentKey);
      return (ma?.rankDelta ?? 999) - (mb?.rankDelta ?? 999);
    });

    const key = `song_rival_protect_${rivalId}`;
    const freshCount = this.getFreshCount(pool);
    if (!this.shouldEmit(key, freshCount)) return [];
    const take = this.getDisplayCount();
    const final = this.selectNewFirst(key, pool, take);
    if (final.length === 0) return [];
    return [{
      key,
      title: `Protect Your Lead vs ${rival.displayName}`,
      description: `You're barely ahead of ${rival.displayName} on these. Don't let them pass you.`,
      songs: final.map(p => {
        const m = matches.find(x => x.songId === p.song.track.su && x.instrument === p.instrumentKey);
        return this.mapRivalSong(p, rival, m?.rankDelta ?? 0);
      }),
    }];
  }

  /** Battleground Songs: songs where 2+ rivals cluster near your rank */
  private songRivalBattleground(): SuggestionCategory[] {
    if (!this.rivalData) return [];
    // Count rivals within ±10 ranks per song+instrument
    const rivalCountBySong = new Map<string, number>();
    for (const [, matches] of this.rivalData.byRival) {
      for (const m of matches) {
        if (Math.abs(m.rankDelta) <= 10) {
          const k = `${m.songId}:${m.instrument}`;
          rivalCountBySong.set(k, (rivalCountBySong.get(k) ?? 0) + 1);
        }
      }
    }

    const pool: SongPair[] = [];
    for (const [k, count] of rivalCountBySong) {
      if (count < 2) continue;
      const [songId, instrument] = k.split(':') as [string, InstrumentKey];
      const song = this.findSong(songId);
      if (!song) continue;
      const board = this.scoresIndex[songId];
      const tracker = board ? getTracker(board, instrument) ?? null : null;
      pool.push({song, tracker, instrumentKey: instrument});
    }
    if (pool.length === 0) return [];
    this.shuffleInPlace(pool);

    const key = 'song_rival_battleground';
    const freshCount = this.getFreshCount(pool);
    if (!this.shouldEmit(key, freshCount)) return [];
    const take = this.getDisplayCount();
    const final = this.selectNewFirst(key, pool, take);
    if (final.length === 0) return [];
    return [{
      key,
      title: 'Battleground Songs',
      description: 'Multiple rivals are clustered around your rank on these songs. Every position matters.',
      songs: final.map(p => {
        const ann = this.annotateWithRival(p.song.track.su, p.instrumentKey);
        return {...this.mapUniqueSongWithInstrument(p), ...ann};
      }),
    }];
  }

  /** Rival Spotlight: curated mix of songs across sentiments for one rival */
  private songRivalSpotlight(rivalId: string): SuggestionCategory[] {
    if (!this.rivalData) return [];
    const matches = this.rivalData.byRival.get(rivalId);
    if (!matches || matches.length < 3) return [];
    const rival = matches[0]!.rival;

    const behind = matches.filter(m => m.rankDelta < 0).sort((a, b) => Math.abs(a.rankDelta) - Math.abs(b.rankDelta));
    const ahead = matches.filter(m => m.rankDelta > 0).sort((a, b) => a.rankDelta - b.rankDelta);
    const closest = [...matches].sort((a, b) => Math.abs(a.rankDelta) - Math.abs(b.rankDelta));

    const picks: typeof matches = [];
    // 1-2 catchup, 1-2 protect, 1 closest
    if (behind.length > 0) picks.push(behind[0]!);
    if (behind.length > 1) picks.push(behind[1]!);
    if (ahead.length > 0) picks.push(ahead[0]!);
    if (ahead.length > 1) picks.push(ahead[1]!);
    const closestNew = closest.find(m => !picks.some(p => p.songId === m.songId));
    if (closestNew) picks.push(closestNew);

    const pool: SongPair[] = [];
    for (const m of picks) {
      const song = this.findSong(m.songId);
      if (!song) continue;
      const board = this.scoresIndex[m.songId];
      const tracker = board ? getTracker(board, m.instrument) ?? null : null;
      pool.push({song, tracker, instrumentKey: m.instrument});
    }
    if (pool.length < 3) return [];

    const key = `song_rival_spotlight_${rivalId}`;
    if (this.emitted.has(key)) return [];
    return [{
      key,
      title: `Rival Spotlight: ${rival.displayName}`,
      description: `A curated mix of your rivalry with ${rival.displayName} — catches, defenses, and closest battles.`,
      songs: pool.map(p => {
        const m = matches.find(x => x.songId === p.song.track.su && x.instrument === p.instrumentKey);
        return this.mapRivalSong(p, rival, m?.rankDelta ?? 0);
      }),
    }];
  }

  /** {rival} is Pulling Ahead: large rival leads (rankDelta < -20) */
  private songRivalSlipping(rivalId: string): SuggestionCategory[] {
    if (!this.rivalData) return [];
    const matches = this.rivalData.byRival.get(rivalId);
    if (!matches) return [];
    const rival = matches[0]!.rival;

    const pool: SongPair[] = [];
    for (const m of matches) {
      if (m.rankDelta >= -20) continue;
      const song = this.findSong(m.songId);
      if (!song) continue;
      const board = this.scoresIndex[m.songId];
      const tracker = board ? getTracker(board, m.instrument) ?? null : null;
      pool.push({song, tracker, instrumentKey: m.instrument});
    }
    if (pool.length === 0) return [];
    this.shuffleInPlace(pool);

    const key = `song_rival_slipping_${rivalId}`;
    const freshCount = this.getFreshCount(pool);
    if (!this.shouldEmit(key, freshCount)) return [];
    const take = this.getDisplayCount();
    const final = this.selectNewFirst(key, pool, take);
    if (final.length === 0) return [];
    return [{
      key,
      title: `${rival.displayName} is Pulling Ahead`,
      description: `${rival.displayName} has a big lead on these songs. Time to close the gap.`,
      songs: final.map(p => {
        const m = matches.find(x => x.songId === p.song.track.su && x.instrument === p.instrumentKey);
        return this.mapRivalSong(p, rival, m?.rankDelta ?? 0);
      }),
    }];
  }

  /** Dominate {rival}: large user leads (rankDelta > 30) */
  private songRivalDominate(rivalId: string): SuggestionCategory[] {
    if (!this.rivalData) return [];
    const matches = this.rivalData.byRival.get(rivalId);
    if (!matches) return [];
    const rival = matches[0]!.rival;

    const pool: SongPair[] = [];
    for (const m of matches) {
      if (m.rankDelta <= 30) continue;
      const song = this.findSong(m.songId);
      if (!song) continue;
      const board = this.scoresIndex[m.songId];
      const tracker = board ? getTracker(board, m.instrument) ?? null : null;
      pool.push({song, tracker, instrumentKey: m.instrument});
    }
    if (pool.length === 0) return [];
    this.shuffleInPlace(pool);

    const key = `song_rival_dominate_${rivalId}`;
    const freshCount = this.getFreshCount(pool);
    if (!this.shouldEmit(key, freshCount)) return [];
    const take = this.getDisplayCount();
    const final = this.selectNewFirst(key, pool, take);
    if (final.length === 0) return [];
    return [{
      key,
      title: `Dominate ${rival.displayName}`,
      description: `You're crushing ${rival.displayName} on these. Keep up the dominance.`,
      songs: final.map(p => {
        const m = matches.find(x => x.songId === p.song.track.su && x.instrument === p.instrumentKey);
        return this.mapRivalSong(p, rival, m?.rankDelta ?? 0);
      }),
    }];
  }

  // ─── Cross-pollination rivalry-enhanced variants ─────────────

  /** Near FC songs where a rival also has a score (per-song data). */
  private songRivalNearFc(): SuggestionCategory[] {
    if (!this.rivalData) return [];
    const pool: SongPair[] = [];
    for (const s of this.songs) {
      const board = this.scoresIndex[s.track.su];
      const pairs = this.eachTracker(s, board, t => t.numStars >= 5 && t.percentHit >= 920000 && !t.isFullCombo);
      for (const p of pairs) {
        const key = `${p.song.track.su}:${p.instrumentKey}`;
        if (this.rivalData.closestRivalBySong.has(key)) pool.push(p);
      }
    }
    if (pool.length === 0) return [];
    this.shuffleInPlace(pool);

    const key = 'song_rival_near_fc';
    const freshCount = this.getFreshCount(pool);
    if (!this.shouldEmit(key, freshCount)) return [];
    const take = this.getDisplayCount();
    const final = this.selectNewFirst(key, pool, take);
    if (final.length === 0) return [];
    const firstMatch = this.rivalData.closestRivalBySong.get(`${final[0]!.song.track.su}:${final[0]!.instrumentKey}`);
    const rivalName = firstMatch?.rival.displayName ?? 'a rival';
    return [{
      key,
      title: `FC These to Beat ${rivalName}!`,
      description: 'Almost FC songs where your rival also competes. Nail the combo to pull ahead.',
      songs: final.map(p => {
        const ann = this.annotateWithRival(p.song.track.su, p.instrumentKey);
        return {...this.mapUniqueSongWithInstrument(p), ...ann};
      }),
    }];
  }

  /** Stale songs where a rival is beating you. */
  private songRivalStale(): SuggestionCategory[] {
    if (!this.rivalData || this.currentSeason === 0) return [];
    const pool: SongPair[] = [];
    for (const s of this.songs) {
      const board = this.scoresIndex[s.track.su];
      if (!board) continue;
      for (const ins of Instruments) {
        const tr = getTracker(board, ins);
        if (!tr || tr.seasonAchieved === 0) continue;
        if (this.currentSeason - tr.seasonAchieved < 2) continue; // Must be stale (2+ seasons old)
        const rivalKey = `${s.track.su}:${ins}`;
        const match = this.rivalData.closestRivalBySong.get(rivalKey);
        if (match && match.rankDelta < 0) { // Rival is ahead on this stale song
          pool.push({song: s, tracker: tr, instrumentKey: ins});
        }
      }
    }
    if (pool.length === 0) return [];
    this.shuffleInPlace(pool);

    const key = 'song_rival_stale';
    const freshCount = this.getFreshCount(pool);
    if (!this.shouldEmit(key, freshCount)) return [];
    const take = this.getDisplayCount();
    const final = this.selectNewFirst(key, pool, take);
    if (final.length === 0) return [];
    return [{
      key,
      title: 'Stale Songs Your Rivals Are Beating You On',
      description: "Songs you haven't touched in a while where rivals have pulled ahead.",
      songs: final.map(p => {
        const ann = this.annotateWithRival(p.song.track.su, p.instrumentKey);
        return {...this.mapUniqueSongWithInstrument(p), ...ann};
      }),
    }];
  }

  /** Star gain songs where improving would also pass a rival. */
  private songRivalStarGains(): SuggestionCategory[] {
    if (!this.rivalData) return [];
    const pool: SongPair[] = [];
    for (const s of this.songs) {
      const board = this.scoresIndex[s.track.su];
      const pairs = this.eachTracker(s, board, t => t.numStars >= 3 && t.numStars <= 5);
      for (const p of pairs) {
        const k = `${p.song.track.su}:${p.instrumentKey}`;
        const match = this.rivalData.closestRivalBySong.get(k);
        if (match && match.rankDelta < 0) pool.push(p);
      }
    }
    if (pool.length === 0) return [];
    this.shuffleInPlace(pool);

    const key = 'song_rival_star_gains';
    const freshCount = this.getFreshCount(pool);
    if (!this.shouldEmit(key, freshCount)) return [];
    const take = this.getDisplayCount();
    const final = this.selectNewFirst(key, pool, take);
    if (final.length === 0) return [];
    const firstMatch = this.rivalData.closestRivalBySong.get(`${final[0]!.song.track.su}:${final[0]!.instrumentKey}`);
    const rivalName = firstMatch?.rival.displayName ?? 'a rival';
    return [{
      key,
      title: `Gain Stars & Beat ${rivalName}`,
      description: 'Improving your star count on these would also overtake a rival.',
      songs: final.map(p => {
        const ann = this.annotateWithRival(p.song.track.su, p.instrumentKey);
        return {...this.mapUniqueSongWithInstrument(p), ...ann};
      }),
    }];
  }

  /** Percentile push songs where climbing would also pass a rival. */
  private songRivalPctPush(): SuggestionCategory[] {
    if (!this.rivalData) return [];
    const pool: SongPair[] = [];
    for (const s of this.songs) {
      const board = this.scoresIndex[s.track.su];
      if (!board) continue;
      for (const ins of Instruments) {
        const tr = getTracker(board, ins);
        if (!tr || tr.rawPercentile <= 0) continue;
        const bucket = SuggestionGenerator.percentileBucket(tr.rawPercentile);
        if (bucket == null || bucket <= 1) continue; // Already top 1%
        const k = `${s.track.su}:${ins}`;
        const match = this.rivalData.closestRivalBySong.get(k);
        if (match && match.rankDelta < 0) {
          pool.push({song: s, tracker: tr, instrumentKey: ins});
        }
      }
    }
    if (pool.length === 0) return [];
    this.shuffleInPlace(pool);

    const key = 'song_rival_pct_push';
    const freshCount = this.getFreshCount(pool);
    if (!this.shouldEmit(key, freshCount)) return [];
    const take = this.getDisplayCount();
    const final = this.selectNewFirst(key, pool, take);
    if (final.length === 0) return [];
    const firstMatch = this.rivalData.closestRivalBySong.get(`${final[0]!.song.track.su}:${final[0]!.instrumentKey}`);
    const rivalName = firstMatch?.rival.displayName ?? 'a rival';
    return [{
      key,
      title: `Climb Past ${rivalName}`,
      description: 'A percentile push on these would also move you past a rival.',
      songs: final.map(p => {
        const ann = this.annotateWithRival(p.song.track.su, p.instrumentKey);
        return {...this.mapUniqueSongWithInstrument(p), ...ann};
      }),
    }];
  }
}
