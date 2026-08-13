import { lazy, useMemo, useState } from 'react';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import LazyModalBoundary from '../../../src/components/common/LazyModalBoundary';
import ConfirmAlert from '../../../src/components/modals/ConfirmAlert';
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

function LoadedOverlay({ onClose }: { onClose: () => void }) {
  return (
    <ConfirmAlert
      title="Deferred confirmation"
      message="Confirm the deferred action."
      onNo={onClose}
      onYes={onClose}
    />
  );
}

function LoadedModalWithConfirm({ visible, onClose }: { visible: boolean; onClose: () => void }) {
  const [confirmVisible, setConfirmVisible] = useState(false);
  return (
    <ModalShell visible={visible} title="Deferred Control" onClose={onClose}>
      <button type="button" data-testid="nested-confirm-opener" onClick={() => setConfirmVisible(true)}>
        Open confirmation
      </button>
      {confirmVisible && (
        <ConfirmAlert
          title="Nested confirmation"
          message="Keep focus inside the loaded modal."
          onNo={() => setConfirmVisible(false)}
          onYes={() => setConfirmVisible(false)}
        />
      )}
    </ModalShell>
  );
}

function Harness({
  loader,
  mobileEnterOffset,
  initialFocus,
}: {
  loader: () => Promise<{ default: typeof LoadedModal }>;
  mobileEnterOffset?: number | string;
  initialFocus?: 'first' | 'panel';
}) {
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
        mobileEnterOffset={mobileEnterOffset}
        initialFocus={initialFocus}
      >
        <LazyModal visible={visible} onClose={() => setVisible(false)} />
      </LazyModalBoundary>
    </>
  );
}

function OverlayHarness({
  loader,
}: {
  loader: () => Promise<{ default: typeof LoadedOverlay }>;
}) {
  const [visible, setVisible] = useState(false);
  const LazyOverlay = useMemo(() => lazy(loader), [loader]);
  return (
    <>
      <button type="button" data-testid="overlay-opener" onClick={() => setVisible(true)}>Open overlay</button>
      <LazyModalBoundary
        visible={visible}
        title="Deferred confirmation"
        boundaryName="deferred-overlay"
        onClose={() => setVisible(false)}
        initialFocus="panel"
      >
        {visible && <LazyOverlay onClose={() => setVisible(false)} />}
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

  it('restores the launcher when a cold custom overlay replaces the loading shell', async () => {
    const deferred = createDeferred<{ default: typeof LoadedOverlay }>();
    render(<OverlayHarness loader={() => deferred.promise} />);
    const opener = screen.getByTestId('overlay-opener');

    opener.focus();
    fireEvent.click(opener);
    expect(screen.getByTestId('deferred-overlay-lazy-loading')).toHaveFocus();

    await act(async () => deferred.resolve({ default: LoadedOverlay }));
    expect(await screen.findByRole('alertdialog', { name: 'Deferred confirmation' })).toBeVisible();

    fireEvent.click(screen.getByRole('button', { name: 'No' }));
    await waitFor(() => expect(screen.queryByRole('alertdialog')).toBeNull());
    expect(document.activeElement).toBe(opener);
  });

  it('restores an in-modal launcher when a nested overlay closes', async () => {
    const deferred = createDeferred<{ default: typeof LoadedModal }>();
    render(<Harness loader={() => deferred.promise} />);
    const opener = screen.getByTestId('opener');

    opener.focus();
    fireEvent.click(opener);
    await act(async () => deferred.resolve({ default: LoadedModalWithConfirm }));
    const nestedOpener = await screen.findByTestId('nested-confirm-opener');
    nestedOpener.focus();
    fireEvent.click(nestedOpener);
    expect(await screen.findByRole('alertdialog', { name: 'Nested confirmation' })).toBeVisible();

    fireEvent.click(screen.getByRole('button', { name: 'No' }));
    await waitFor(() => expect(screen.queryByRole('alertdialog')).toBeNull());
    expect(document.activeElement).toBe(nestedOpener);
    expect(screen.getByRole('dialog', { name: 'Deferred Control' })).toBeVisible();
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
