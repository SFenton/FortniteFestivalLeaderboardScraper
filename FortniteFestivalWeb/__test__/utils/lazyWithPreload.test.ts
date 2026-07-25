import { createElement, Suspense } from 'react';
import { render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { lazyWithPreload } from '../../src/utils/lazyWithPreload';

const originalOnLine = Object.getOwnPropertyDescriptor(navigator, 'onLine');

afterEach(() => {
  if (originalOnLine) {
    Object.defineProperty(navigator, 'onLine', originalOnLine);
  } else {
    Reflect.deleteProperty(navigator, 'onLine');
  }
});

describe('lazyWithPreload', () => {
  it('deduplicates repeated intent preloads', async () => {
    const loader = vi.fn(async () => ({ default: () => null }));
    const control = lazyWithPreload(loader);

    control.preload();
    control.preload();
    await Promise.resolve();

    expect(loader).toHaveBeenCalledOnce();
  });

  it('does not start optional intent prefetch while the browser is offline', () => {
    Object.defineProperty(navigator, 'onLine', { configurable: true, value: false });
    const loader = vi.fn(async () => ({ default: () => null }));

    lazyWithPreload(loader).preload();
    expect(loader).not.toHaveBeenCalled();
    expect(loader).not.toHaveBeenCalled();
  });

  it('contains rejected intent preloads so the modal boundary owns failure UI', async () => {
    const loader = vi.fn(() => Promise.reject(new Error('chunk unavailable')));
    const control = lazyWithPreload(loader);

    control.preload();
    await Promise.resolve();
    await Promise.resolve();

    expect(loader).toHaveBeenCalledOnce();
  });

  it('renders synchronously through React.lazy after the external load gate resolves', async () => {
    const control = lazyWithPreload(async () => ({
      default: () => createElement('span', null, 'Loaded synchronously'),
    }));
    await control.load();

    render(createElement(
      Suspense,
      { fallback: createElement('span', null, 'Suspended') },
      createElement(control.Component),
    ));

    expect(screen.getByText('Loaded synchronously')).toBeVisible();
    expect(screen.queryByText('Suspended')).toBeNull();
    expect(control.isLoaded()).toBe(true);
  });
});
