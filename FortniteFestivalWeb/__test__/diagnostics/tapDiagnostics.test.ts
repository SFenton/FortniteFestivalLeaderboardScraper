import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, render, waitFor } from '@testing-library/react';
import { createElement } from 'react';
import { createTapDiagnostics, isTapDiagnosticsEnabled, isTapTelemetryEnabled, setTapDiagnosticsPreference } from '../../src/diagnostics/tapDiagnostics';
import { useTapDiagnostics } from '../../src/diagnostics/useTapDiagnostics';

describe('tapDiagnostics', () => {
  afterEach(() => {
    delete window.__fstTapDiagnostics;
    delete window.__fstInteractionTelemetry;
    window.localStorage.clear();
    document.body.innerHTML = '';
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('can be enabled persistently for installed PWA sessions through localStorage', () => {
    window.localStorage.setItem('fst.tapDiagnostics', '1');
    window.localStorage.setItem('fst.tapTelemetry', '1');

    expect(isTapDiagnosticsEnabled()).toBe(true);
    expect(isTapTelemetryEnabled()).toBe(true);

    const diagnostics = createTapDiagnostics(() => ({ pathname: '/songs' }), {
      telemetry: { enabled: isTapTelemetryEnabled(), fetchRef: vi.fn() as unknown as typeof fetch },
    });

    expect(diagnostics).not.toBeNull();
    expect(window.__fstTapDiagnostics).toBeTruthy();

    diagnostics!.dispose();
  });

  it('reinstalls diagnostics when Settings toggles change preferences', async () => {
    function Harness() {
      useTapDiagnostics({ pathname: '/settings' });
      return null;
    }

    const { unmount } = render(createElement(Harness));
    expect(window.__fstTapDiagnostics).toBeUndefined();

    act(() => { setTapDiagnosticsPreference('diagnostics', true); });
    await waitFor(() => expect(window.__fstTapDiagnostics).toBeTruthy());

    act(() => { setTapDiagnosticsPreference('diagnostics', false); });
    await waitFor(() => expect(window.__fstTapDiagnostics).toBeUndefined());

    unmount();
  });

  it('records captured tap events with target, hit target, path, and app state', () => {
    const button = document.createElement('button');
    button.dataset.testid = 'tap-target';
    button.setAttribute('aria-label', 'Tap Target');
    button.textContent = 'Tap Target';
    document.body.appendChild(button);
    Object.defineProperty(document, 'elementFromPoint', { configurable: true, value: vi.fn(() => button) });

    const diagnostics = createTapDiagnostics(() => ({ pathname: '/songs', activeTab: 'songs' }), { force: true });
    expect(diagnostics).not.toBeNull();

    button.dispatchEvent(new MouseEvent('pointerdown', { bubbles: true, composed: true, clientX: 12, clientY: 34 }));
    button.dispatchEvent(new MouseEvent('click', { bubbles: true, composed: true, clientX: 12, clientY: 34 }));

    const records = window.__fstTapDiagnostics!.getRecords();
    expect(window.__fstInteractionTelemetry).toBe(window.__fstTapDiagnostics);
    expect(records.filter(record => record.kind === 'event').map(record => record.eventType)).toEqual(['pointerdown', 'click']);
    const click = records[1]!;
    expect(click.kind).toBe('event');
    if (click.kind === 'event') {
      expect(click.target?.testId).toBe('tap-target');
      expect(click.hitTarget?.testId).toBe('tap-target');
      expect(click.path.some(item => item.testId === 'tap-target')).toBe(true);
      expect(click.state).toEqual({ pathname: '/songs', activeTab: 'songs' });
    }

    diagnostics!.dispose();
  });

  it('forwards sanitized telemetry batches when service telemetry is enabled', async () => {
    vi.useFakeTimers();
    const button = document.createElement('button');
    button.dataset.testid = 'tap-target';
    button.setAttribute('aria-label', 'Player Name Secret');
    button.textContent = 'Player Name Secret';
    document.body.appendChild(button);
    Object.defineProperty(document, 'elementFromPoint', { configurable: true, value: vi.fn(() => button) });
    const fetchSpy = vi.fn().mockResolvedValue({ ok: true });

    const diagnostics = createTapDiagnostics(() => ({
      pathname: '/songs',
      search: '?name=Secret',
      player: { accountId: 'acct-secret', displayName: 'Secret' },
      selectedProfile: { type: 'player', displayName: 'Secret' },
      fabReady: { songs: true, label: 'Sort' },
    }), {
      force: true,
      telemetry: {
        enabled: true,
        endpoint: '/api/debug/client-interactions',
        sessionId: 'test-session',
        flushDelayMs: 10,
        fetchRef: fetchSpy as unknown as typeof fetch,
      },
    });

    button.dispatchEvent(new MouseEvent('click', { bubbles: true, composed: true, clientX: 12, clientY: 34 }));
    vi.advanceTimersByTime(10);
    await Promise.resolve();
    await Promise.resolve();

    expect(fetchSpy).toHaveBeenCalledTimes(1);
    const [, init] = fetchSpy.mock.calls[0]!;
    const payload = JSON.parse(String((init as RequestInit).body));
    expect(payload.sessionId).toBe('test-session');
    expect(payload.route).toBe('/songs');
    expect(payload.events).toHaveLength(1);
    expect(payload.events[0].target.testId).toBe('tap-target');
    expect(payload.events[0].target.text).toBeUndefined();
    expect(payload.events[0].target.ariaLabel).toBeUndefined();
    expect(payload.events[0].state.search).toBeUndefined();
    expect(payload.events[0].state.hasPlayer).toBe(true);
    expect(payload.events[0].state.selectedProfile).toEqual({ type: 'player' });
    expect(payload.events[0].state.fabReady).toEqual({ songs: true, label: 'Sort' });

    diagnostics!.dispose();
  });

  it('records action markers and clears records on reset', () => {
    const diagnostics = createTapDiagnostics(() => ({ pathname: '/settings' }), { force: true });
    expect(diagnostics).not.toBeNull();

    window.__fstTapDiagnostics!.markAction('open settings', 'start', { expected: '/settings' });
    expect(window.__fstTapDiagnostics!.dump().records).toHaveLength(1);

    window.__fstTapDiagnostics!.reset();
    expect(window.__fstTapDiagnostics!.dump().records).toHaveLength(0);

    diagnostics!.dispose();
  });
});