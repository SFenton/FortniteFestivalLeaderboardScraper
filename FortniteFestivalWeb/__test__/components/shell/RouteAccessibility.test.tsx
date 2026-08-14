import { act, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { NavigationType } from 'react-router-dom';
import {
  RouteAccessibility,
  RouteMain,
} from '../../../src/components/shell/RouteAccessibility';
import RouteAccessibilityRuntime from '../../../src/components/shell/RouteAccessibilityRuntime';

let queuedFrame: FrameRequestCallback | null = null;

describe('RouteAccessibility', () => {
  beforeEach(() => {
    queuedFrame = null;
    vi.stubGlobal('requestAnimationFrame', vi.fn((callback: FrameRequestCallback) => {
      queuedFrame = callback;
      return 1;
    }));
    vi.stubGlobal('cancelAnimationFrame', vi.fn());
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    document.title = '';
  });

  it('updates the title without announcing or focusing the initial route', () => {
    renderRouteAccessibility('songs', 'Songs', NavigationType.Pop);
    flushFrame();

    expect(document.title).toBe('Songs | Festival Score Tracker');
    expect(screen.queryByText('Navigated to Songs')).toBeNull();
    expect(document.getElementById('main-content')).not.toHaveFocus();
  });

  it('announces and focuses a distinct pushed route without scrolling', () => {
    const focus = vi.spyOn(HTMLElement.prototype, 'focus');
    const view = renderRouteAccessibility('songs', 'Songs', NavigationType.Pop);
    flushFrame();

    view.rerender(routeAccessibility('settings', 'Settings', NavigationType.Push));
    flushFrame();

    expect(screen.getByText('Navigated to Settings')).toBeInTheDocument();
    expect(document.getElementById('main-content')).toHaveFocus();
    expect(focus).toHaveBeenCalledWith({ preventScroll: true });
  });

  it('announces POP navigation without stealing focus', () => {
    const view = renderRouteAccessibility('songs', 'Songs', NavigationType.Pop);
    flushFrame();
    const launcher = screen.getByRole('button', { name: 'Persistent control' });
    launcher.focus();

    view.rerender(routeAccessibility('settings', 'Settings', NavigationType.Pop));
    flushFrame();

    expect(screen.getByText('Navigated to Settings')).toBeInTheDocument();
    expect(launcher).toHaveFocus();
  });

  it('does not refocus or reannounce same-route metadata updates', () => {
    const view = renderRouteAccessibility('statistics', 'Statistics', NavigationType.Pop);
    flushFrame();
    const launcher = screen.getByRole('button', { name: 'Persistent control' });
    launcher.focus();

    view.rerender(routeAccessibility('statistics', 'Player One', NavigationType.Replace));
    flushFrame();

    expect(document.title).toBe('Player One | Festival Score Tracker');
    expect(screen.queryByText('Navigated to Player One')).toBeNull();
    expect(launcher).toHaveFocus();
  });

  it('suppresses route focus while a modal owns focus', () => {
    const view = renderRouteAccessibility('songs', 'Songs', NavigationType.Pop);
    flushFrame();
    const dialog = document.createElement('div');
    dialog.setAttribute('aria-modal', 'true');
    document.body.appendChild(dialog);

    view.rerender(routeAccessibility('settings', 'Settings', NavigationType.Push));
    flushFrame();

    expect(document.getElementById('main-content')).not.toHaveFocus();
    dialog.remove();
  });

  it('keeps modal-delayed focus pending while the route title resolves', () => {
    const view = renderRouteAccessibility('songs', 'Songs', NavigationType.Pop);
    flushFrame();
    const dialog = document.createElement('div');
    dialog.setAttribute('aria-modal', 'true');
    document.body.appendChild(dialog);

    view.rerender(routeAccessibility('songs/song-1', 'Main content', NavigationType.Push));
    flushFrame();
    view.rerender(routeAccessibility('songs/song-1', 'Song Info', NavigationType.Push));
    dialog.remove();
    flushFrame();

    expect(document.getElementById('main-content')).toHaveFocus();
  });

  it('moves focus without replacing the HashRouter URL when the skip link is used', () => {
    window.location.hash = '#/suggestions';
    render(routeShellAccessibility('suggestions', 'Suggestions', NavigationType.Pop));

    fireEvent.click(screen.getByRole('link', { name: 'Skip to main content' }));

    expect(window.location.hash).toBe('#/suggestions');
    expect(document.getElementById('main-content')).toHaveFocus();
  });
});

describe('RouteMain', () => {
  it('renders a focusable main landmark and optional fallback H1', () => {
    const { rerender } = render(
      <RouteMain routeTitle="Songs" fallbackHeading style={{ padding: 4 }}>
        <p>Content</p>
      </RouteMain>,
    );
    const main = screen.getByRole('main', { name: 'Songs' });
    expect(main).toHaveAttribute('id', 'main-content');
    expect(main).toHaveAttribute('tabindex', '-1');
    expect(screen.getByRole('heading', { level: 1, name: 'Songs' })).toBeInTheDocument();

    rerender(
      <RouteMain routeTitle="Song" fallbackHeading={false}>
        <h1>Visible song title</h1>
      </RouteMain>,
    );
    expect(screen.getAllByRole('heading', { level: 1 })).toHaveLength(1);
  });
});

function renderRouteAccessibility(routeKey: string, title: string, navigationType: NavigationType) {
  return render(routeAccessibility(routeKey, title, navigationType));
}

function routeAccessibility(routeKey: string, title: string, navigationType: NavigationType) {
  return (
    <>
      <RouteAccessibilityRuntime
        pathname={`/${routeKey}`}
        titleOverride={title}
        navigationType={navigationType}
      />
      <RouteMain routeTitle={title} fallbackHeading={false}>
        <button type="button">Persistent control</button>
      </RouteMain>
    </>
  );
}

function routeShellAccessibility(routeKey: string, title: string, navigationType: NavigationType) {
  return (
    <>
      <RouteAccessibility
        pathname={`/${routeKey}`}
        titleOverride={title}
        navigationType={navigationType}
        skipLabel="Skip to main content"
      />
      <RouteMain routeTitle={title} fallbackHeading={false}>
        <button type="button">Persistent control</button>
      </RouteMain>
    </>
  );
}

function flushFrame(): void {
  const frame = queuedFrame;
  queuedFrame = null;
  act(() => {
    frame?.(0);
  });
}
