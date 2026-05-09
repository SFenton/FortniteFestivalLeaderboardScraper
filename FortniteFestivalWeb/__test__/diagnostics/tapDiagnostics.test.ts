import { afterEach, describe, expect, it, vi } from 'vitest';
import { createTapDiagnostics } from '../../src/diagnostics/tapDiagnostics';

describe('tapDiagnostics', () => {
  afterEach(() => {
    delete window.__fstTapDiagnostics;
    document.body.innerHTML = '';
    vi.restoreAllMocks();
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