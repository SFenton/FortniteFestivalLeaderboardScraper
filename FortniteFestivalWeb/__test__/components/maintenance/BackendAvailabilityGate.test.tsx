import { act, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import BackendAvailabilityGate from '../../../src/components/maintenance/BackendAvailabilityGate';

beforeEach(() => {
  vi.stubGlobal('fetch', vi.fn());
});

afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
});

function mockServiceInfo(status: string) {
  (fetch as ReturnType<typeof vi.fn>).mockResolvedValue({
    ok: true,
    json: () => Promise.resolve({ workerStatus: { status } }),
  });
}

describe('BackendAvailabilityGate', () => {
  it('shows a status check message while the backend check is pending', () => {
    (fetch as ReturnType<typeof vi.fn>).mockReturnValue(new Promise(() => {}));

    render(
      <BackendAvailabilityGate>
        <div>App content</div>
      </BackendAvailabilityGate>,
    );

    expect(screen.getByText('Checking Festival Score Tracker status...')).toBeInTheDocument();
    expect(screen.queryByText('App content')).not.toBeInTheDocument();
  });

  it('renders the app when service and worker are available', async () => {
    mockServiceInfo('online');

    render(
      <BackendAvailabilityGate>
        <div>App content</div>
      </BackendAvailabilityGate>,
    );

    await waitFor(() => expect(screen.getByText('App content')).toBeInTheDocument());
    expect(screen.queryByText('Festival Score Tracker Status')).not.toBeInTheDocument();
  });

  it('renders maintenance mode when service-info fails', async () => {
    (fetch as ReturnType<typeof vi.fn>).mockResolvedValue({ ok: false, status: 503 });

    render(
      <BackendAvailabilityGate>
        <div>App content</div>
      </BackendAvailabilityGate>,
    );

    await waitFor(() => expect(screen.getByText('Festival Score Tracker Status')).toBeInTheDocument());
    expect(screen.getByText('Festival Score Tracker Status')).toBeInTheDocument();
    expect(screen.getByText(/currently down for maintenance/i)).toBeInTheDocument();
    expect(screen.queryByText('App content')).not.toBeInTheDocument();
  });

  it('renders maintenance mode when service-info rejects', async () => {
    (fetch as ReturnType<typeof vi.fn>).mockRejectedValue(new Error('offline'));

    render(
      <BackendAvailabilityGate>
        <div>App content</div>
      </BackendAvailabilityGate>,
    );

    await waitFor(() => expect(screen.getByText(/currently down for maintenance/i)).toBeInTheDocument());
    expect(screen.queryByText('App content')).not.toBeInTheDocument();
  });

  it('renders maintenance mode when worker status is missing', async () => {
    (fetch as ReturnType<typeof vi.fn>).mockResolvedValue({
      ok: true,
      json: () => Promise.resolve({}),
    });

    render(
      <BackendAvailabilityGate>
        <div>App content</div>
      </BackendAvailabilityGate>,
    );

    await waitFor(() => expect(screen.getByText(/currently down for maintenance/i)).toBeInTheDocument());
    expect(screen.queryByText('App content')).not.toBeInTheDocument();
  });

  it('renders maintenance mode when worker status is unavailable', async () => {
    mockServiceInfo('stale');

    render(
      <BackendAvailabilityGate>
        <div>App content</div>
      </BackendAvailabilityGate>,
    );

    await waitFor(() => expect(screen.getByText(/currently down for maintenance/i)).toBeInTheDocument());
    expect(screen.queryByText('App content')).not.toBeInTheDocument();
  });

  it('ignores a backend result that resolves after unmount', async () => {
    let resolveFetch!: (response: { ok: boolean; json: () => Promise<unknown> }) => void;
    (fetch as ReturnType<typeof vi.fn>).mockReturnValue(new Promise(resolve => {
      resolveFetch = resolve;
    }));

    const { unmount } = render(
      <BackendAvailabilityGate>
        <div>App content</div>
      </BackendAvailabilityGate>,
    );

    unmount();

    await act(async () => {
      resolveFetch({
        ok: true,
        json: () => Promise.resolve({ workerStatus: { status: 'online' } }),
      });
      await Promise.resolve();
    });

    expect(fetch).toHaveBeenCalledWith('/api/service-info', expect.objectContaining({
      signal: expect.any(AbortSignal),
    }));
  });

  it('times out a slow backend check and renders maintenance mode', async () => {
    vi.useFakeTimers();
    (fetch as ReturnType<typeof vi.fn>).mockImplementation((_url: string, init: RequestInit) => new Promise((_resolve, reject) => {
      init.signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')));
    }));

    render(
      <BackendAvailabilityGate>
        <div>App content</div>
      </BackendAvailabilityGate>,
    );

    await act(async () => {
      await vi.advanceTimersByTimeAsync(3_000);
    });

    expect(screen.getByText(/currently down for maintenance/i)).toBeInTheDocument();
    expect(screen.queryByText('App content')).not.toBeInTheDocument();
  });
});
