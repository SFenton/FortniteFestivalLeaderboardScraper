import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, renderHook } from '@testing-library/react';
import { useIOSKeyboardPanGuard } from '../../../src/hooks/ui/useIOSKeyboardPanGuard';

vi.mock('@festival/ui-utils', async () => {
	const actual = await vi.importActual<typeof import('@festival/ui-utils')>('@festival/ui-utils');
	return { ...actual, IS_IOS: true, IS_ANDROID: false, IS_PWA: false };
});

class MockVisualViewport extends EventTarget {
	height = 844;
	offsetTop = 0;
}

function setWindowScroll(x: number, y: number) {
	Object.defineProperty(window, 'scrollX', { configurable: true, value: x });
	Object.defineProperty(window, 'scrollY', { configurable: true, value: y });
}

describe('useIOSKeyboardPanGuard', () => {
	let visualViewport: MockVisualViewport;
	let scrollToSpy: ReturnType<typeof vi.fn>;

	beforeEach(() => {
		vi.useFakeTimers({ shouldAdvanceTime: true });
		document.documentElement.removeAttribute('style');
		document.body.removeAttribute('style');
		visualViewport = new MockVisualViewport();
		Object.defineProperty(window, 'visualViewport', { configurable: true, value: visualViewport });
		setWindowScroll(0, 40);
		scrollToSpy = vi.fn((x: number, y: number) => setWindowScroll(x, y));
		Object.defineProperty(window, 'scrollTo', { configurable: true, value: scrollToSpy });
		vi.spyOn(window, 'requestAnimationFrame').mockImplementation((callback) => window.setTimeout(() => callback(0), 0));
		vi.spyOn(window, 'cancelAnimationFrame').mockImplementation((id) => window.clearTimeout(id));
	});

	afterEach(() => {
		vi.restoreAllMocks();
		vi.useRealTimers();
		document.documentElement.removeAttribute('style');
		document.body.removeAttribute('style');
	});

	it('locks document and scroll container pan in modal mode', () => {
		const scrollContainer = document.createElement('div');
		scrollContainer.style.overflowY = 'auto';
		scrollContainer.scrollTop = 120;
		const scrollContainerRef = { current: scrollContainer };

		const { unmount } = renderHook(() => useIOSKeyboardPanGuard({
			active: true,
			mode: 'modal',
			scrollContainerRef,
		}));
		act(() => vi.runOnlyPendingTimers());

		expect(document.documentElement.style.overflow).toBe('hidden');
		expect(document.body.style.overflow).toBe('hidden');
		expect(document.documentElement.style.touchAction).toBe('none');
		expect(document.body.style.position).toBe('fixed');
		expect(document.body.style.top).toBe('0px');
		expect(scrollContainer.style.overflowY).toBe('hidden');

		scrollContainer.scrollTop = 260;
		setWindowScroll(0, 90);
		act(() => {
			scrollContainer.dispatchEvent(new Event('scroll'));
			vi.runOnlyPendingTimers();
		});

		expect(scrollToSpy).toHaveBeenCalledWith(0, 40);
		expect(scrollContainer.scrollTop).toBe(120);

		unmount();

		expect(document.documentElement.style.overflow).toBe('');
		expect(document.body.style.overflow).toBe('');
		expect(document.documentElement.style.touchAction).toBe('');
		expect(document.body.style.position).toBe('');
		expect(scrollContainer.style.overflowY).toBe('auto');
	});

	it('keeps page scroll containers usable in page mode while restoring document pan', () => {
		const scrollContainer = document.createElement('div');
		scrollContainer.style.overflowY = 'auto';
		scrollContainer.scrollTop = 120;
		const scrollContainerRef = { current: scrollContainer };

		const { unmount } = renderHook(() => useIOSKeyboardPanGuard({
			active: true,
			mode: 'page',
			scrollContainerRef,
		}));
		act(() => vi.runOnlyPendingTimers());

		expect(document.documentElement.style.overflow).toBe('hidden');
		expect(document.body.style.overflow).toBe('hidden');
		expect(document.documentElement.style.touchAction).toBe('');
		expect(document.body.style.position).toBe('');
		expect(scrollContainer.style.overflowY).toBe('auto');

		scrollContainer.scrollTop = 260;
		setWindowScroll(0, 90);
		act(() => {
			visualViewport.dispatchEvent(new Event('resize'));
			vi.runOnlyPendingTimers();
		});

		expect(scrollToSpy).toHaveBeenCalledWith(0, 40);
		expect(scrollContainer.scrollTop).toBe(260);

		unmount();
	});

	it('restores document pan without fixing body or root in floating-page mode', () => {
		const scrollContainer = document.createElement('div');
		scrollContainer.style.overflowY = 'auto';
		scrollContainer.scrollTop = 120;
		const scrollContainerRef = { current: scrollContainer };

		const { unmount } = renderHook(() => useIOSKeyboardPanGuard({
			active: true,
			mode: 'floating-page',
			scrollContainerRef,
		}));
		act(() => vi.runOnlyPendingTimers());

		expect(document.documentElement.style.overflow).toBe('hidden');
		expect(document.body.style.overflow).toBe('hidden');
		expect(document.documentElement.style.touchAction).toBe('');
		expect(document.body.style.position).toBe('');
		expect(document.getElementById('root')?.style.position ?? '').toBe('');
		expect(scrollContainer.style.overflowY).toBe('auto');

		scrollContainer.scrollTop = 260;
		setWindowScroll(0, 90);
		act(() => {
			visualViewport.dispatchEvent(new Event('scroll'));
			vi.runOnlyPendingTimers();
		});

		expect(scrollToSpy).toHaveBeenCalledWith(0, 40);
		expect(scrollContainer.scrollTop).toBe(260);

		unmount();

		expect(scrollContainer.style.overflowY).toBe('auto');
	});
});
