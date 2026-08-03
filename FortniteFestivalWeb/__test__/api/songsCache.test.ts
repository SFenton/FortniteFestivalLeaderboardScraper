import { beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '../../src/api/client';
import {
  clearSongsCache,
  PUBLIC_CATALOG_CACHE_SCOPE,
  readSongsCache,
  SONGS_CACHE_KEY,
  SONGS_CACHE_VERSION,
} from '../../src/api/songsCache';
import { setPublicationForTests } from '../../src/api/publication';

const cachedSongs = {
  count: 1,
  currentSeason: 7,
  songs: [{
    songId: 'cached-song',
    title: 'Cached Song',
    artist: 'Cached Artist',
    albumArt: 'https://example.com/cached.jpg',
  }],
};

beforeEach(() => {
  vi.restoreAllMocks();
  localStorage.clear();
  clearSongsCache();
  setPublicationForTests(42, false);
  global.fetch = vi.fn();
});

describe('Songs cache ownership', () => {
  it('parses one storage update once across placeholder and API owners', async () => {
    localStorage.setItem(SONGS_CACHE_KEY, JSON.stringify({
      version: SONGS_CACHE_VERSION,
      scope: PUBLIC_CATALOG_CACHE_SCOPE,
      publicationId: 42,
      data: cachedSongs,
      etag: '"songs-etag"',
    }));
    const parse = vi.spyOn(JSON, 'parse');
    (global.fetch as ReturnType<typeof vi.fn>).mockResolvedValue({
      ok: false,
      status: 304,
      headers: new Headers({ etag: '"songs-etag"' }),
    });

    const first = readSongsCache();
    const second = readSongsCache();
    const response = await api.getSongs();

    expect(first).toBe(second);
    expect(response).toBe(first?.data);
    expect(parse).toHaveBeenCalledTimes(1);
  });

  it('invalidates publication-less data during the version-4 cache migration', () => {
    localStorage.setItem(SONGS_CACHE_KEY, JSON.stringify({
      version: 3,
      scope: PUBLIC_CATALOG_CACHE_SCOPE,
      data: cachedSongs,
      etag: '"legacy-etag"',
    }));

    expect(readSongsCache()).toBeNull();
    expect(localStorage.getItem(SONGS_CACHE_KEY)).toBeNull();
  });

  it('removes data cached for a different publication', () => {
    localStorage.setItem(SONGS_CACHE_KEY, JSON.stringify({
      version: SONGS_CACHE_VERSION,
      scope: PUBLIC_CATALOG_CACHE_SCOPE,
      publicationId: 41,
      data: cachedSongs,
      etag: '"old-publication-etag"',
    }));

    expect(readSongsCache()).toBeNull();
    expect(localStorage.getItem(SONGS_CACHE_KEY)).toBeNull();
  });

  it('accepts legacy null values in partial max-score records', () => {
    localStorage.setItem(SONGS_CACHE_KEY, JSON.stringify({
      version: SONGS_CACHE_VERSION,
      scope: PUBLIC_CATALOG_CACHE_SCOPE,
      publicationId: 42,
      data: {
        count: 1,
        currentSeason: 7,
        songs: [{
          songId: 'partial-max',
          title: 'Partial Max',
          artist: 'Artist',
          maxScores: {
            Solo_Guitar: null,
            Solo_PeripheralGuitar: 209100,
          },
        }],
      },
      etag: '"partial-max"',
    }));

    expect(readSongsCache()?.data.songs[0]?.maxScores).toEqual({
      Solo_Guitar: null,
      Solo_PeripheralGuitar: 209100,
    });
  });

  it.each([
    ['invalid JSON', '{not-json'],
    ['unsupported version', JSON.stringify({
      version: 99,
      scope: 'public',
      publicationId: 42,
      data: cachedSongs,
      etag: null,
    })],
    ['wrong scope', JSON.stringify({
      version: SONGS_CACHE_VERSION,
      scope: 'profile-a',
      publicationId: 42,
      data: cachedSongs,
      etag: null,
    })],
    ['invalid song shape', JSON.stringify({
      version: SONGS_CACHE_VERSION,
      scope: PUBLIC_CATALOG_CACHE_SCOPE,
      publicationId: 42,
      data: { count: 1, songs: [{ songId: 'missing-fields' }] },
      etag: null,
    })],
    ['invalid optional song metadata', JSON.stringify({
      version: SONGS_CACHE_VERSION,
      scope: PUBLIC_CATALOG_CACHE_SCOPE,
      publicationId: 42,
      data: {
        count: 1,
        songs: [{
          songId: 'bad-metadata',
          title: 'Bad Metadata',
          artist: 'Artist',
          genres: 'not-an-array',
        }],
      },
      etag: null,
    })],
  ])('removes %s rather than exposing unvalidated placeholder data', (_label, raw) => {
    localStorage.setItem(SONGS_CACHE_KEY, raw);

    expect(readSongsCache()).toBeNull();
    expect(localStorage.getItem(SONGS_CACHE_KEY)).toBeNull();
  });
});
