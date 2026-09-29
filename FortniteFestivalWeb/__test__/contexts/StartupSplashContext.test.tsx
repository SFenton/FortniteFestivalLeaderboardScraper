import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { defaultScheduler, notifyManager } from '@tanstack/query-core';
import { useState } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import BackendAvailabilityGate from '../../src/components/maintenance/BackendAvailabilityGate';
import PublicationBoundary from '../../src/contexts/PublicationBoundary';
import {
  StartupSplashProvider,
  useStartupSplashAnnouncement,
  useStartupSplashReveal,
  useStartupSplashSuppression,
} from '../../src/contexts/StartupSplashContext';
import { resetPublicationForTests } from '../../src/api/publication';
import type { InitialAppRevealPhase } from '../../src/hooks/ui/useInitialAppReveal';

function Announcer({ active }: { active: boolean }) {
  useStartupSplashAnnouncement(active);
  return null;
}

function FailureSurface() {
  useStartupSplashSuppression();
  return <p>Failure surface</p>;
}

function RevealProbe({
  phase,
  onExitComplete = () => {},
}: {
  phase: InitialAppRevealPhase;
  onExitComplete?: () => void;
}) {
  useStartupSplashReveal(phase, onExitComplete);
  return <p>App shell</p>;
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(res => {
    resolve = res;
  });
  return { promise, resolve };
}

describe('StartupSplashContext', () => {
  it('covers silently before any stage reports', () => {
    render(<StartupSplashProvider><p>Booting</p></StartupSplashProvider>);

    const splash = screen.getByTestId('startup-splash');
    expect(splash).toHaveAttribute('data-phase', 'covered');
    expect(splash).toHaveAttribute('aria-hidden', 'true');
    expect(screen.queryByRole('status')).not.toBeInTheDocument();
    expect(screen.getAllByTestId('arc-spinner')).toHaveLength(1);
  });

  it('keeps one splash node and spinner while announcing, silent, and app stages hand off', () => {
    const { rerender } = render(
      <StartupSplashProvider><Announcer active /></StartupSplashProvider>,
    );
    const splash = screen.getByTestId('startup-splash');
    const spinner = screen.getByTestId('arc-spinner');
    expect(splash).toHaveAttribute('role', 'status');
    expect(screen.getByTestId('startup-splash-status')).toHaveTextContent('Loading…');

    rerender(<StartupSplashProvider><Announcer active={false} /></StartupSplashProvider>);
    expect(splash).not.toHaveAttribute('role');
    expect(splash).toHaveAttribute('aria-hidden', 'true');

    rerender(<StartupSplashProvider><RevealProbe phase="waiting" /></StartupSplashProvider>);
    expect(splash).toHaveAttribute('data-phase', 'covered');

    rerender(<StartupSplashProvider><RevealProbe phase="revealing" /></StartupSplashProvider>);
    expect(splash).toHaveAttribute('data-phase', 'revealing');

    expect(screen.getByTestId('startup-splash')).toBe(splash);
    expect(screen.getByTestId('arc-spinner')).toBe(spinner);
  });

  it('hands its own opacity transition end to the app and leaves after entry', () => {
    const onExitComplete = vi.fn();
    const { rerender } = render(
      <StartupSplashProvider>
        <RevealProbe phase="revealing" onExitComplete={onExitComplete} />
      </StartupSplashProvider>,
    );
    const splash = screen.getByTestId('startup-splash');

    fireEvent.transitionEnd(screen.getByTestId('arc-spinner'), { propertyName: 'opacity' });
    fireEvent.transitionEnd(splash, { propertyName: 'transform' });
    expect(onExitComplete).not.toHaveBeenCalled();

    fireEvent.transitionEnd(splash, { propertyName: 'opacity' });
    expect(onExitComplete).toHaveBeenCalledTimes(1);

    rerender(
      <StartupSplashProvider>
        <RevealProbe phase="entered" onExitComplete={onExitComplete} />
      </StartupSplashProvider>,
    );
    expect(screen.queryByTestId('startup-splash')).not.toBeInTheDocument();
  });

  it('restores the covered splash and forgets the exit callback when the app unmounts', () => {
    const onExitComplete = vi.fn();
    const { rerender } = render(
      <StartupSplashProvider>
        <RevealProbe phase="entered" onExitComplete={onExitComplete} />
      </StartupSplashProvider>,
    );
    expect(screen.queryByTestId('startup-splash')).not.toBeInTheDocument();

    rerender(<StartupSplashProvider>{null}</StartupSplashProvider>);

    const splash = screen.getByTestId('startup-splash');
    expect(splash).toHaveAttribute('data-phase', 'covered');
    fireEvent.transitionEnd(splash, { propertyName: 'opacity' });
    expect(onExitComplete).not.toHaveBeenCalled();
  });

  it('removes the splash while a failure surface is mounted and restores it afterward', () => {
    const { rerender } = render(
      <StartupSplashProvider><FailureSurface /></StartupSplashProvider>,
    );
    expect(screen.getByText('Failure surface')).toBeInTheDocument();
    expect(screen.queryByTestId('startup-splash')).not.toBeInTheDocument();

    rerender(<StartupSplashProvider>{null}</StartupSplashProvider>);
    expect(screen.getByTestId('startup-splash')).toHaveAttribute('data-phase', 'covered');
  });

  it('treats stage hooks outside a provider as no-ops', () => {
    render(
      <>
        <Announcer active />
        <FailureSurface />
        <RevealProbe phase="revealing" />
      </>,
    );

    expect(screen.queryByTestId('startup-splash')).not.toBeInTheDocument();
    expect(screen.getByText('Failure surface')).toBeInTheDocument();
    expect(screen.getByText('App shell')).toBeInTheDocument();
  });
});

describe('StartupSplashProvider bootstrap handoff', () => {
  beforeEach(() => {
    localStorage.clear();
    resetPublicationForTests();
    notifyManager.setScheduler(callback => callback());
  });

  afterEach(() => {
    notifyManager.setScheduler(defaultScheduler);
    vi.unstubAllGlobals();
    resetPublicationForTests();
  });

  it('keeps the same splash and spinner nodes from publication through availability to app reveal', async () => {
    const publication = deferred<Response>();
    const serviceInfo = deferred<Response>();
    vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/api/publication')) return publication.promise;
      if (url.includes('/api/service-info')) return serviceInfo.promise;
      return Promise.reject(new Error(`Unexpected request ${url}`));
    }));
    let setAppPhase!: (phase: InitialAppRevealPhase) => void;
    const onExitComplete = vi.fn();
    function AppProbe() {
      const [phase, setPhase] = useState<InitialAppRevealPhase>('waiting');
      setAppPhase = setPhase;
      return <RevealProbe phase={phase} onExitComplete={onExitComplete} />;
    }
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <StartupSplashProvider>
          <PublicationBoundary>
            <BackendAvailabilityGate>
              <AppProbe />
            </BackendAvailabilityGate>
          </PublicationBoundary>
        </StartupSplashProvider>
      </QueryClientProvider>,
    );

    const splash = screen.getByTestId('startup-splash');
    const spinner = screen.getByTestId('arc-spinner');
    expect(splash).toHaveAttribute('role', 'status');

    await act(async () => {
      publication.resolve(new Response(JSON.stringify({
        contractVersion: 1,
        publicationId: 42,
        previousPublicationId: null,
        publishedScrapeId: 1271,
        publishedAt: '2026-07-30T19:35:02Z',
        readyForPinning: false,
        pinningEnabled: false,
        unreadySurfaces: [],
      }), { status: 200, headers: { 'Content-Type': 'application/json' } }));
    });
    await waitFor(() => expect(splash).not.toHaveAttribute('role'));
    expect(splash).toHaveAttribute('aria-hidden', 'true');
    expect(screen.queryByText('App shell')).not.toBeInTheDocument();

    await act(async () => {
      serviceInfo.resolve(new Response(JSON.stringify({
        currentUpdate: { status: 'idle' },
        workerStatus: { status: 'online' },
      }), { status: 200, headers: { 'Content-Type': 'application/json' } }));
    });
    await waitFor(() => expect(screen.getByText('App shell')).toBeInTheDocument());
    expect(splash).toHaveAttribute('data-phase', 'covered');

    act(() => setAppPhase('revealing'));
    expect(splash).toHaveAttribute('data-phase', 'revealing');

    expect(screen.getAllByTestId('startup-splash')).toEqual([splash]);
    expect(screen.getAllByTestId('arc-spinner')).toEqual([spinner]);

    fireEvent.transitionEnd(splash, { propertyName: 'opacity' });
    expect(onExitComplete).toHaveBeenCalledTimes(1);
    act(() => setAppPhase('entered'));
    expect(screen.queryByTestId('startup-splash')).not.toBeInTheDocument();
  });
});
