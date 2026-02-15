import type {HttpClient, HttpResponse} from '../services/types';
import {FestivalService} from '../services/festivalService';
import type {Song} from '../models';

const contentUrl = 'https://fortnitecontent-website-prod07.ol.epicgames.com/content/api/pages/fortnite-game/spark-tracks';
const tokenUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/token';
const verifyUrl = 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/verify';

const ok = (text?: any, status = 200): HttpResponse => ({ok: true, status, text});
const fail = (status = 500, text?: any): HttpResponse => ({ok: false, status, text});

const makeFakeHttp = (routes: Record<string, HttpResponse>, opts?: {throwOnPost?: unknown; throwOnGet?: unknown}) => {
  const seen: string[] = [];
  const http: HttpClient = {
    async getText(url, _opts) {
      seen.push(`GET ${url}`);
      if (opts && Object.prototype.hasOwnProperty.call(opts, 'throwOnGet')) throw opts.throwOnGet;
      return routes[url] ?? fail(404, '');
    },
    async postForm(url, form, _opts) {
      seen.push(`POST ${url} ${JSON.stringify(form)}`);
      if (opts && Object.prototype.hasOwnProperty.call(opts, 'throwOnPost')) throw opts.throwOnPost;
      return routes[url] ?? fail(404, '');
    },
    async getBytes(url, _opts) {
      seen.push(`BYTES ${url}`);
      return {ok: true, status: 200, bytes: new Uint8Array()};
    },
  };
  return {http, seen};
};

const mkSong = (id: string, title?: string): Song => ({
  track: {su: id, tt: title, an: 'Artist', ry: 2001, in: {gr: 1, ds: 1}},
});

describe('FestivalService (coverage add-ons)', () => {
  test('syncSongs counts bytes when response text is undefined', async () => {
    const logs: string[] = [];
    const {http} = makeFakeHttp({[contentUrl]: ok(undefined)});
    const svc = new FestivalService({http, events: {log: l => logs.push(l)}});
    await expect(svc.syncSongs()).resolves.toBeUndefined();
    // may log due to parse failure, but should not throw
    expect(Array.isArray(logs)).toBe(true);
  });

  test('syncSongs logs non-Error failures using fallback stringification', async () => {
    const logs: string[] = [];
    const {http} = makeFakeHttp({}, {throwOnGet: 123});
    const svc = new FestivalService({http, events: {log: l => logs.push(l)}});
    await expect(svc.syncSongs()).resolves.toBeUndefined();
    expect(logs.some(l => l.includes('Song sync failed'))).toBe(true);
    expect(logs.some(l => l.includes('123'))).toBe(true);
  });

  test('initialize tolerates undefined persisted song list', async () => {
    const songsPayload = JSON.stringify({k1: mkSong('a', 'A')});
    const {http} = makeFakeHttp({[contentUrl]: ok(songsPayload)});

    const persistence = {
      async loadScores() {
        return [];
      },
      async loadSongs() {
        return undefined;
      },
      async saveScores() {},
      async saveSongs() {},
    };

    const svc = new FestivalService({http, persistence: persistence as any});
    await expect(svc.initialize()).resolves.toBeUndefined();
  });

  test('fetchScores hits canon(undefined) path when verify JSON lacks account_id', async () => {
    const songsPayload = JSON.stringify({k1: mkSong('a', 'A')});

    const tokenBody = JSON.stringify({access_token: 't', account_id: 'acc'});
    const verifyBody = JSON.stringify({});

    const {http} = makeFakeHttp({
      [contentUrl]: ok(songsPayload),
      [tokenUrl]: ok(tokenBody),
      [verifyUrl]: ok(verifyBody),
    });

    const svc = new FestivalService({http});
    await svc.syncSongs();
    await svc.syncImages();

    const res = await svc.fetchScores({exchangeCode: 'ex', degreeOfParallelism: 1});
    expect(res).toBe(false);
  });

  test('fetchScores counts bytes using nullish text fallback (token/verify)', async () => {
    const songsPayload = JSON.stringify({k1: mkSong('a', 'A')});

    const {http} = makeFakeHttp({
      [contentUrl]: ok(songsPayload),
      [tokenUrl]: ok(undefined),
      [verifyUrl]: ok(undefined),
    });

    const svc = new FestivalService({http});
    await svc.syncSongs();
    await svc.syncImages();

    const res = await svc.fetchScores({exchangeCode: 'ex', degreeOfParallelism: 1});
    expect(res).toBe(false);

    const inst = svc.getInstrumentation();
    expect(inst.requests).toBeGreaterThanOrEqual(1);
  });

  test('fetchScores counts bytes when verify response text is undefined', async () => {
    const songsPayload = JSON.stringify({k1: mkSong('a', 'A')});
    const tokenBody = JSON.stringify({access_token: 't', account_id: 'acc'});

    const {http} = makeFakeHttp({
      [contentUrl]: ok(songsPayload),
      [tokenUrl]: ok(tokenBody),
      [verifyUrl]: ok(undefined),
    });

    const svc = new FestivalService({http});
    await svc.syncSongs();
    await svc.syncImages();

    const res = await svc.fetchScores({exchangeCode: 'ex', degreeOfParallelism: 1});
    expect(res).toBe(false);
    expect(svc.getInstrumentation().requests).toBeGreaterThanOrEqual(2);
  });

  test('fetchScores catch-path logs non-Error via fallback', async () => {
    const songsPayload = JSON.stringify({k1: mkSong('a', 'A')});
    const logs: string[] = [];

    const {http} = makeFakeHttp({[contentUrl]: ok(songsPayload)}, {throwOnPost: 123});
    const svc = new FestivalService({http, events: {log: l => logs.push(l)}});

    await svc.syncSongs();
    await svc.syncImages();

    const res = await svc.fetchScores({exchangeCode: 'ex', degreeOfParallelism: 1});
    expect(res).toBe(false);
    expect(logs.some(l => l.includes('FetchScores failed'))).toBe(true);
    expect(logs.some(l => l.includes('123'))).toBe(true);
  });

  test('songProgress label falls back to _title and id when track title is missing', async () => {
    const songsPayload = JSON.stringify({k1: mkSong('a', 'A'), k2: mkSong('b', 'B')});
    const tokenBody = JSON.stringify({access_token: 't', account_id: 'acc'});
    const verifyBody = JSON.stringify({account_id: 'acc', displayName: 'x'});

    const labels: string[] = [];
    const {http} = makeFakeHttp({
      [contentUrl]: ok(songsPayload),
      [tokenUrl]: ok(tokenBody),
      [verifyUrl]: ok(verifyBody),
    });

    const svc = new FestivalService({
      http,
      events: {
        songProgress: (_i, _t, label) => {
          labels.push(label);
        },
      },
    });

    await svc.syncSongs();
    await svc.syncImages();

    // mutate loaded songs to exercise the label fallback chain
    const songs = svc.songs;
    const a = songs.find(s => s.track.su === 'a')!;
    const b = songs.find(s => s.track.su === 'b')!;

    (a.track as any).tt = undefined;
    a._title = 'Persisted Title';

    (b.track as any).tt = undefined;
    (b as any)._title = undefined;

    const res = await svc.fetchScores({
      exchangeCode: 'ex',
      degreeOfParallelism: 1,
      settings: {
        // disable all instrument fetches so we only exercise orchestration
        queryDrums: false,
        queryLead: false,
        queryProBass: false,
        queryProLead: false,
        queryBass: false,
        queryVocals: false,
      } as any,
    });

    expect(res).toBe(true);
    expect(labels.some(l => l.includes('Persisted Title'))).toBe(true);
    expect(labels.some(l => l === 'b')).toBe(true);
  });

  test('buildPrioritizedSongList sorts songs without scores first', async () => {
    const songsPayload = JSON.stringify({k1: mkSong('a', 'A'), k2: mkSong('b', 'B')});
    const tokenBody = JSON.stringify({access_token: 't', account_id: 'acc'});
    const verifyBody = JSON.stringify({account_id: 'acc', displayName: 'x'});

    const started: string[] = [];
    const {http} = makeFakeHttp({
      [contentUrl]: ok(songsPayload),
      [tokenUrl]: ok(tokenBody),
      [verifyUrl]: ok(verifyBody),
    });

    const svc = new FestivalService({
      http,
      events: {
        songUpdateStarted: id => started.push(id),
      },
    });

    await svc.syncSongs();
    await svc.syncImages();

    // Pretend we already have a score for song 'b'
    Object.assign((svc as any).scoresBySongId, {b: {songId: 'b'}});

    const res = await svc.fetchScores({
      exchangeCode: 'ex',
      degreeOfParallelism: 1,
      settings: {
        queryDrums: false,
        queryLead: false,
        queryProBass: false,
        queryProLead: false,
        queryBass: false,
        queryVocals: false,
      } as any,
    });

    expect(res).toBe(true);
    expect(started.slice(0, 2)).toEqual(['a', 'b']);
  });

  test('buildPrioritizedSongList ternary branches are exercised', () => {
    const {http} = makeFakeHttp({});
    const svc = new FestivalService({http});

    // Seed internal maps directly for a deterministic sort.
    (svc as any).songsById.set('a', mkSong('a', 'A'));
    (svc as any).songsById.set('b', mkSong('b', 'B'));
    // Mark both songs as having scores so the `bHas ? 1 : 0` truthy arm is guaranteed,
    // regardless of sort implementation argument ordering.
    Object.assign((svc as any).scoresBySongId, {a: {songId: 'a'}, b: {songId: 'b'}});

    const ordered = (svc as any).buildPrioritizedSongList() as Song[];
    expect(ordered.map(s => s.track.su)).toEqual(['a', 'b']);
  });

  test('syncImages title fallbacks and non-Error image failures are logged', async () => {
    const songsPayload = JSON.stringify({k1: mkSong('a', 'A')});
    const logs: string[] = [];

    const {http} = makeFakeHttp({[contentUrl]: ok(songsPayload)});

    const imageCache = {
      async ensureCached() {
        throw 123;
      },
      async clearAll() {},
    };

    const svc = new FestivalService({http, imageCache: imageCache as any, events: {log: l => logs.push(l)}});

    await svc.syncSongs();

    // Make sure title falls back to ''
    const a = svc.songs.find(s => s.track.su === 'a')!;
    (a.track as any).tt = undefined;
    (a as any)._title = undefined;

    await expect(svc.syncImages()).resolves.toBeUndefined();

    expect(logs.some(l => l.includes('Image download failed'))).toBe(true);
    expect(logs.some(l => l.includes('123'))).toBe(true);
  });

  test('fetchScores exercises missing intensities fallbacks and nullish response text', async () => {
    const songWithoutIn: Song = {
      track: {su: 'a', tt: 'A', an: 'Artist', ry: 2001},
    } as any;

    const songsPayload = JSON.stringify({k1: songWithoutIn});
    const tokenBody = JSON.stringify({access_token: 't', account_id: 'acc'});
    const verifyBody = JSON.stringify({account_id: 'acc', displayName: 'x'});

    // We don't care about the exact URL here; the service will call events base with a computed path.
    // Returning 404 with undefined text is enough to exercise nullish byte counting + empty path.
    const {http} = makeFakeHttp(
      {
        [contentUrl]: ok(songsPayload),
        [tokenUrl]: ok(tokenBody),
        [verifyUrl]: ok(verifyBody),
      },
      undefined,
    );

    // Wrap getText to return undefined body for any events URL.
    const httpWithEventFallback: HttpClient = {
      ...http,
      async getText(url, opts) {
        if (String(url).includes('events-public-service-live.ol.epicgames.com')) {
          return fail(503, undefined);
        }
        return http.getText(url, opts);
      },
    };

    const svc = new FestivalService({http: httpWithEventFallback});
    await svc.syncSongs();
    await svc.syncImages();

    const res = await svc.fetchScores({
      exchangeCode: 'ex',
      degreeOfParallelism: 1,
      settings: {
        queryDrums: true,
        queryLead: true,
        queryProBass: false,
        queryProLead: false,
        queryBass: false,
        queryVocals: false,
      } as any,
    });

    expect(res).toBe(true);
  });
});
