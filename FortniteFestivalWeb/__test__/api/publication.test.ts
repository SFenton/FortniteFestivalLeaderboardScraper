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
      .mockResolvedValueOnce(jsonResponse({ ok: true }));

    const response = await fetchWithPublication('/api/songs');

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
      undefined,
    );
  });
});
