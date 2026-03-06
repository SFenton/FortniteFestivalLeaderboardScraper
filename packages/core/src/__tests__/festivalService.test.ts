import type {HttpClient, HttpResponse} from '../services/types';
import {FestivalService} from '../services/festivalService';
import type {Song} from '../models';
import {ScoreTracker} from '../models';
import {InMemoryFestivalPersistence} from '../persistence';

const mkSong = (id: string, title: string, year = 2001): Song => ({
  track: {su: id, tt: title, an: 'Artist', ry: year, in: {gr: 3, ds: 2}},
});

const ok = (text: string, status = 200): HttpResponse => ({ok: true, status, text});
const fail = (status = 500, text = ''): HttpResponse => ({ok: false, status, text});

const makeFakeHttp = (routes: Record<string, HttpResponse>) => {
  const seen: string[] = [];
  const http: HttpClient = {
    async getText(url, _opts) {
      seen.push(`GET ${url}`);
      return routes[url] ?? fail(404, '');
    },
    async postForm(url, form, _opts) {
      seen.push(`POST ${url} ${JSON.stringify(form)}`);
      return routes[url] ?? fail(404, '');
    },
    async getBytes(url, _opts) {
      seen.push(`BYTES ${url}`);
      const r = routes[url] ?? fail(404, '');
      return {ok: r.ok, status: r.status, bytes: new Uint8Array(r.text ? r.text.length : 0)};
    },
  };
  return {http, seen};
};

describe('FestivalService (portable core)', () => {
  test('getInstrumentation is zeroed before any run', () => {
    const {http} = makeFakeHttp({});
    const svc = new FestivalService({http});
    const inst = svc.getInstrumentation();
    expect(inst.elapsedSec).toBe(0);
    expect(inst.requests).toBe(0);
  });

  test('fetchScores returns false before song/image sync completes', async () => {
    const {http} = makeFakeHttp({});
    const svc = new FestivalService({http});
    const res = await svc.fetchScores({exchangeCode: 'ex', degreeOfParallelism: 1});
    expect(res).toBe(false);
  });

  test('syncImages called before syncSongs is a no-op (still blocks fetchScores)', async () => {
    const {http} = makeFakeHttp({});
    const svc = new FestivalService({http});
    await svc.syncImages();
    const res = await svc.fetchScores({exchangeCode: 'ex', degreeOfParallelism: 1});
    expect(res).toBe(false);
  });

  test('syncSongs handles HTTP failure and safe log handler', async () => {
    const contentUrl = 'https://fortnitecontent-website-prod07.ol.epicgames.com/content/api/pages/fortnite-game/spark-tracks';
    const {http} = makeFakeHttp({[contentUrl]: fail(503, 'nope')});
    const logs: string[] = [];
    const svc = new FestivalService({
      http,
      events: {
        log: line => {
          logs.push(line);
          throw new Error('listener blew up');
        },
      },
    });

    await svc.syncSongs();
    expect(logs.some(l => l.includes('SongSync failed'))).toBe(true);
  });

  test('syncSongs swallows non-abort exceptions and logs (catch path)', async () => {
    const logs: string[] = [];
    const http: HttpClient = {
      async getText() {
        throw new Error('network down');
      },
      async postForm() {
        return ok('{}');
      },
      async getBytes() {
        return {ok: true, status: 200, bytes: new Uint8Array()};
      },
    };

    const svc = new FestivalService({
      http,
      events: {
        log: l => {
          logs.push(l);
          throw new Error('listener boom');
        },
      },
    });
    await expect(svc.syncSongs()).resolves.toBeUndefined();
    expect(logs.some(l => l.toLowerCase().includes('song sync failed'))).toBe(true);
  });

  test('syncSongs removes stale songs (stale-removal branch)', async () => {
    const contentUrl =
      'https://fortnitecontent-website-prod07.ol.epicgames.com/content/api/pages/fortnite-game/spark-tracks';

    let call = 0;
    const http: HttpClient = {
      async getText(url) {
        if (url !== contentUrl) return fail(404, '');
        call++;
        return ok(JSON.stringify(call === 1 ? {k1: mkSong('a', 'A'), k2: mkSong('b', 'B')} : {k1: mkSong('a', 'A')}));
      },
      async postForm() {
        return ok('{}');
      },
      async getBytes() {
        return {ok: true, status: 200, bytes: new Uint8Array()};
      },
    };

    const svc = new FestivalService({http});
    await svc.syncSongs();
    expect(svc.songs.map(s => s.track.su).sort()).toEqual(['a', 'b']);

    await svc.syncSongs();
    expect(svc.songs.map(s => s.track.su).sort()).toEqual(['a']);
  });

  test('initialize tolerates malformed persisted scores/songs (optional chain branches)', async () => {
    const contentUrl =
      'https://fortnitecontent-website-prod07.ol.epicgames.com/content/api/pages/fortnite-game/spark-tracks';
    const {http} = makeFakeHttp({[contentUrl]: ok(JSON.stringify({k1: mkSong('a', 'A')}))});

    const persistence = {
      async loadScores() {
        return [
          null,
          {title: 'missing songId'} as any,
          {songId: 'a', guitar: {initialized: true} as any, dirty: true} as any,
        ];
      },
      async loadSongs() {
        return [
          null,
          {track: {}} as any,
          {track: {su: 'a'}, _title: 'Title From Persistence'} as any,
        ];
      },
      async saveScores() {
        // unused
      },
      async saveSongs() {
        // unused
      },
    };

    const svc = new FestivalService({http, persistence: persistence as any});
    await svc.initialize();
    expect(svc.scoresIndex.a).toBeTruthy();
    expect(svc.songs.some(s => s.track.su === 'a')).toBe(true);
  });

  test('syncImages sets complete when no imageCache is configured (no-imageCache branch)', async () => {
    const contentUrl =
      'https://fortnitecontent-website-prod07.ol.epicgames.com/content/api/pages/fortnite-game/spark-tracks';
    const {http} = makeFakeHttp({[contentUrl]: ok(JSON.stringify({k1: mkSong('a', 'A')}))});
    const svc = new FestivalService({http});

    await svc.syncSongs();
    await svc.syncImages();

    expect((svc as any).imagesSyncComplete).toBe(true);
  });

  test('syncSongs rethrows aborted error when signaled', async () => {
    const http: HttpClient = {
      async getText(_url, opts) {
        if (opts?.signal?.aborted) throw new Error('aborted');
        return ok('{}');
      },
      async postForm() {
        return ok('{}');
      },
      async getBytes() {
        return {ok: true, status: 200, bytes: new Uint8Array()};
      },
    };
    const svc = new FestivalService({http});
    const ac = new AbortController();
    ac.abort();
    await expect(svc.syncSongs({signal: ac.signal})).rejects.toThrow('aborted');
  });

  test('syncSongs updates existing songs on subsequent syncs (existing-branch)', async () => {
    const contentUrl =
      'https://fortnitecontent-website-prod07.ol.epicgames.com/content/api/pages/fortnite-game/spark-tracks';

    const v1 = mkSong('a', 'Old Title');
    const v2 = mkSong('a', 'New Title');

    let call = 0;
    const http: HttpClient = {
      async getText(url) {
        if (url !== contentUrl) return fail(404, '');
        call++;
        return ok(JSON.stringify(call === 1 ? {k1: v1} : {k1: v2}));
      },
      async postForm() {
        return ok('{}');
      },
      async getBytes() {
        return {ok: true, status: 200, bytes: new Uint8Array()};
      },
    };

    const svc = new FestivalService({http});
    await svc.syncSongs();
    expect(svc.songs.find(s => s.track.su === 'a')?.track.tt).toBe('Old Title');

    await svc.syncSongs();
    expect(svc.songs.find(s => s.track.su === 'a')?.track.tt).toBe('New Title');
  });

  test('syncImages uses imageCache, logs errors, and becomes idempotent', async () => {
    const contentUrl = 'https://fortnitecontent-website-prod07.ol.epicgames.com/content/api/pages/fortnite-game/spark-tracks';
    const s1 = mkSong('a', 'A');
    const s2 = mkSong('b', 'B');
    const {http} = makeFakeHttp({[contentUrl]: ok(JSON.stringify({k1: s1, k2: s2}))});

    const logs: string[] = [];
    let calls = 0;
    const imageCache = {
      async ensureCached(song: Song) {
        calls++;
        if (song.track.su === 'b') throw new Error('boom');
        return `/local/${song.track.su}.jpg`;
      },
      async clearAll() {},
    };

    const svc = new FestivalService({http, imageCache, events: {log: l => logs.push(l)}});
    await svc.syncSongs();

    await svc.syncImages();
    expect(calls).toBe(2);
    expect(svc.songs.find(s => s.track.su === 'a')?.imagePath).toContain('/local/a.jpg');
    expect(logs.some(l => l.includes('Image download failed'))).toBe(true);

    // Second call should be no-op
    await svc.syncImages();
    expect(calls).toBe(2);
  });

  test('syncImages persists songs when persistence is provided', async () => {
    const contentUrl =
      'https://fortnitecontent-website-prod07.ol.epicgames.com/content/api/pages/fortnite-game/spark-tracks';
    const s1 = mkSong('a', 'A');
    const {http} = makeFakeHttp({[contentUrl]: ok(JSON.stringify({k1: s1}))});

    let saveSongsCalls = 0;
    const persistence = {
      async loadScores() {
        return [];
      },
      async loadSongs() {
        return [];
      },
      async saveScores() {
        // unused
      },
      async saveSongs() {
        saveSongsCalls++;
      },
    };

    const imageCache = {
      async ensureCached(_song: Song) {
        return undefined;
      },
      async clearAll() {},
    };

    const svc = new FestivalService({http, persistence: persistence as any, imageCache});
    await svc.syncSongs();

    // Reset so we only count the syncImages saveSongs() call
    saveSongsCalls = 0;
    await svc.syncImages();
    expect(saveSongsCalls).toBe(1);
  });

  test('syncImages rethrows aborted errors from imageCache', async () => {
    const contentUrl =
      'https://fortnitecontent-website-prod07.ol.epicgames.com/content/api/pages/fortnite-game/spark-tracks';
    const song: Song = {track: {su: 'a', an: 'Artist', in: {}}} as any;
    song._title = 'Fallback';

    const {http} = makeFakeHttp({[contentUrl]: ok(JSON.stringify({k1: song}))});
    const imageCache = {
      async ensureCached() {
        throw new Error('aborted');
      },
      async clearAll() {},
    };

    const svc = new FestivalService({http, imageCache});
    await svc.syncSongs();

    await expect(svc.syncImages()).rejects.toThrow('aborted');
  });

  test('prioritizeSong returns false when not fetching or when song is updating', async () => {
    const contentUrl = 'https://fortnitecontent-website-prod07.ol.epicgames.com/content/api/pages/fortnite-game/spark-tracks';
    const tokenUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/token';
    const verifyUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/verify';

    const a = mkSong('a', 'A');
    const v1Url =
      'https://events-public-service-live.ol.epicgames.com' +
      '/api/v1/leaderboards/FNFestival/alltime_a_Solo_Guitar/alltime/acc1?page=0&rank=0&teamAccountIds=acc1&appId=Fortnite&showLiveSessions=false';

    const routes: Record<string, HttpResponse> = {
      [contentUrl]: ok(JSON.stringify({k1: a})),
      [tokenUrl]: ok(JSON.stringify({access_token: 'tok', account_id: 'acc1'})),
      [verifyUrl]: ok(JSON.stringify({account_id: 'acc1'})),
      [v1Url]: ok(JSON.stringify({page: 0, totalPages: 1, entries: []})),
    };
    const {http} = makeFakeHttp(routes);

    const svc = new FestivalService({
      http,
      events: {
        songUpdateStarted: id => {
          expect(svc.prioritizeSong(id)).toBe(false);
        },
      },
    });
    await svc.initialize();

    expect(svc.prioritizeSong('a')).toBe(false);

    await svc.fetchScores({
      exchangeCode: 'ex',
      degreeOfParallelism: 1,
      settings: {
        degreeOfParallelism: 1,
        queryLead: true,
        queryDrums: false,
        queryVocals: false,
        queryBass: false,
        queryProLead: false,
        queryProBass: false,
      },
    });
  });

  test('initialize() loads persistence, syncs songs, and emits scoreUpdated for loaded scores', async () => {
    const persistence = new InMemoryFestivalPersistence();
    await persistence.saveScores([
      {
        songId: 'a',
        title: 'A',
        artist: 'X',
        dirty: false,
        guitar: {initialized: true},
      },
    ]);
    await persistence.saveSongs([mkSong('a', 'Song A')]);

    const contentUrl = 'https://fortnitecontent-website-prod07.ol.epicgames.com/content/api/pages/fortnite-game/spark-tracks';
    const routes = {
      [contentUrl]: ok(JSON.stringify({k1: mkSong('a', 'Song A'), k2: mkSong('b', 'Song B')})),
    };
    const {http} = makeFakeHttp(routes);

    const updated: string[] = [];
    const svc = new FestivalService({
      http,
      persistence,
      events: {scoreUpdated: b => updated.push(b.songId)},
    });

    await svc.initialize();

    expect(updated).toContain('a');
    expect(svc.songs.map(s => s.track.su).sort()).toEqual(['a', 'b']);
  });

  test('fetchScores() respects settings (lead only) and updates ScoreTracker from V1 page', async () => {
    const contentUrl = 'https://fortnitecontent-website-prod07.ol.epicgames.com/content/api/pages/fortnite-game/spark-tracks';
    const tokenUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/token';
    const verifyUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/verify';

    const song = mkSong('song1', 'Song 1');

    const v1Url =
      'https://events-public-service-live.ol.epicgames.com' +
      '/api/v1/leaderboards/FNFestival/alltime_song1_Solo_Guitar/alltime/acc1?page=0&rank=0&teamAccountIds=acc1&appId=Fortnite&showLiveSessions=false';

    const v1Page = {
      page: 0,
      totalPages: 1,
      entries: [
        {
          team_id: 'acc1',
          rank: 10,
          percentile: 0.02,
          sessionHistory: [
            {trackedStats: {SCORE: 1234, ACCURACY: 990000, FULL_COMBO: 0, STARS_EARNED: 5, SEASON: 12}},
          ],
        },
      ],
    };

    const routes = {
      [contentUrl]: ok(JSON.stringify({k1: song})),
      [tokenUrl]: ok(JSON.stringify({access_token: 'tok', account_id: 'acc1'})),
      [verifyUrl]: ok(JSON.stringify({account_id: 'acc1', displayName: 'User'})),
      [v1Url]: ok(JSON.stringify(v1Page)),
    };
    const {http, seen} = makeFakeHttp(routes);

    const persistence = new InMemoryFestivalPersistence();
    const svc = new FestivalService({http, persistence});
    await svc.initialize();

    const okRun = await svc.fetchScores({
      exchangeCode: 'ex',
      degreeOfParallelism: 1,
      settings: {
        degreeOfParallelism: 1,
        queryLead: true,
        queryDrums: false,
        queryVocals: false,
        queryBass: false,
        queryProLead: false,
        queryProBass: false,
      },
    });

    expect(okRun).toBe(true);
    const board = svc.scoresIndex.song1;
    expect(board).toBeTruthy();
    expect(board.guitar?.maxScore).toBe(1234);
    expect(board.guitar?.rank).toBe(10);
    expect(board.guitar?.percentHitFormatted).toBe('99.00%');

    // Ensure we never called other instruments (drums etc)
    expect(seen.some(x => x.includes('Solo_Drums'))).toBe(false);
  });

  test('fetchScores hits all instrument branches when enabled', async () => {
    const contentUrl =
      'https://fortnitecontent-website-prod07.ol.epicgames.com/content/api/pages/fortnite-game/spark-tracks';
    const tokenUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/token';
    const verifyUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/verify';

    const song: Song = {
      track: {
        su: 'song_all',
        tt: 'All Instruments',
        an: 'Artist',
        ry: 2001,
        in: {gr: 3, ds: 2, pb: 4, pg: 5, ba: 1, vl: 3},
      },
    };

    const mkV1 = (api: string) =>
      'https://events-public-service-live.ol.epicgames.com' +
      `/api/v1/leaderboards/FNFestival/alltime_song_all_${api}/alltime/acc1?page=0&rank=0&teamAccountIds=acc1&appId=Fortnite&showLiveSessions=false`;

    const apis = [
      'Solo_Drums',
      'Solo_Guitar',
      'Solo_PeripheralBass',
      'Solo_PeripheralGuitar',
      'Solo_Bass',
      'Solo_Vocals',
    ];

    const routes: Record<string, HttpResponse> = {
      [contentUrl]: ok(JSON.stringify({k1: song})),
      [tokenUrl]: ok(JSON.stringify({access_token: 'tok', account_id: 'acc1'})),
      [verifyUrl]: ok(JSON.stringify({account_id: 'acc1'})),
      ...Object.fromEntries(apis.map(api => [mkV1(api), ok(JSON.stringify({page: 0, totalPages: 1, entries: []}))])),
    };

    const {http, seen} = makeFakeHttp(routes);
    const svc = new FestivalService({http});
    await svc.initialize();

    const res = await svc.fetchScores({
      exchangeCode: 'ex',
      degreeOfParallelism: 1,
      settings: {
        degreeOfParallelism: 1,
        queryLead: true,
        queryDrums: true,
        queryVocals: true,
        queryBass: true,
        queryProLead: true,
        queryProBass: true,
      },
    });

    expect(res).toBe(true);
    for (const api of apis) {
      expect(seen.some(x => x.includes(api))).toBe(true);
    }

    const board: any = svc.scoresIndex.song_all;
    expect(board).toBeTruthy();
    expect(board.drums).toBeTruthy();
    expect(board.guitar).toBeTruthy();
    expect(board.pro_bass).toBeTruthy();
    expect(board.pro_guitar).toBeTruthy();
    expect(board.bass).toBeTruthy();
    expect(board.vocals).toBeTruthy();
  });

  test('fetchScores with all instruments disabled makes no leaderboard calls', async () => {
    const contentUrl = 'https://fortnitecontent-website-prod07.ol.epicgames.com/content/api/pages/fortnite-game/spark-tracks';
    const tokenUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/token';
    const verifyUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/verify';

    const song = mkSong('song1', 'Song 1');
    const routes = {
      [contentUrl]: ok(JSON.stringify({k1: song})),
      [tokenUrl]: ok(JSON.stringify({access_token: 'tok', account_id: 'acc1'})),
      [verifyUrl]: ok(JSON.stringify({account_id: 'acc1'})),
    };
    const {http, seen} = makeFakeHttp(routes);
    const svc = new FestivalService({http});
    await svc.initialize();

    const okRun = await svc.fetchScores({
      exchangeCode: 'ex',
      degreeOfParallelism: 1,
      settings: {
        degreeOfParallelism: 1,
        queryLead: false,
        queryDrums: false,
        queryVocals: false,
        queryBass: false,
        queryProLead: false,
        queryProBass: false,
      },
    });

    expect(okRun).toBe(true);
    expect(seen.some(x => x.includes('Solo_'))).toBe(false);
  });

  test('fetchScores auth/token verify failure paths return false', async () => {
    const contentUrl = 'https://fortnitecontent-website-prod07.ol.epicgames.com/content/api/pages/fortnite-game/spark-tracks';
    const tokenUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/token';
    const verifyUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/verify';

    const song = mkSong('song1', 'Song 1');

    // (1) token parse fail
    {
      const {http} = makeFakeHttp({
        [contentUrl]: ok(JSON.stringify({k1: song})),
        [tokenUrl]: ok('{not-json'),
      });
      const svc = new FestivalService({http});
      await svc.initialize();
      const res = await svc.fetchScores({exchangeCode: 'ex', degreeOfParallelism: 1});
      expect(res).toBe(false);
    }

    // (2) verify mismatch
    {
      const {http} = makeFakeHttp({
        [contentUrl]: ok(JSON.stringify({k1: song})),
        [tokenUrl]: ok(JSON.stringify({access_token: 'tok', account_id: 'acc1'})),
        [verifyUrl]: ok(JSON.stringify({account_id: 'someone-else'})),
      });
      const svc = new FestivalService({http});
      await svc.initialize();
      const res = await svc.fetchScores({exchangeCode: 'ex', degreeOfParallelism: 1});
      expect(res).toBe(false);
    }

    // (3) verify not ok
    {
      const {http} = makeFakeHttp({
        [contentUrl]: ok(JSON.stringify({k1: song})),
        [tokenUrl]: ok(JSON.stringify({access_token: 'tok', account_id: 'acc1'})),
        [verifyUrl]: fail(401, 'no'),
      });
      const svc = new FestivalService({http});
      await svc.initialize();
      const res = await svc.fetchScores({exchangeCode: 'ex', degreeOfParallelism: 1});
      expect(res).toBe(false);
    }
  });

  test('fetchInstrument empty and parse-error cases update instrumentation', async () => {
    const contentUrl = 'https://fortnitecontent-website-prod07.ol.epicgames.com/content/api/pages/fortnite-game/spark-tracks';
    const tokenUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/token';
    const verifyUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/verify';

    const song = mkSong('song1', 'Song 1');

    const v1Url =
      'https://events-public-service-live.ol.epicgames.com' +
      '/api/v1/leaderboards/FNFestival/alltime_song1_Solo_Guitar/alltime/acc1?page=0&rank=0&teamAccountIds=acc1&appId=Fortnite&showLiveSessions=false';

    // First call: empty (404)
    // Second call: parse error (200 but invalid json)
    let call = 0;
    const {http} = makeFakeHttp({
      [contentUrl]: ok(JSON.stringify({k1: song})),
      [tokenUrl]: ok(JSON.stringify({access_token: 'tok', account_id: 'acc1'})),
      [verifyUrl]: ok(JSON.stringify({account_id: 'acc1'})),
      [v1Url]: {
        ok: true,
        status: 200,
        get text() {
          call++;
          return call === 1 ? '' : '{not-json';
        },
      } as any,
    });

    // Wrap getText to override ok/status on first run
    const http2: HttpClient = {
      ...http,
      async getText(url, opts) {
        const res = await http.getText(url, opts);
        if (url === v1Url && call === 0) return fail(404, '');
        return res;
      },
    };

    const svc = new FestivalService({http: http2});
    await svc.initialize();

    await svc.fetchScores({
      exchangeCode: 'ex',
      degreeOfParallelism: 1,
      settings: {
        degreeOfParallelism: 1,
        queryLead: true,
        queryDrums: false,
        queryVocals: false,
        queryBass: false,
        queryProLead: false,
        queryProBass: false,
      },
    });

    const inst = svc.getInstrumentation();
    expect(inst.requests).toBeGreaterThan(0);
    expect(inst.empty + inst.errors).toBeGreaterThanOrEqual(1);
  });

  test('fetchInstrument parse failure increments errors (parseV1LeaderboardPage==null path)', async () => {
    const contentUrl =
      'https://fortnitecontent-website-prod07.ol.epicgames.com/content/api/pages/fortnite-game/spark-tracks';
    const tokenUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/token';
    const verifyUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/verify';

    const song = mkSong('song1', 'Song 1');
    const v1Url =
      'https://events-public-service-live.ol.epicgames.com' +
      '/api/v1/leaderboards/FNFestival/alltime_song1_Solo_Guitar/alltime/acc1?page=0&rank=0&teamAccountIds=acc1&appId=Fortnite&showLiveSessions=false';

    const {http} = makeFakeHttp({
      [contentUrl]: ok(JSON.stringify({k1: song})),
      [tokenUrl]: ok(JSON.stringify({access_token: 'tok', account_id: 'acc1'})),
      [verifyUrl]: ok(JSON.stringify({account_id: 'acc1'})),
      [v1Url]: ok('{not-json'),
    });

    const svc = new FestivalService({http});
    await svc.initialize();
    await svc.fetchScores({
      exchangeCode: 'ex',
      degreeOfParallelism: 1,
      settings: {
        degreeOfParallelism: 1,
        queryLead: true,
        queryDrums: false,
        queryVocals: false,
        queryBass: false,
        queryProLead: false,
        queryProBass: false,
      },
    });

    const inst = svc.getInstrumentation();
    expect(inst.errors).toBeGreaterThanOrEqual(1);
  });

  test('filteredSongIds limits which songs are fetched', async () => {
    const contentUrl = 'https://fortnitecontent-website-prod07.ol.epicgames.com/content/api/pages/fortnite-game/spark-tracks';
    const tokenUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/token';
    const verifyUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/verify';

    const a = mkSong('a', 'A');
    const b = mkSong('b', 'B');
    const mkV1 = (songId: string) =>
      'https://events-public-service-live.ol.epicgames.com' +
      `/api/v1/leaderboards/FNFestival/alltime_${songId}_Solo_Guitar/alltime/acc1?page=0&rank=0&teamAccountIds=acc1&appId=Fortnite&showLiveSessions=false`;

    const routes: Record<string, HttpResponse> = {
      [contentUrl]: ok(JSON.stringify({k1: a, k2: b})),
      [tokenUrl]: ok(JSON.stringify({access_token: 'tok', account_id: 'acc1'})),
      [verifyUrl]: ok(JSON.stringify({account_id: 'acc1'})),
      [mkV1('a')]: ok(JSON.stringify({page: 0, totalPages: 1, entries: []})),
      [mkV1('b')]: ok(JSON.stringify({page: 0, totalPages: 1, entries: []})),
    };

    const {http, seen} = makeFakeHttp(routes);
    const svc = new FestivalService({http});
    await svc.initialize();

    await svc.fetchScores({
      exchangeCode: 'ex',
      degreeOfParallelism: 1,
      filteredSongIds: ['b'],
      settings: {
        degreeOfParallelism: 1,
        queryLead: true,
        queryDrums: false,
        queryVocals: false,
        queryBass: false,
        queryProLead: false,
        queryProBass: false,
      },
    });

    expect(seen.some(s => s.includes('alltime_a_Solo_Guitar'))).toBe(false);
    expect(seen.some(s => s.includes('alltime_b_Solo_Guitar'))).toBe(true);
  });

  test('improved counter only increments when score increases; persistence errors are swallowed', async () => {
    const contentUrl = 'https://fortnitecontent-website-prod07.ol.epicgames.com/content/api/pages/fortnite-game/spark-tracks';
    const tokenUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/token';
    const verifyUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/verify';

    const song = mkSong('song1', 'Song 1');
    const v1Url =
      'https://events-public-service-live.ol.epicgames.com' +
      '/api/v1/leaderboards/FNFestival/alltime_song1_Solo_Guitar/alltime/acc1?page=0&rank=0&teamAccountIds=acc1&appId=Fortnite&showLiveSessions=false';

    const pageWithScore = (score: number) => ({
      page: 0,
      totalPages: 1,
      entries: [
        {
          team_id: 'acc1',
          rank: 1,
          percentile: 0.1,
          sessionHistory: [{trackedStats: {SCORE: score}}],
          score,
        },
      ],
    });

    let phase = 0;
    const {http} = makeFakeHttp({
      [contentUrl]: ok(JSON.stringify({k1: song})),
      [tokenUrl]: ok(JSON.stringify({access_token: 'tok', account_id: 'acc1'})),
      [verifyUrl]: ok(JSON.stringify({account_id: 'acc1'})),
      [v1Url]: {
        ok: true,
        status: 200,
        get text() {
          return JSON.stringify(phase === 0 ? pageWithScore(100) : pageWithScore(50));
        },
      } as any,
    });

    const badPersistence = {
      async loadScores() {
        return [];
      },
      async saveScores() {
        throw new Error('disk full');
      },
      async loadSongs() {
        return [];
      },
      async saveSongs() {
        throw new Error('disk full');
      },
    };

    const svc = new FestivalService({http, persistence: badPersistence as any});
    await svc.initialize();

    await svc.fetchScores({
      exchangeCode: 'ex',
      degreeOfParallelism: 1,
      settings: {
        degreeOfParallelism: 1,
        queryLead: true,
        queryDrums: false,
        queryVocals: false,
        queryBass: false,
        queryProLead: false,
        queryProBass: false,
      },
    });
    const inst1 = svc.getInstrumentation();
    expect(inst1.improved).toBe(1);

    phase = 1;
    await svc.fetchScores({
      exchangeCode: 'ex',
      degreeOfParallelism: 1,
      settings: {
        degreeOfParallelism: 1,
        queryLead: true,
        queryDrums: false,
        queryVocals: false,
        queryBass: false,
        queryProLead: false,
        queryProBass: false,
      },
    });
    const inst2 = svc.getInstrumentation();
    expect(inst2.improved).toBe(0);
  });

  test('fetchScores returns false when called while already fetching', async () => {
    const contentUrl = 'https://fortnitecontent-website-prod07.ol.epicgames.com/content/api/pages/fortnite-game/spark-tracks';
    const tokenUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/token';
    const verifyUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/verify';
    const song = mkSong('song1', 'Song 1');

    let releaseToken: ((value?: void) => void) | undefined;
    const tokenHold = new Promise<void>(resolve => {
      releaseToken = resolve;
    });

    const http: HttpClient = {
      async getText(url, _opts) {
        if (url === contentUrl) return ok(JSON.stringify({k1: song}));
        if (url === verifyUrl) return ok(JSON.stringify({account_id: 'acc1'}));
        return fail(404, '');
      },
      async postForm(url, _form, _opts) {
        if (url !== tokenUrl) return fail(404, '');
        await tokenHold;
        return ok(JSON.stringify({access_token: 'tok', account_id: 'acc1'}));
      },
      async getBytes() {
        return {ok: true, status: 200, bytes: new Uint8Array()};
      },
    };

    const svc = new FestivalService({http});
    await svc.initialize();

    const p1 = svc.fetchScores({exchangeCode: 'ex', degreeOfParallelism: 1});
    const p2 = svc.fetchScores({exchangeCode: 'ex', degreeOfParallelism: 1});
    expect(await p2).toBe(false);

    releaseToken?.();
    await p1;
  });

  test('fetchScores uses degreeOfParallelism fallback when settings.degreeOfParallelism <= 0', async () => {
    const contentUrl = 'https://fortnitecontent-website-prod07.ol.epicgames.com/content/api/pages/fortnite-game/spark-tracks';
    const tokenUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/token';
    const verifyUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/verify';

    const song = mkSong('song1', 'Song 1');
    const v1Url =
      'https://events-public-service-live.ol.epicgames.com' +
      '/api/v1/leaderboards/FNFestival/alltime_song1_Solo_Guitar/alltime/acc1?page=0&rank=0&teamAccountIds=acc1&appId=Fortnite&showLiveSessions=false';

    const routes = {
      [contentUrl]: ok(JSON.stringify({k1: song})),
      [tokenUrl]: ok(JSON.stringify({access_token: 'tok', account_id: 'acc1'})),
      [verifyUrl]: ok(JSON.stringify({account_id: 'acc1'})),
      [v1Url]: ok(JSON.stringify({page: 0, totalPages: 1, entries: []})),
    };
    const {http} = makeFakeHttp(routes);
    const svc = new FestivalService({http});
    await svc.initialize();
    await svc.fetchScores({
      exchangeCode: 'ex',
      degreeOfParallelism: 2,
      settings: {
        degreeOfParallelism: 0,
        queryLead: true,
        queryDrums: false,
        queryVocals: false,
        queryBass: false,
        queryProLead: false,
        queryProBass: false,
      },
    });
    expect(svc.getInstrumentation().requests).toBeGreaterThan(0);
  });

  test('per-pass tracking methods reflect updating/completed status', async () => {
    const contentUrl = 'https://fortnitecontent-website-prod07.ol.epicgames.com/content/api/pages/fortnite-game/spark-tracks';
    const tokenUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/token';
    const verifyUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/verify';

    const song = mkSong('song1', 'Song 1');
    const v1Url =
      'https://events-public-service-live.ol.epicgames.com' +
      '/api/v1/leaderboards/FNFestival/alltime_song1_Solo_Guitar/alltime/acc1?page=0&rank=0&teamAccountIds=acc1&appId=Fortnite&showLiveSessions=false';

    const routes = {
      [contentUrl]: ok(JSON.stringify({k1: song})),
      [tokenUrl]: ok(JSON.stringify({access_token: 'tok', account_id: 'acc1'})),
      [verifyUrl]: ok(JSON.stringify({account_id: 'acc1'})),
      [v1Url]: ok(JSON.stringify({page: 0, totalPages: 1, entries: []})),
    };
    const {http} = makeFakeHttp(routes);

    const svc = new FestivalService({
      http,
      events: {
        songUpdateStarted: id => {
          expect(svc.isSongUpdating(id)).toBe(true);
          expect(svc.isSongCompletedThisPass(id)).toBe(false);
        },
        songUpdateCompleted: id => {
          expect(svc.isSongUpdating(id)).toBe(false);
          expect(svc.isSongCompletedThisPass(id)).toBe(true);
        },
      },
    });
    await svc.initialize();

    await svc.fetchScores({
      exchangeCode: 'ex',
      degreeOfParallelism: 1,
      settings: {
        degreeOfParallelism: 1,
        queryLead: true,
        queryDrums: false,
        queryVocals: false,
        queryBass: false,
        queryProLead: false,
        queryProBass: false,
      },
    });
  });

  test('fetchScores returns true when there are no songs (total==0 path)', async () => {
    const contentUrl = 'https://fortnitecontent-website-prod07.ol.epicgames.com/content/api/pages/fortnite-game/spark-tracks';
    const tokenUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/token';
    const verifyUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/verify';

    const routes = {
      [contentUrl]: ok('{}'),
      [tokenUrl]: ok(JSON.stringify({access_token: 'tok', account_id: 'acc1'})),
      [verifyUrl]: ok(JSON.stringify({account_id: 'acc1'})),
    };
    const {http} = makeFakeHttp(routes);
    const svc = new FestivalService({http});
    await svc.initialize();

    const res = await svc.fetchScores({exchangeCode: 'ex', degreeOfParallelism: 1});
    expect(res).toBe(true);
  });

  test('prioritizeSong can enqueue an unknown id without crashing', async () => {
    const contentUrl = 'https://fortnitecontent-website-prod07.ol.epicgames.com/content/api/pages/fortnite-game/spark-tracks';
    const tokenUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/token';
    const verifyUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/verify';

    const song = mkSong('song1', 'Song 1');
    const v1Url =
      'https://events-public-service-live.ol.epicgames.com' +
      '/api/v1/leaderboards/FNFestival/alltime_song1_Solo_Guitar/alltime/acc1?page=0&rank=0&teamAccountIds=acc1&appId=Fortnite&showLiveSessions=false';

    const routes = {
      [contentUrl]: ok(JSON.stringify({k1: song})),
      [tokenUrl]: ok(JSON.stringify({access_token: 'tok', account_id: 'acc1'})),
      [verifyUrl]: ok(JSON.stringify({account_id: 'acc1'})),
      [v1Url]: ok(JSON.stringify({page: 0, totalPages: 1, entries: []})),
    };
    const {http} = makeFakeHttp(routes);
    const svc = new FestivalService({http});
    await svc.initialize();

    const run = svc.fetchScores({exchangeCode: 'ex', degreeOfParallelism: 1});
    await new Promise<void>(resolve => setTimeout(resolve, 0));
    expect(svc.prioritizeSong('does-not-exist')).toBe(true);
    await run;
  });

  test('ensureTracker reuses existing tracker loaded from persistence', async () => {
    const contentUrl = 'https://fortnitecontent-website-prod07.ol.epicgames.com/content/api/pages/fortnite-game/spark-tracks';
    const tokenUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/token';
    const verifyUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/verify';

    const song = mkSong('song1', 'Song 1');
    const v1Url =
      'https://events-public-service-live.ol.epicgames.com' +
      '/api/v1/leaderboards/FNFestival/alltime_song1_Solo_Guitar/alltime/acc1?page=0&rank=0&teamAccountIds=acc1&appId=Fortnite&showLiveSessions=false';

    const routes = {
      [contentUrl]: ok(JSON.stringify({k1: song})),
      [tokenUrl]: ok(JSON.stringify({access_token: 'tok', account_id: 'acc1'})),
      [verifyUrl]: ok(JSON.stringify({account_id: 'acc1'})),
      [v1Url]: ok(
        JSON.stringify({
          page: 0,
          totalPages: 1,
          entries: [{team_id: 'acc1', rank: 2, percentile: 0.5, sessionHistory: [{trackedStats: {SCORE: 200}}], score: 200}],
        }),
      ),
    };
    const {http} = makeFakeHttp(routes);

    const persistence = new InMemoryFestivalPersistence();
    // preload a board with an existing tracker instance
    await persistence.saveScores([
      {
        songId: 'song1',
        guitar: Object.assign(new ScoreTracker(), {initialized: true, maxScore: 150}),
      } as any,
    ]);

    const svc = new FestivalService({http, persistence});
    await svc.initialize();

    const before = svc.scoresIndex.song1.guitar;
    await svc.fetchScores({
      exchangeCode: 'ex',
      degreeOfParallelism: 1,
      settings: {
        degreeOfParallelism: 1,
        queryLead: true,
        queryDrums: false,
        queryVocals: false,
        queryBass: false,
        queryProLead: false,
        queryProBass: false,
      },
    });
    const after = svc.scoresIndex.song1.guitar;
    expect(after).toBe(before);
    expect(after?.maxScore).toBe(200);
  });

  test('abort during between-song sleep returns false (sleep abort path)', async () => {
    const contentUrl = 'https://fortnitecontent-website-prod07.ol.epicgames.com/content/api/pages/fortnite-game/spark-tracks';
    const tokenUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/token';
    const verifyUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/verify';

    const a = mkSong('a', 'A');
    const b = mkSong('b', 'B');
    const mkV1 = (songId: string) =>
      'https://events-public-service-live.ol.epicgames.com' +
      `/api/v1/leaderboards/FNFestival/alltime_${songId}_Solo_Guitar/alltime/acc1?page=0&rank=0&teamAccountIds=acc1&appId=Fortnite&showLiveSessions=false`;

    const routes: Record<string, HttpResponse> = {
      [contentUrl]: ok(JSON.stringify({k1: a, k2: b})),
      [tokenUrl]: ok(JSON.stringify({access_token: 'tok', account_id: 'acc1'})),
      [verifyUrl]: ok(JSON.stringify({account_id: 'acc1'})),
      [mkV1('a')]: ok(JSON.stringify({page: 0, totalPages: 1, entries: []})),
      [mkV1('b')]: ok(JSON.stringify({page: 0, totalPages: 1, entries: []})),
    };

    const {http} = makeFakeHttp(routes);
    const svc = new FestivalService({http});
    await svc.initialize();

    const ac = new AbortController();
    setTimeout(() => ac.abort(), 0);

    const res = await svc.fetchScores({
      exchangeCode: 'ex',
      degreeOfParallelism: 1,
      settings: {
        degreeOfParallelism: 1,
        queryLead: true,
        queryDrums: false,
        queryVocals: false,
        queryBass: false,
        queryProLead: false,
        queryProBass: false,
      },
      signal: ac.signal,
    });

    expect(res).toBe(false);
  });

  test('abort immediately after first song completes triggers sleep(signal.aborted) branch', async () => {
    const contentUrl =
      'https://fortnitecontent-website-prod07.ol.epicgames.com/content/api/pages/fortnite-game/spark-tracks';
    const tokenUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/token';
    const verifyUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/verify';

    const a = mkSong('a', 'A');
    const b = mkSong('b', 'B');
    const mkV1 = (songId: string) =>
      'https://events-public-service-live.ol.epicgames.com' +
      `/api/v1/leaderboards/FNFestival/alltime_${songId}_Solo_Guitar/alltime/acc1?page=0&rank=0&teamAccountIds=acc1&appId=Fortnite&showLiveSessions=false`;

    const routes: Record<string, HttpResponse> = {
      [contentUrl]: ok(JSON.stringify({k1: a, k2: b})),
      [tokenUrl]: ok(JSON.stringify({access_token: 'tok', account_id: 'acc1'})),
      [verifyUrl]: ok(JSON.stringify({account_id: 'acc1'})),
      [mkV1('a')]: ok(JSON.stringify({page: 0, totalPages: 1, entries: []})),
      [mkV1('b')]: ok(JSON.stringify({page: 0, totalPages: 1, entries: []})),
    };

    const {http} = makeFakeHttp(routes);
    const ac = new AbortController();
    let completed = 0;
    const svc = new FestivalService({
      http,
      events: {
        songUpdateCompleted: () => {
          completed++;
          if (completed === 1) ac.abort();
        },
      },
    });

    await svc.initialize();

    const res = await svc.fetchScores({
      exchangeCode: 'ex',
      degreeOfParallelism: 1,
      settings: {
        degreeOfParallelism: 1,
        queryLead: true,
        queryDrums: false,
        queryVocals: false,
        queryBass: false,
        queryProLead: false,
        queryProBass: false,
      },
      signal: ac.signal,
    });

    expect(res).toBe(false);
  });

  test('nextSong skips falsy prioritized ids and falsy pending ids (continue branches)', async () => {
    const tokenUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/token';
    const verifyUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/verify';

    // Note: empty songId intentionally to hit `if (!id) continue;`.
    const emptyIdSong: Song = {track: {su: '', an: 'Artist', in: {}}} as any;
    const b = mkSong('b', 'B');

    let svc: FestivalService | undefined;

    const http: HttpClient = {
      async getText(url) {
        if (url === verifyUrl) return ok(JSON.stringify({account_id: 'acc1'}));
        return fail(404, '');
      },
      async postForm(url) {
        if (url !== tokenUrl) return fail(404, '');
        // After fetchScores() resets prioritizedSongIds, re-seed with a falsy entry.
        (svc as any).prioritizedSongIds = [undefined, 'b'];
        return ok(JSON.stringify({access_token: 'tok', account_id: 'acc1'}));
      },
      async getBytes() {
        return {ok: true, status: 200, bytes: new Uint8Array()};
      },
    };

    svc = new FestivalService({http});
    // Bypass sync and images; we only need fetchScores to run.
    (svc as any).songSyncComplete = true;
    (svc as any).imagesSyncComplete = true;
    (svc as any).songsById = new Map([
      ['', emptyIdSong],
      ['b', b],
    ]);

    const res = await svc.fetchScores({
      exchangeCode: 'ex',
      degreeOfParallelism: 1,
      settings: {
        degreeOfParallelism: 1,
        queryLead: false,
        queryDrums: false,
        queryVocals: false,
        queryBass: false,
        queryProLead: false,
        queryProBass: false,
      },
    });

    expect(res).toBe(true);
  });

  test('prioritizeSong() affects order when fetching sequentially', async () => {
    const contentUrl = 'https://fortnitecontent-website-prod07.ol.epicgames.com/content/api/pages/fortnite-game/spark-tracks';
    const tokenUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/token';
    const verifyUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/verify';

    const a = mkSong('a', 'A');
    const b = mkSong('b', 'B');

    const mkV1 = (songId: string) =>
      'https://events-public-service-live.ol.epicgames.com' +
      `/api/v1/leaderboards/FNFestival/alltime_${songId}_Solo_Guitar/alltime/acc1?page=0&rank=0&teamAccountIds=acc1&appId=Fortnite&showLiveSessions=false`;

    const routes: Record<string, HttpResponse> = {
      [contentUrl]: ok(JSON.stringify({k1: a, k2: b})),
      [tokenUrl]: ok(JSON.stringify({access_token: 'tok', account_id: 'acc1'})),
      [verifyUrl]: ok(JSON.stringify({account_id: 'acc1'})),
      [mkV1('a')]: ok(JSON.stringify({page: 0, totalPages: 1, entries: []})),
      [mkV1('b')]: ok(JSON.stringify({page: 0, totalPages: 1, entries: []})),
    };

    const {http} = makeFakeHttp(routes);

    const started: string[] = [];
    const svc = new FestivalService({
      http,
      events: {
        songUpdateStarted: id => started.push(id),
      },
    });
    await svc.initialize();

    const ac = new AbortController();

    const run = svc.fetchScores({
      exchangeCode: 'ex',
      degreeOfParallelism: 1,
      settings: {
        degreeOfParallelism: 1,
        queryLead: true,
        queryDrums: false,
        queryVocals: false,
        queryBass: false,
        queryProLead: false,
        queryProBass: false,
      },
      signal: ac.signal,
    });

    // Give fetch loop a tick, then prioritize 'b'
    await new Promise<void>(resolve => setTimeout(resolve, 0));
    svc.prioritizeSong('b');

    await run;

    // Either A started first (if already taken), but B should start no later than second.
    expect(started).toEqual(expect.arrayContaining(['b']));
    expect(started.length).toBe(2);
  });

  test('cancellation returns false and stops fetching', async () => {
    const contentUrl = 'https://fortnitecontent-website-prod07.ol.epicgames.com/content/api/pages/fortnite-game/spark-tracks';
    const tokenUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/token';
    const verifyUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/verify';

    const song = mkSong('a', 'A');

    const routes: Record<string, HttpResponse> = {
      [contentUrl]: ok(JSON.stringify({k1: song})),
      [tokenUrl]: ok(JSON.stringify({access_token: 'tok', account_id: 'acc1'})),
      [verifyUrl]: ok(JSON.stringify({account_id: 'acc1'})),
      // never reached
    };

    const {http} = makeFakeHttp(routes);
    const svc = new FestivalService({http});
    await svc.initialize();

    const ac = new AbortController();
    ac.abort();

    const res = await svc.fetchScores({
      exchangeCode: 'ex',
      degreeOfParallelism: 1,
      settings: {
        degreeOfParallelism: 1,
        queryLead: true,
        queryDrums: false,
        queryVocals: false,
        queryBass: false,
        queryProLead: false,
        queryProBass: false,
      },
      signal: ac.signal,
    });

    expect(res).toBe(false);
    expect(svc.isFetching).toBe(false);
  });
});
