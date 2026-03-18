import {createLimiter} from '@festival/core';
import type {InstrumentKey} from '@festival/core';
import {InstrumentKeys} from '@festival/core';
import type {LeaderboardData, ScoreTracker, Song} from '@festival/core';
import {ScoreTracker as ScoreTrackerClass} from '@festival/core';
import type {FestivalPersistence} from '@festival/core';
import type {Settings} from '@festival/core';
import {defaultSettings} from '@festival/core';
import {parseExchangeCodeToken, parseTokenVerify} from '@festival/auth';
import {parseSongCatalog} from '../epic/contentParsing';
import {buildV1LeaderboardUrl, parseV1LeaderboardPage, updateTrackerFromV1} from '../epic/leaderboardV1';
import type {FestivalServiceEvents, FetchScoresParams, HttpClient, ImageCache, Instrumentation} from './types';

const CONTENT_BASE = 'https://fortnitecontent-website-prod07.ol.epicgames.com';
const EVENTS_BASE = 'https://events-public-service-live.ol.epicgames.com';
const ACCOUNT_BASE = 'https://account-public-service-prod.ol.epicgames.com';
// Epic launcher OAuth client (matches MAUI implementation)
const LAUNCHER_BASIC =
  'ZWM2ODRiOGM2ODdmNDc5ZmFkZWEzY2IyYWQ4M2Y1YzY6ZTFmMzFjMjExZjI4NDEzMTg2MjYyZDM3YTEzZmM4NGQ=';

const nowMs = () => Date.now();

const sleep = (ms: number, signal?: AbortSignal): Promise<void> =>
  new Promise((resolve, reject) => {
    const t = setTimeout(resolve, ms);
    if (signal) {
      const onAbort = () => {
        clearTimeout(t);
        reject(new Error('aborted'));
      };
      if (signal.aborted) return onAbort();
      signal.addEventListener('abort', onAbort, {once: true});
    }
  });

const safeCall = <T extends (...args: any[]) => any>(fn: T | undefined, ...args: Parameters<T>): void => {
  try {
    fn?.(...args);
  } catch {
    // swallow event handler failures (matches C# behavior)
  }
};

const canon = (s: string | undefined): string => (s ?? '').trim().toLowerCase();

const instrumentDefsForSong = (song: Song, settings: Settings) => {
  const intensities = song.track.in ?? {};
  const defs: Array<{key: InstrumentKey; api: string; diff: number}> = [];

  // Mirror C# ordering
  if (settings.queryDrums) defs.push({key: 'drums', api: 'Solo_Drums', diff: intensities.ds ?? 0});
  if (settings.queryLead) defs.push({key: 'guitar', api: 'Solo_Guitar', diff: intensities.gr ?? 0});
  if (settings.queryProBass) defs.push({key: 'pro_bass', api: 'Solo_PeripheralBass', diff: intensities.pb ?? 0});
  if (settings.queryProLead) defs.push({key: 'pro_guitar', api: 'Solo_PeripheralGuitar', diff: intensities.pg ?? 0});
  if (settings.queryBass) defs.push({key: 'bass', api: 'Solo_Bass', diff: intensities.ba ?? 0});
  if (settings.queryVocals) defs.push({key: 'vocals', api: 'Solo_Vocals', diff: intensities.vl ?? 0});

  return defs;
};

const ensureTracker = (ld: LeaderboardData, key: InstrumentKey): ScoreTracker => {
  const existing = (ld as any)[key] as ScoreTracker | undefined;
  if (existing) return existing;
  const created = new ScoreTrackerClass();
  (ld as any)[key] = created;
  return created;
};

const hasAnyInitializedScore = (ld: LeaderboardData): boolean =>
  ld.guitar?.initialized === true ||
  ld.drums?.initialized === true ||
  ld.bass?.initialized === true ||
  ld.vocals?.initialized === true ||
  ld.pro_guitar?.initialized === true ||
  ld.pro_bass?.initialized === true;

export class FestivalService {
  public isFetching = false;

  private readonly http: HttpClient;
  private readonly _persistence?: FestivalPersistence;
  private readonly imageCache?: ImageCache;
  private readonly events: FestivalServiceEvents;

  private songsById = new Map<string, Song>();
  private scoresBySongId: Record<string, LeaderboardData> = {};

  private songSyncComplete = false;
  private imagesSyncComplete = false;

  private songsCompletedThisPass = new Set<string>();
  private songsCurrentlyUpdating = new Set<string>();
  private prioritizedSongIds: string[] = [];

  private instImproved = 0;
  private instEmpty = 0;
  private instErrors = 0;
  private instRequests = 0;
  private instBytes = 0;
  private runStartedAtMs = 0;

  constructor(deps: {
    http: HttpClient;
    persistence?: FestivalPersistence;
    imageCache?: ImageCache;
    events?: FestivalServiceEvents;
  }) {
    this.http = deps.http;
    this._persistence = deps.persistence;
    this.imageCache = deps.imageCache;
    this.events = deps.events ?? {};
  }

  /** The underlying persistence layer. */
  get persistence(): FestivalPersistence | undefined {
    return this._persistence;
  }

  get songs(): Song[] {
    return [...this.songsById.values()];
  }

  get scoresIndex(): Readonly<Record<string, LeaderboardData>> {
    return this.scoresBySongId;
  }

  isSongCompletedThisPass(songId: string): boolean {
    return this.songsCompletedThisPass.has(songId);
  }

  isSongUpdating(songId: string): boolean {
    return this.songsCurrentlyUpdating.has(songId);
  }

  prioritizeSong(songId: string): boolean {
    if (!songId || !this.isFetching) return false;
    if (this.songsCompletedThisPass.has(songId) || this.songsCurrentlyUpdating.has(songId)) return false;
    // FIFO queue; allow duplicates but nextSong() will ignore if already removed
    this.prioritizedSongIds.push(songId);
    return true;
  }

  getInstrumentation(): Instrumentation {
    const elapsedSec = this.runStartedAtMs > 0 ? (nowMs() - this.runStartedAtMs) / 1000 : 0;
    return {
      improved: this.instImproved,
      empty: this.instEmpty,
      errors: this.instErrors,
      requests: this.instRequests,
      bytes: this.instBytes,
      elapsedSec,
    };
  }

  async initialize(opts?: {signal?: AbortSignal}): Promise<void> {
    // Load persisted state first
    if (this._persistence) {
      try {
        const loadedScores = await this._persistence.loadScores();
        for (const ld of loadedScores) {
          if (!ld?.songId) continue;
          if (!hasAnyInitializedScore(ld)) continue;
          ld.dirty = false;
          // Defensive: ensure derived strings exist
          for (const k of InstrumentKeys) {
            const tr = (ld as any)[k] as ScoreTracker | undefined;
            tr?.refreshDerived?.();
          }
          this.scoresBySongId[ld.songId] = ld;
          safeCall(this.events.scoreUpdated, ld);
        }
      } catch {
        // swallow
      }

      try {
        const loadedSongs = await this._persistence.loadSongs();
        for (const s of loadedSongs ?? []) {
          if (s?.track?.su) this.songsById.set(s.track.su, s);
        }
      } catch {
        // swallow
      }
    }

    await this.syncSongs({signal: opts?.signal});
    await this.syncImages({signal: opts?.signal});
  }

  async syncSongs(opts?: {signal?: AbortSignal}): Promise<void> {
    try {
      const res = await this.http.getText(`${CONTENT_BASE}/content/api/pages/fortnite-game/spark-tracks`, {
        signal: opts?.signal,
      });
      this.instRequests++;
      this.instBytes += res.text?.length ?? 0;
      if (!res.ok) {
        safeCall(this.events.log, `SongSync failed: HTTP ${res.status}`);
        return;
      }

      const list = parseSongCatalog(res.text);
      const incomingIds = new Set(list.map(s => s.track.su));

      // remove stale
      for (const id of [...this.songsById.keys()]) {
        if (!incomingIds.has(id)) this.songsById.delete(id);
      }

      for (const s of list) {
        const id = s.track.su;
        const existing = this.songsById.get(id);
        if (existing) {
          existing.track = s.track;
          existing._activeDate = s._activeDate;
          existing.lastModified = s.lastModified;
          existing._title = s._title ?? existing._title;
        } else {
          this.songsById.set(id, s);
        }
      }

      if (this._persistence) {
        try {
          await this._persistence.saveSongs(this.songs);
        } catch {
          // swallow
        }
      }
    } catch (e: any) {
      if (canon(String(e?.message)).includes('aborted')) throw e;
      safeCall(this.events.log, `Song sync failed: ${String(e?.message ?? e)}`);
    } finally {
      this.songSyncComplete = true;
    }
  }

  async deleteAllScores(): Promise<void> {
    console.log('[FestivalService] Deleting all scores');

    this.scoresBySongId = {};

    if (this._persistence) {
      try {
        await this._persistence.saveScores([]);
      } catch {
        // swallow
      }
    }
  }

  /**
   * Clear all scores and score history from memory and persistence.
   * Keeps songs and cached images intact.
   */
  async clearScoresAndHistory(): Promise<void> {
    console.log('[FestivalService] Clearing scores and score history');

    this.scoresBySongId = {};

    if (this._persistence) {
      try {
        await this._persistence.clearScoresAndHistory();
      } catch {
        // swallow
      }
    }
  }

  async clearImageCache(): Promise<void> {
    console.log('[FestivalService] Clearing image cache');

    // Delete on-disk cached images
    if (this.imageCache) {
      try {
        await this.imageCache.clearAll();
      } catch {
        // swallow
      }
    }

    // Clear imagePath from all songs
    for (const s of this.songs) {
      s.imagePath = undefined;
    }
    // Reset sync flag to allow re-sync
    this.imagesSyncComplete = false;
    
    // Persist the cleared state
    if (this._persistence) {
      try {
        await this._persistence.saveSongs(this.songs);
      } catch {
        // swallow
      }
    }
  }

  async syncImages(opts?: {signal?: AbortSignal}): Promise<void> {
    if (this.imagesSyncComplete) return;
    if (!this.songSyncComplete) return;
    if (!this.imageCache) {
      this.imagesSyncComplete = true;
      return;
    }

    const allSongs = this.songs;
    
    // Run all songs through ensureCached — it does a fast filesystem exists
    // check and only downloads when the file is actually missing.  We can't
    // trust a persisted imagePath because the on-disk cache may have been
    // purged (e.g. simulator restart, OS storage pressure).
    console.log(`[FestivalService] Syncing images for ${allSongs.length} songs (16 workers)`);
    const total = allSongs.length;
    let completed = 0;
    const queue = [...allSongs];
    const limiter = createLimiter(16);

    const runOne = async (s: Song): Promise<void> => {
      const title = s.track.tt ?? s._title ?? '';
      const current = ++completed;
      safeCall(this.events.songProgress, current, total, `Img ${title}`, true);
      try {
        const local = await this.imageCache?.ensureCached(s, {signal: opts?.signal});
        if (local) s.imagePath = local;
      } catch (e: any) {
        if (canon(String(e?.message)).includes('aborted')) throw e;
        safeCall(this.events.log, `Image download failed for ${title}: ${String(e?.message ?? e)}`);
      } finally {
        safeCall(this.events.songProgress, current, total, `Img ${title}`, false);
      }
    };

    await Promise.all(queue.map(s => limiter.schedule(() => runOne(s))));

    if (this._persistence) {
      try {
        await this._persistence.saveSongs(this.songs);
      } catch {
        // swallow
      }
    }

    this.imagesSyncComplete = true;
  }

  async fetchScores(params: FetchScoresParams): Promise<boolean> {
    if (this.isFetching) return false;
    if (!this.songSyncComplete || !this.imagesSyncComplete) return false;

    const signal = params.signal;
    const settings = {...defaultSettings(), ...(params.settings ?? {})};

    this.isFetching = true;
    this.songsCompletedThisPass = new Set();
    this.songsCurrentlyUpdating = new Set();
    this.prioritizedSongIds = [];

    this.instImproved = 0;
    this.instEmpty = 0;
    this.instErrors = 0;
    this.instRequests = 0;
    this.instBytes = 0;
    this.runStartedAtMs = nowMs();

    try {
      // 1) code -> token
      // MAUI generates an OAuth authorization code (responseType=code). We support that flow first,
      // and fall back to the legacy exchange_code grant for compatibility.
      const authHeaders = {Authorization: `basic ${LAUNCHER_BASIC}`};

      let token = null as ReturnType<typeof parseExchangeCodeToken>;

      const tokenResAuthCode = await this.http.postForm(
        `${ACCOUNT_BASE}/account/api/oauth/token`,
        {grant_type: 'authorization_code', code: params.exchangeCode},
        {signal, headers: authHeaders},
      );
      this.instRequests++;
      this.instBytes += tokenResAuthCode.text?.length ?? 0;
      token = parseExchangeCodeToken(tokenResAuthCode.text);

      if (!token) {
        const tokenResExchangeCode = await this.http.postForm(
          `${ACCOUNT_BASE}/account/api/oauth/token`,
          {grant_type: 'exchange_code', exchange_code: params.exchangeCode, token_type: 'eg1'},
          {signal, headers: authHeaders},
        );
        this.instRequests++;
        this.instBytes += tokenResExchangeCode.text?.length ?? 0;
        token = parseExchangeCodeToken(tokenResExchangeCode.text);
      }

      if (!token) {
        safeCall(
          this.events.log,
          'Auth failed (no token). Code may be invalid / already used. Generate a fresh code and retry.',
        );
        return false;
      }

      // 2) verify token
      const verifyRes = await this.http.getText(`${ACCOUNT_BASE}/account/api/oauth/verify`, {
        headers: {Authorization: `bearer ${token.access_token}`},
        signal,
      });
      this.instRequests++;
      this.instBytes += verifyRes.text?.length ?? 0;
      const verify = parseTokenVerify(verifyRes.text);
      if (!verifyRes.ok || canon(verify.accountId) !== canon(token.account_id)) {
        safeCall(this.events.log, 'Token verification failed. Generate a fresh exchange code and retry.');
        return false;
      }

      const ordered = this.buildPrioritizedSongList(params.filteredSongIds);
      const total = ordered.length;
      if (total === 0) return true;

      const limiter = createLimiter(16);

      let completed = 0;
      const pending = new Map<string, Song>(ordered.map(s => [s.track.su, s]));
      const pendingOrder = ordered.map(s => s.track.su);

      const nextSong = (): Song | undefined => {
        // drain prioritized queue first
        while (this.prioritizedSongIds.length > 0) {
          const pri = this.prioritizedSongIds.shift();
          if (!pri) continue;
          const song = pending.get(pri);
          if (!song) continue;
          pending.delete(pri);
          return song;
        }
        while (pendingOrder.length > 0) {
          const id = pendingOrder.shift();
          if (!id) continue;
          const song = pending.get(id);
          if (!song) continue;
          pending.delete(id);
          return song;
        }
        return undefined;
      };

      const runSong = async (song: Song): Promise<void> => {
        const id = song.track.su;
        this.songsCurrentlyUpdating.add(id);
        safeCall(this.events.songUpdateStarted, id);
        safeCall(this.events.songProgress, completed + 1, total, song.track.tt ?? song._title ?? id, true);

        try {
          await this.fetchSong(song, token.account_id, token.access_token, settings, {signal});
        } finally {
          this.songsCurrentlyUpdating.delete(id);
          this.songsCompletedThisPass.add(id);
          safeCall(this.events.songUpdateCompleted, id);
          completed++;
          safeCall(this.events.songProgress, completed, total, song.track.tt ?? song._title ?? id, false);
        }
      };

      const workers: Promise<void>[] = [];
      for (let w = 0; w < 16; w++) {
        workers.push(
          (async () => {
            while (true) {
              if (signal?.aborted) throw new Error('aborted');
              const song = nextSong();
              if (!song) break;
              await limiter.schedule(() => runSong(song));
              // yield to allow prioritizeSong() to take effect between items
              await sleep(0, signal);
            }
          })(),
        );
      }

      await Promise.all(workers);

      // Persist once at end (portable); platform adapters can do incremental upsert.
      if (this._persistence) {
        try {
          await this._persistence.saveScores(Object.values(this.scoresBySongId));
        } catch {
          // swallow
        }
      }

      return true;
    } catch (e: any) {
      if (canon(String(e?.message)).includes('aborted')) return false;
      safeCall(this.events.log, `FetchScores failed: ${String(e?.message ?? e)}`);
      return false;
    } finally {
      this.isFetching = false;
    }
  }

  private buildPrioritizedSongList(filteredSongIds?: string[]): Song[] {
    const list = this.songs;
    const indexed = list.map((s, i) => ({s, i}));

    const ordered = indexed
      .sort((a, b) => {
        const aHas = this.scoresBySongId[a.s.track.su] ? 1 : 0;
        const bHas = this.scoresBySongId[b.s.track.su] ? 1 : 0;
        if (aHas !== bHas) return aHas - bHas;
        return a.i - b.i;
      })
      .map(x => x.s);

    if (filteredSongIds && filteredSongIds.length > 0) {
      const allow = new Set(filteredSongIds);
      return ordered.filter(s => allow.has(s.track.su));
    }

    return ordered;
  }

  private async fetchSong(
    song: Song,
    accountId: string,
    accessToken: string,
    settings: Settings,
    opts: {signal?: AbortSignal},
  ): Promise<void> {
    const defs = instrumentDefsForSong(song, settings);
    if (defs.length === 0) return;

    let board = this.scoresBySongId[song.track.su];
    if (!board) {
      board = {songId: song.track.su, title: song.track.tt, artist: song.track.an};
      this.scoresBySongId[song.track.su] = board;
    }

    board.correlatedV1Pages = board.correlatedV1Pages ?? {};

    await Promise.all(
      defs.map(async def => {
        await this.fetchInstrument(board, song, def, accountId, accessToken, opts);
      }),
    );

    safeCall(this.events.scoreUpdated, board);
  }

  private async fetchInstrument(
    board: LeaderboardData,
    song: Song,
    def: {key: InstrumentKey; api: string; diff: number},
    accountId: string,
    accessToken: string,
    opts: {signal?: AbortSignal},
  ): Promise<void> {
    this.instRequests++;

    const tracker = ensureTracker(board, def.key);
    const prevScore = tracker.maxScore;

    const urlPath = buildV1LeaderboardUrl({songId: song.track.su, api: def.api, accountId, page: 0});
    const res = await this.http.getText(`${EVENTS_BASE}${urlPath}`, {
      headers: {Authorization: `bearer ${accessToken}`},
      signal: opts.signal,
    });

    this.instBytes += res.text?.length ?? 0;

    if (!res.ok || !res.text) {
      this.instEmpty++;
      return;
    }

    const page = parseV1LeaderboardPage(res.text);
    if (!page) {
      this.instErrors++;
      return;
    }

    (board.correlatedV1Pages as any)[def.api] = page;

    updateTrackerFromV1({page, accountId, difficulty: def.diff, existing: tracker});

    if (tracker.maxScore > prevScore) {
      this.instImproved++;
      board.dirty = true;
    }
  }
}
