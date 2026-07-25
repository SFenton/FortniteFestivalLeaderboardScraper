import { lazy, useMemo, useState } from 'react';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import LazyModalBoundary from '../../../src/components/common/LazyModalBoundary';
import ModalShell from '../../../src/components/modals/components/ModalShell';

type Deferred<T> = {
  promise: Promise<T>;
  resolve: (value: T) => void;
  reject: (reason: unknown) => void;
};

function createDeferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}

function LoadedModal({ visible, onClose }: { visible: boolean; onClose: () => void }) {
  return (
    <ModalShell visible={visible} title="Deferred Control" onClose={onClose}>
      <div>Loaded control</div>
    </ModalShell>
  );
}

function Harness({ loader }: { loader: () => Promise<{ default: typeof LoadedModal }> }) {
  const [visible, setVisible] = useState(false);
  const LazyModal = useMemo(() => lazy(loader), [loader]);
  return (
    <>
      <button type="button" data-testid="opener" onClick={() => setVisible(true)}>Open</button>
      <LazyModalBoundary
        visible={visible}
        title="Deferred Control"
        boundaryName="deferred-control"
        onClose={() => setVisible(false)}
      >
        <LazyModal visible={visible} onClose={() => setVisible(false)} />
      </LazyModalBoundary>
    </>
  );
}

describe('LazyModalBoundary', () => {
  beforeEach(() => {
    vi.spyOn(window, 'requestAnimationFrame').mockImplementation((callback) => {
      callback(0);
      return 1;
    });
    vi.spyOn(window, 'cancelAnimationFrame').mockImplementation(() => {});
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('shows an accessible first-open loading dialog and loads only after activation', async () => {
    const deferred = createDeferred<{ default: typeof LoadedModal }>();
    const loader = vi.fn(() => deferred.promise);
    render(<Harness loader={loader} />);

    expect(loader).not.toHaveBeenCalled();
    const opener = screen.getByTestId('opener');
    opener.focus();
    fireEvent.click(opener);

    expect(loader).toHaveBeenCalledOnce();
    expect(screen.getByTestId('deferred-control-lazy-loading')).toHaveAttribute('role', 'dialog');
    expect(screen.getByRole('status')).toHaveTextContent('Loading');

    await act(async () => deferred.resolve({ default: LoadedModal }));
    expect(await screen.findByText('Loaded control')).toBeVisible();
  });

  it('preserves close, focus restoration, and immediate reopen after the chunk loads', async () => {
    const loader = vi.fn(async () => ({ default: LoadedModal }));
    render(<Harness loader={loader} />);
    const opener = screen.getByTestId('opener');

    opener.focus();
    fireEvent.click(opener);
    expect(await screen.findByText('Loaded control')).toBeVisible();

    fireEvent.click(screen.getByRole('button', { name: 'Close' }));
    await waitFor(() => expect(screen.queryByText('Loaded control')).toBeNull());
    expect(document.activeElement).toBe(opener);

    fireEvent.click(opener);
    expect(await screen.findByText('Loaded control')).toBeVisible();
    expect(loader).toHaveBeenCalledOnce();
  });

  it('fails closed with reload and close actions when a lazy chunk rejects', async () => {
    const consoleError = vi.spyOn(console, 'error').mockImplementation(() => {});
    const loader = vi.fn(() => Promise.reject(new Error('offline chunk failure')));
    render(<Harness loader={loader} />);
    const opener = screen.getByTestId('opener');

    opener.focus();
    fireEvent.click(opener);

    expect(await screen.findByTestId('deferred-control-lazy-error')).toHaveAttribute('role', 'dialog');
    expect(screen.getByRole('alert')).toHaveTextContent('could not be loaded');
    expect(screen.getByRole('button', { name: 'Reload' })).toBeVisible();

    fireEvent.click(screen.getByRole('button', { name: 'Close' }));
    await waitFor(() => expect(screen.queryByTestId('deferred-control-lazy-error')).toBeNull());
    expect(document.activeElement).toBe(opener);
    consoleError.mockRestore();
  });
});
