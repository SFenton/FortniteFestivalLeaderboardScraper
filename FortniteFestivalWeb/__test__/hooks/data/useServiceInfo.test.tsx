import { act, render, renderHook, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { defaultScheduler, notifyManager } from '@tanstack/query-core';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const mockApi = vi.hoisted(() => ({
  getServiceInfo: vi.fn(),
}));

vi.mock('../../../src/api/client', () => ({ api: mockApi }));

const {
  SERVICE_INFO_AVAILABILITY_POLL_MS,
  SERVICE_INFO_SETTINGS_POLL_MS,
  SERVICE_INFO_TIMEOUT_MS,
  useServiceInfo,
} = await import('../../../src/hooks/data/useServiceInfo');

const serviceInfo = {
  currentUpdate: { status: 'failed' },
  workerStatus: { status: 'offline' },
};

function createClient() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });
}

function makeWrapper(queryClient: QueryClient) {
  return function Wrapper({ children }: { children: ReactNode }) {
    return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
  };
}

async function flushPromises() {
  await act(async () => {
    await Promise.resolve();
    await Promise.resolve();
  });
}

describe('useServiceInfo', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockApi.getServiceInfo.mockResolvedValue(serviceInfo);
    notifyManager.setScheduler(callback => callback());
  });

  afterEach(() => {
    notifyManager.setScheduler(defaultScheduler);
    vi.useRealTimers();
  });

  it('deduplicates availability and Settings consumers onto one request', async () => {
    let resolveRequest!: (value: typeof serviceInfo) => void;
    mockApi.getServiceInfo.mockReturnValue(new Promise(resolve => {
      resolveRequest = resolve;
    }));
    const queryClient = createClient();

    function Consumers() {
      const availability = useServiceInfo('availability');
      const settings = useServiceInfo('settings');
      return <div>{availability.status}:{settings.status}</div>;
    }

    render(
      <QueryClientProvider client={queryClient}>
        <Consumers />
      </QueryClientProvider>,
    );

    expect(mockApi.getServiceInfo).toHaveBeenCalledTimes(1);
    expect(mockApi.getServiceInfo).toHaveBeenCalledWith(expect.any(AbortSignal));

    resolveRequest(serviceInfo);
    await waitFor(() => expect(screen.getByText('success:success')).toBeInTheDocument());
    expect(mockApi.getServiceInfo).toHaveBeenCalledTimes(1);
  });

  it('polls Settings every five seconds without overlapping an in-flight request', async () => {
    vi.useFakeTimers();
    let resolveSecond!: (value: typeof serviceInfo) => void;
    mockApi.getServiceInfo
      .mockResolvedValueOnce(serviceInfo)
      .mockReturnValueOnce(new Promise(resolve => {
        resolveSecond = resolve;
      }));
    const queryClient = createClient();

    renderHook(() => useServiceInfo('settings'), { wrapper: makeWrapper(queryClient) });
    await flushPromises();
    expect(mockApi.getServiceInfo).toHaveBeenCalledTimes(1);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(SERVICE_INFO_SETTINGS_POLL_MS);
    });
    expect(mockApi.getServiceInfo).toHaveBeenCalledTimes(2);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(2_000);
    });
    expect(mockApi.getServiceInfo).toHaveBeenCalledTimes(2);

    resolveSecond(serviceInfo);
    await flushPromises();
  });

  it('keeps healthy availability polling at thirty seconds', async () => {
    vi.useFakeTimers();
    const queryClient = createClient();

    renderHook(() => useServiceInfo('availability'), { wrapper: makeWrapper(queryClient) });
    await flushPromises();
    expect(mockApi.getServiceInfo).toHaveBeenCalledTimes(1);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(SERVICE_INFO_AVAILABILITY_POLL_MS - 1);
    });
    expect(mockApi.getServiceInfo).toHaveBeenCalledTimes(1);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(1);
    });
    expect(mockApi.getServiceInfo).toHaveBeenCalledTimes(2);
  });

  it('uses one explicit five-second retry after failure with no hidden retries', async () => {
    vi.useFakeTimers();
    mockApi.getServiceInfo
      .mockRejectedValueOnce(new Error('offline'))
      .mockResolvedValueOnce(serviceInfo);
    const queryClient = createClient();

    renderHook(() => useServiceInfo('availability'), { wrapper: makeWrapper(queryClient) });
    await flushPromises();
    expect(mockApi.getServiceInfo).toHaveBeenCalledTimes(1);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(4_999);
    });
    expect(mockApi.getServiceInfo).toHaveBeenCalledTimes(1);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(1);
    });
    expect(mockApi.getServiceInfo).toHaveBeenCalledTimes(2);
  });

  it('aborts a service-info request at the explicit timeout', async () => {
    vi.useFakeTimers();
    let capturedSignal: AbortSignal | undefined;
    mockApi.getServiceInfo.mockImplementation((signal?: AbortSignal) => {
      capturedSignal = signal;
      return new Promise((_resolve, reject) => {
        signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')));
      });
    });
    const queryClient = createClient();
    const { result } = renderHook(() => useServiceInfo('availability'), { wrapper: makeWrapper(queryClient) });

    expect(capturedSignal).toBeInstanceOf(AbortSignal);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(SERVICE_INFO_TIMEOUT_MS);
      await vi.advanceTimersByTimeAsync(0);
    });

    expect(capturedSignal?.aborted).toBe(true);
    expect(result.current.isError).toBe(true);
    expect(mockApi.getServiceInfo).toHaveBeenCalledTimes(1);
  });
});
