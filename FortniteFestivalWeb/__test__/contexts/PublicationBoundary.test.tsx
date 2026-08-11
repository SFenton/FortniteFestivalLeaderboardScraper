import { act, render, screen } from '@testing-library/react';
import { useEffect } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import PublicationBoundary from '../../src/contexts/PublicationBoundary';
import {
  PUBLICATION_CHANGED_EVENT,
  resetPublicationForTests,
} from '../../src/api/publication';

describe('PublicationBoundary', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    localStorage.clear();
    resetPublicationForTests();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('shows service unavailable while retrying a transient bootstrap failure', async () => {
    global.fetch = vi.fn()
      .mockRejectedValueOnce(new Error('temporary outage'))
      .mockResolvedValueOnce(new Response(JSON.stringify({
        contractVersion: 1,
        publicationId: 42,
        previousPublicationId: null,
        publishedScrapeId: 1271,
        publishedAt: '2026-07-30T19:35:02Z',
        readyForPinning: false,
        pinningEnabled: false,
        unreadySurfaces: [],
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
    expect(screen.getByText(/currently down for maintenance/i)).toBeInTheDocument();
    expect(screen.queryByText('Published app')).not.toBeInTheDocument();

    await act(async () => {
      vi.advanceTimersByTime(2_000);
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(screen.getByText('Published app')).toBeInTheDocument();
    expect(global.fetch).toHaveBeenCalledTimes(2);
  });

  it.each([
    [404, 'Not Found'],
    [502, 'Bad Gateway'],
  ])('renders maintenance mode when publication bootstrap returns %i', async (status, statusText) => {
    global.fetch = vi.fn().mockResolvedValue(
      new Response(null, {
        status,
        statusText,
      }),
    );

    render(
      <PublicationBoundary>
        <div>Published app</div>
      </PublicationBoundary>,
    );

    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(screen.getByText(/currently down for maintenance/i)).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.queryByText('Published app')).not.toBeInTheDocument();
  });

  it('remounts consumers for a same-publication refresh', async () => {
    const publication = {
      contractVersion: 1,
      publicationId: 42,
      previousPublicationId: 41,
      publishedScrapeId: 1271,
      publishedAt: '2026-07-30T19:35:02Z',
      readyForPinning: true,
      pinningEnabled: true,
      unreadySurfaces: [],
    };
    global.fetch = vi.fn().mockResolvedValue(
      new Response(JSON.stringify(publication), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    );
    let mounts = 0;
    let unmounts = 0;
    function Consumer() {
      useEffect(() => {
        mounts++;
        return () => { unmounts++; };
      }, []);
      return <div>Published app</div>;
    }

    render(
      <PublicationBoundary>
        <Consumer />
      </PublicationBoundary>,
    );
    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
    });
    expect(mounts).toBe(1);

    act(() => {
      window.dispatchEvent(new CustomEvent(
        PUBLICATION_CHANGED_EVENT,
        { detail: publication },
      ));
    });

    expect(mounts).toBe(2);
    expect(unmounts).toBe(1);
  });
});
