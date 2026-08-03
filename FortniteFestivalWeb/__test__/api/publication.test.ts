import { beforeEach, describe, expect, it, vi } from 'vitest';
import {
  activatePublicationBootstrap,
  ensurePublication,
  fetchWithPublication,
  PUBLICATION_CHANGED_EVENT,
  resetPublicationForTests,
  setPublicationForTests,
} from '../../src/api/publication';

function jsonResponse(data: unknown, status = 200): Response {
  return new Response(JSON.stringify(data), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

beforeEach(() => {
  resetPublicationForTests();
  localStorage.clear();
  global.fetch = vi.fn();
});

describe('publication bootstrap', () => {
  it('deduplicates bootstrap and pins request URLs', async () => {
    activatePublicationBootstrap();
    (global.fetch as ReturnType<typeof vi.fn>)
      .mockResolvedValueOnce(jsonResponse({
        publicationId: 42,
        previousPublicationId: 41,
        publishedScrapeId: 1271,
        publishedAt: '2026-07-30T19:35:02Z',
        pinningEnabled: true,
      }))
      .mockResolvedValueOnce(jsonResponse({ ok: true }));

    const [first, second] = await Promise.all([
      ensurePublication(),
      ensurePublication(),
    ]);
    expect(first.publicationId).toBe(42);
    expect(second.publicationId).toBe(42);
    expect(global.fetch).toHaveBeenCalledTimes(1);

    await fetchWithPublication('/api/songs?limit=10', {
      headers: { Accept: 'application/json' },
    });

    expect(global.fetch).toHaveBeenLastCalledWith(
      '/api/songs?limit=10&publicationId=42',
      { headers: { Accept: 'application/json' } },
    );
  });

  it('revalidates a stored publication session without staying no-cache', async () => {
    localStorage.setItem('fst_publication_id', '42');
    activatePublicationBootstrap();
    (global.fetch as ReturnType<typeof vi.fn>)
      .mockResolvedValueOnce(jsonResponse({
        publicationId: 42,
        previousPublicationId: 41,
        publishedScrapeId: 1271,
        publishedAt: '2026-07-30T19:35:02Z',
        pinningEnabled: false,
      }))
      .mockResolvedValueOnce(jsonResponse({ ok: true }))
      .mockResolvedValueOnce(jsonResponse({ ok: true }));

    await ensurePublication();
    await fetchWithPublication('/api/rankings/Solo_Drums');
    await fetchWithPublication('/api/rankings/Solo_Drums');

    expect(global.fetch).toHaveBeenNthCalledWith(
      2,
      '/api/rankings/Solo_Drums',
      { cache: 'no-cache' },
    );
    expect(global.fetch).toHaveBeenNthCalledWith(
      3,
      '/api/rankings/Solo_Drums',
      undefined,
    );
  });

  it('refreshes and emits a change event after a 409', async () => {
    setPublicationForTests(42);
    const changes: number[] = [];
    window.addEventListener(PUBLICATION_CHANGED_EVENT, event => {
      changes.push(
        (event as CustomEvent<{ publicationId: number }>).detail.publicationId,
      );
    }, { once: true });

    (global.fetch as ReturnType<typeof vi.fn>)
      .mockResolvedValueOnce(jsonResponse({
        status: 'publication_changed',
        currentPublicationId: 43,
      }, 409))
      .mockResolvedValueOnce(jsonResponse({
        publicationId: 43,
        previousPublicationId: 42,
        publishedScrapeId: 1272,
        publishedAt: '2026-07-31T00:00:00Z',
        pinningEnabled: true,
      }))
      .mockResolvedValueOnce(jsonResponse({ ok: true }))
      .mockResolvedValueOnce(jsonResponse({ ok: true }));

    const response = await fetchWithPublication('/api/songs');
    await fetchWithPublication('/api/songs');

    expect(response.ok).toBe(true);
    expect(changes).toEqual([43]);
    expect(global.fetch).toHaveBeenNthCalledWith(
      1,
      '/api/songs?publicationId=42',
      undefined,
    );
    expect(global.fetch).toHaveBeenNthCalledWith(
      2,
      '/api/publication',
      { cache: 'no-store' },
    );
    expect(global.fetch).toHaveBeenNthCalledWith(
      3,
      '/api/songs?publicationId=43',
      { cache: 'no-cache' },
    );
    expect(global.fetch).toHaveBeenNthCalledWith(
      4,
      '/api/songs?publicationId=43',
      undefined,
    );
  });

  it('can force a cache-boundary event without changing publication ID', async () => {
    setPublicationForTests(42);
    const changes: number[] = [];
    window.addEventListener(PUBLICATION_CHANGED_EVENT, event => {
      changes.push(
        (event as CustomEvent<{ publicationId: number }>).detail.publicationId,
      );
    }, { once: true });
    (global.fetch as ReturnType<typeof vi.fn>)
      .mockResolvedValueOnce(
        jsonResponse({
          publicationId: 42,
          previousPublicationId: 41,
          publishedScrapeId: 1274,
          publishedAt: '2026-08-02T00:00:00Z',
          pinningEnabled: false,
        }),
      )
      .mockResolvedValueOnce(jsonResponse({ ok: true }))
      .mockResolvedValueOnce(jsonResponse({ ok: true }));

    await ensurePublication(true, true);
    await fetchWithPublication('/api/rankings/Solo_PeripheralGuitar');
    await fetchWithPublication('/api/rankings/Solo_PeripheralGuitar');

    expect(changes).toEqual([42]);
    expect(global.fetch).toHaveBeenNthCalledWith(
      2,
      '/api/rankings/Solo_PeripheralGuitar',
      { cache: 'no-cache' },
    );
    expect(global.fetch).toHaveBeenNthCalledWith(
      3,
      '/api/rankings/Solo_PeripheralGuitar',
      undefined,
    );
  });

  it('revalidates each resource once after an actual publication change', async () => {
    setPublicationForTests(42, false);
    (global.fetch as ReturnType<typeof vi.fn>)
      .mockResolvedValueOnce(jsonResponse({
        publicationId: 43,
        previousPublicationId: 42,
        publishedScrapeId: 1275,
        publishedAt: '2026-08-03T00:00:00Z',
        pinningEnabled: false,
      }))
      .mockResolvedValue(jsonResponse({ ok: true }));

    await ensurePublication(true);
    await Promise.all([
      fetchWithPublication('/api/rankings/Solo_Guitar'),
      fetchWithPublication('/api/player/player-1'),
    ]);
    await fetchWithPublication('/api/rankings/Solo_Guitar');
    await fetchWithPublication('/api/player/player-1');

    expect(global.fetch).toHaveBeenNthCalledWith(
      2,
      '/api/rankings/Solo_Guitar',
      { cache: 'no-cache' },
    );
    expect(global.fetch).toHaveBeenNthCalledWith(
      3,
      '/api/player/player-1',
      { cache: 'no-cache' },
    );
    expect(global.fetch).toHaveBeenNthCalledWith(
      4,
      '/api/rankings/Solo_Guitar',
      undefined,
    );
    expect(global.fetch).toHaveBeenNthCalledWith(
      5,
      '/api/player/player-1',
      undefined,
    );
  });

  it('serializes concurrent refreshes for the same resource', async () => {
    setPublicationForTests(42, false);
    const fetchMock = global.fetch as ReturnType<typeof vi.fn>;
    fetchMock.mockResolvedValueOnce(jsonResponse({
      publicationId: 42,
      previousPublicationId: 41,
      publishedScrapeId: 1276,
      publishedAt: '2026-08-03T01:00:00Z',
      pinningEnabled: false,
    }));
    await ensurePublication(true, true);

    let resolveRequests!: (response: Response) => void;
    const pendingResponse = new Promise<Response>(resolve => {
      resolveRequests = resolve;
    });
    fetchMock
      .mockReturnValueOnce(pendingResponse)
      .mockResolvedValue(jsonResponse({ ok: true }));

    const first = fetchWithPublication('/api/rankings/Solo_Bass');
    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2));
    const second = fetchWithPublication('/api/rankings/Solo_Bass');
    await Promise.resolve();

    expect(fetchMock).toHaveBeenNthCalledWith(
      2,
      '/api/rankings/Solo_Bass',
      { cache: 'no-cache' },
    );
    expect(fetchMock).toHaveBeenCalledTimes(2);

    resolveRequests(jsonResponse({ ok: true }));
    await Promise.all([first, second]);
    expect(fetchMock).toHaveBeenNthCalledWith(
      3,
      '/api/rankings/Solo_Bass',
      undefined,
    );

    await fetchWithPublication('/api/rankings/Solo_Bass');

    expect(fetchMock).toHaveBeenNthCalledWith(
      4,
      '/api/rankings/Solo_Bass',
      undefined,
    );
  });

  it('retries an in-flight resource against a newer cache epoch', async () => {
    setPublicationForTests(42, false);
    const fetchMock = global.fetch as ReturnType<typeof vi.fn>;
    fetchMock.mockResolvedValueOnce(jsonResponse({
      publicationId: 42,
      previousPublicationId: 41,
      publishedScrapeId: 1278,
      publishedAt: '2026-08-03T03:00:00Z',
      pinningEnabled: false,
    }));
    await ensurePublication(true, true);

    let resolveStaleRequest!: (response: Response) => void;
    const staleRequest = new Promise<Response>(resolve => {
      resolveStaleRequest = resolve;
    });
    fetchMock
      .mockReturnValueOnce(staleRequest)
      .mockResolvedValueOnce(jsonResponse({
        publicationId: 42,
        previousPublicationId: 41,
        publishedScrapeId: 1279,
        publishedAt: '2026-08-03T03:05:00Z',
        pinningEnabled: false,
      }))
      .mockResolvedValueOnce(jsonResponse({ fresh: true }))
      .mockResolvedValueOnce(jsonResponse({ cached: true }));

    const request = fetchWithPublication('/api/player/player-2');
    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2));
    await ensurePublication(true, true);
    resolveStaleRequest(jsonResponse({ stale: true }));
    await expect(request.then(response => response.json())).resolves.toEqual({
      fresh: true,
    });

    expect(fetchMock).toHaveBeenNthCalledWith(
      2,
      '/api/player/player-2',
      { cache: 'no-cache' },
    );
    expect(fetchMock).toHaveBeenNthCalledWith(
      4,
      '/api/player/player-2',
      { cache: 'no-cache' },
    );

    await fetchWithPublication('/api/player/player-2');
    expect(fetchMock).toHaveBeenNthCalledWith(
      5,
      '/api/player/player-2',
      undefined,
    );
  });
});
