import { act, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import PublicationBoundary from '../../src/contexts/PublicationBoundary';
import { resetPublicationForTests } from '../../src/api/publication';

describe('PublicationBoundary', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    localStorage.clear();
    resetPublicationForTests();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('retries a transient bootstrap failure', async () => {
    global.fetch = vi.fn()
      .mockRejectedValueOnce(new Error('temporary outage'))
      .mockResolvedValueOnce(new Response(JSON.stringify({
        publicationId: 42,
        previousPublicationId: null,
        publishedScrapeId: 1271,
        publishedAt: '2026-07-30T19:35:02Z',
        pinningEnabled: false,
      }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }));

    render(
      <PublicationBoundary>
        <div>Published app</div>
      </PublicationBoundary>,
    );

    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
    });
    expect(screen.getByRole('alert')).toHaveTextContent('retrying');

    await act(async () => {
      vi.advanceTimersByTime(2_000);
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(screen.getByText('Published app')).toBeInTheDocument();
    expect(global.fetch).toHaveBeenCalledTimes(2);
  });
});
