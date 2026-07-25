import { act, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { defaultScheduler, notifyManager } from '@tanstack/query-core';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import BackendAvailabilityGate from '../../../src/components/maintenance/BackendAvailabilityGate';
import { queryKeys } from '../../../src/api/queryKeys';

beforeEach(() => {
  vi.stubGlobal('fetch', vi.fn());
  notifyManager.setScheduler(callback => callback());
});

afterEach(() => {
  notifyManager.setScheduler(defaultScheduler);
  vi.useRealTimers();
  vi.restoreAllMocks();
});

function createClient() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });
}

function renderGate(queryClient = createClient()) {
  return {
    queryClient,
    ...render(
      <QueryClientProvider client={queryClient}>
        <BackendAvailabilityGate>
          <div>App content</div>
        </BackendAvailabilityGate>
      </QueryClientProvider>,
    ),
  };
}

function mockServiceInfo(workerStatus: string, currentUpdateStatus = 'idle') {
  (fetch as ReturnType<typeof vi.fn>).mockResolvedValue({
    ok: true,
    status: 200,
    statusText: 'OK',
    json: () => Promise.resolve({
      currentUpdate: { status: currentUpdateStatus },
      workerStatus: { status: workerStatus },
    }),
  });
}

describe('BackendAvailabilityGate', () => {
  it('shows a status check message while the backend check is pending', () => {
    (fetch as ReturnType<typeof vi.fn>).mockReturnValue(new Promise(() => {}));

    renderGate();

    expect(screen.getByText('Checking Festival Score Tracker status...')).toBeInTheDocument();
    expect(screen.queryByText('App content')).not.toBeInTheDocument();
  });

  it.each([
    ['online', 'idle'],
    ['offline', 'failed'],
    ['stale', 'updating'],
  ])('renders the app for a valid response with worker %s and update %s', async (workerStatus, updateStatus) => {
    mockServiceInfo(workerStatus, updateStatus);

    renderGate();

    await waitFor(() => expect(screen.getByText('App content')).toBeInTheDocument());
    expect(screen.queryByText('Festival Score Tracker Status')).not.toBeInTheDocument();
  });

  it('renders maintenance mode when service-info returns a non-success status', async () => {
    (fetch as ReturnType<typeof vi.fn>).mockResolvedValue({
      ok: false,
      status: 503,
      statusText: 'Service Unavailable',
    });

    renderGate();

    await waitFor(() => expect(screen.getByText(/currently down for maintenance/i)).toBeInTheDocument());
    expect(screen.queryByText('App content')).not.toBeInTheDocument();
  });

  it('renders maintenance mode when service-info rejects', async () => {
    (fetch as ReturnType<typeof vi.fn>).mockRejectedValue(new Error('offline'));

    renderGate();

    await waitFor(() => expect(screen.getByText(/currently down for maintenance/i)).toBeInTheDocument());
    expect(screen.queryByText('App content')).not.toBeInTheDocument();
  });

  it('renders maintenance mode when service-info JSON is malformed', async () => {
    (fetch as ReturnType<typeof vi.fn>).mockResolvedValue({
      ok: true,
      status: 200,
      statusText: 'OK',
      json: () => Promise.reject(new SyntaxError('Unexpected token')),
    });

    renderGate();

    await waitFor(() => expect(screen.getByText(/currently down for maintenance/i)).toBeInTheDocument());
    expect(screen.queryByText('App content')).not.toBeInTheDocument();
  });

  it('times out a slow backend check after three seconds', async () => {
    vi.useFakeTimers();
    (fetch as ReturnType<typeof vi.fn>).mockImplementation((_url: string, init: RequestInit) => new Promise((_resolve, reject) => {
      init.signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')));
    }));

    renderGate();

    await act(async () => {
      await vi.advanceTimersByTimeAsync(3_000);
      await vi.advanceTimersByTimeAsync(0);
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(screen.getByText(/currently down for maintenance/i)).toBeInTheDocument();
    expect(screen.queryByText('App content')).not.toBeInTheDocument();
  });

  it('recovers on the explicit unavailable retry interval', async () => {
    vi.useFakeTimers();
    (fetch as ReturnType<typeof vi.fn>)
      .mockRejectedValueOnce(new Error('offline'))
      .mockResolvedValue({
        ok: true,
        status: 200,
        statusText: 'OK',
        json: () => Promise.resolve({ currentUpdate: { status: 'failed' }, workerStatus: { status: 'offline' } }),
      });

    renderGate();
    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
      await vi.advanceTimersByTimeAsync(0);
    });
    expect(screen.getByText(/currently down for maintenance/i)).toBeInTheDocument();
    expect(fetch).toHaveBeenCalledTimes(1);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(4_999);
    });
    expect(fetch).toHaveBeenCalledTimes(1);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(1);
      await vi.advanceTimersByTimeAsync(0);
    });
    expect(fetch).toHaveBeenCalledTimes(2);
    expect(screen.getByText('App content')).toBeInTheDocument();
  });

  it('does not let cached success mask a failed availability refetch', async () => {
    const queryClient = createClient();
    queryClient.setQueryData(
      queryKeys.serviceInfo(),
      { currentUpdate: { status: 'idle' }, workerStatus: { status: 'online' } },
      { updatedAt: 0 },
    );
    (fetch as ReturnType<typeof vi.fn>).mockRejectedValue(new Error('offline'));

    renderGate(queryClient);
    expect(screen.getByText('App content')).toBeInTheDocument();

    await waitFor(() => expect(screen.getByText(/currently down for maintenance/i)).toBeInTheDocument());
    expect(fetch).toHaveBeenCalledTimes(1);
    expect(screen.queryByText('App content')).not.toBeInTheDocument();
  });
});
