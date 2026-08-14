import { act, renderHook } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { useVisualPreferences } from '../../../src/hooks/ui/useVisualPreferences';

class MockConnection extends EventTarget {
  saveData = false;
}

describe('useVisualPreferences', () => {
  afterEach(() => {
    vi.restoreAllMocks();
    Object.defineProperty(document, 'hidden', {
      configurable: true,
      value: false,
    });
    Reflect.deleteProperty(navigator, 'connection');
  });

  it('tracks Save-Data changes and removes its subscription', () => {
    const connection = new MockConnection();
    const addEventListener = vi.spyOn(connection, 'addEventListener');
    const removeEventListener = vi.spyOn(connection, 'removeEventListener');
    Object.defineProperty(navigator, 'connection', {
      configurable: true,
      value: connection,
    });

    const { result, unmount } = renderHook(() => useVisualPreferences());
    expect(result.current.saveData).toBe(false);
    expect(addEventListener).toHaveBeenCalledWith('change', expect.any(Function));

    act(() => {
      connection.saveData = true;
      connection.dispatchEvent(new Event('change'));
    });
    expect(result.current.saveData).toBe(true);

    unmount();
    expect(removeEventListener).toHaveBeenCalledWith('change', expect.any(Function));
  });

  it('tracks document visibility', () => {
    const { result } = renderHook(() => useVisualPreferences());
    expect(result.current.isDocumentVisible).toBe(true);

    act(() => {
      Object.defineProperty(document, 'hidden', {
        configurable: true,
        value: true,
      });
      document.dispatchEvent(new Event('visibilitychange'));
    });
    expect(result.current.isDocumentVisible).toBe(false);
  });
});
