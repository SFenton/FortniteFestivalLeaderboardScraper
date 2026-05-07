import { useLayoutEffect, type RefObject } from 'react';
import { IS_IOS } from '@festival/ui-utils';

export type IOSKeyboardPanGuardMode = 'modal' | 'page' | 'floating-page';

interface IOSKeyboardPanGuardOptions {
	active: boolean;
	mode: IOSKeyboardPanGuardMode;
	scrollContainerRef?: RefObject<HTMLElement | null>;
}

interface DocumentLockSnapshot {
	rootOverflow: string;
	rootOverscrollBehavior: string;
	bodyOverflow: string;
	bodyOverscrollBehavior: string;
}

interface FixedBodyLockSnapshot {
	rootTouchAction: string;
	bodyPosition: string;
	bodyTop: string;
	bodyLeft: string;
	bodyRight: string;
	bodyWidth: string;
	bodyHeight: string;
	bodyTouchAction: string;
	scrollX: number;
	scrollY: number;
}

let documentLockCount = 0;
let documentLockSnapshot: DocumentLockSnapshot | null = null;
let fixedBodyLockCount = 0;
let fixedBodyLockSnapshot: FixedBodyLockSnapshot | null = null;

function acquireDocumentPanLock(): () => void {
	const root = document.documentElement;
	const body = document.body;

	if (documentLockCount === 0) {
		documentLockSnapshot = {
			rootOverflow: root.style.overflow,
			rootOverscrollBehavior: root.style.overscrollBehavior,
			bodyOverflow: body.style.overflow,
			bodyOverscrollBehavior: body.style.overscrollBehavior,
		};
		root.style.overflow = 'hidden';
		root.style.overscrollBehavior = 'none';
		body.style.overflow = 'hidden';
		body.style.overscrollBehavior = 'none';
	}

	documentLockCount += 1;
	let released = false;

	return () => {
		if (released) return;
		released = true;
		documentLockCount = Math.max(0, documentLockCount - 1);
		if (documentLockCount > 0 || !documentLockSnapshot) return;

		root.style.overflow = documentLockSnapshot.rootOverflow;
		root.style.overscrollBehavior = documentLockSnapshot.rootOverscrollBehavior;
		body.style.overflow = documentLockSnapshot.bodyOverflow;
		body.style.overscrollBehavior = documentLockSnapshot.bodyOverscrollBehavior;
		documentLockSnapshot = null;
	};
}

function acquireFixedBodyLock(scrollX: number, scrollY: number): () => void {
	const root = document.documentElement;
	const body = document.body;

	if (fixedBodyLockCount === 0) {
		fixedBodyLockSnapshot = {
			rootTouchAction: root.style.touchAction,
			bodyPosition: body.style.position,
			bodyTop: body.style.top,
			bodyLeft: body.style.left,
			bodyRight: body.style.right,
			bodyWidth: body.style.width,
			bodyHeight: body.style.height,
			bodyTouchAction: body.style.touchAction,
			scrollX,
			scrollY,
		};
		root.style.touchAction = 'none';
		body.style.position = 'fixed';
		body.style.top = '0';
		body.style.left = '0';
		body.style.right = '0';
		body.style.width = '100%';
		body.style.height = '100%';
		body.style.touchAction = 'none';
	}

	fixedBodyLockCount += 1;
	let released = false;

	return () => {
		if (released) return;
		released = true;
		fixedBodyLockCount = Math.max(0, fixedBodyLockCount - 1);
		if (fixedBodyLockCount > 0 || !fixedBodyLockSnapshot) return;

		root.style.touchAction = fixedBodyLockSnapshot.rootTouchAction;
		body.style.position = fixedBodyLockSnapshot.bodyPosition;
		body.style.top = fixedBodyLockSnapshot.bodyTop;
		body.style.left = fixedBodyLockSnapshot.bodyLeft;
		body.style.right = fixedBodyLockSnapshot.bodyRight;
		body.style.width = fixedBodyLockSnapshot.bodyWidth;
		body.style.height = fixedBodyLockSnapshot.bodyHeight;
		body.style.touchAction = fixedBodyLockSnapshot.bodyTouchAction;
		window.scrollTo(fixedBodyLockSnapshot.scrollX, fixedBodyLockSnapshot.scrollY);
		fixedBodyLockSnapshot = null;
	};
}

export function useIOSKeyboardPanGuard({ active, mode, scrollContainerRef }: IOSKeyboardPanGuardOptions) {
	useLayoutEffect(() => {
		if (!active || !IS_IOS) return;

		const root = document.documentElement;
		const body = document.body;
		const scrollContainer = scrollContainerRef?.current ?? null;
		const anchor = {
			windowX: window.scrollX,
			windowY: window.scrollY,
			rootScrollTop: root.scrollTop,
			bodyScrollTop: body.scrollTop,
			scrollContainerLeft: scrollContainer?.scrollLeft ?? 0,
			scrollContainerTop: scrollContainer?.scrollTop ?? 0,
		};
		const releaseDocumentLock = acquireDocumentPanLock();
		const releaseFixedBodyLock = mode === 'modal'
			? acquireFixedBodyLock(anchor.windowX, anchor.windowY)
			: undefined;
		const savedScrollContainerOverflowY = scrollContainer?.style.overflowY ?? '';
		const savedScrollContainerOverscrollBehavior = scrollContainer?.style.overscrollBehavior ?? '';
		const lockScrollContainer = mode === 'modal' && !!scrollContainer;

		if (lockScrollContainer && scrollContainer) {
			scrollContainer.style.overflowY = 'hidden';
			scrollContainer.style.overscrollBehavior = 'none';
		}

		let restoreFrame = 0;
		const restorePan = () => {
			restoreFrame = 0;
			if (window.scrollX !== anchor.windowX || window.scrollY !== anchor.windowY) {
				window.scrollTo(anchor.windowX, anchor.windowY);
			}
			if (root.scrollTop !== anchor.rootScrollTop) root.scrollTop = anchor.rootScrollTop;
			if (body.scrollTop !== anchor.bodyScrollTop) body.scrollTop = anchor.bodyScrollTop;
			if (lockScrollContainer && scrollContainer) {
				if (scrollContainer.scrollLeft !== anchor.scrollContainerLeft) scrollContainer.scrollLeft = anchor.scrollContainerLeft;
				if (scrollContainer.scrollTop !== anchor.scrollContainerTop) scrollContainer.scrollTop = anchor.scrollContainerTop;
			}
		};

		const scheduleRestorePan = () => {
			if (restoreFrame) return;
			restoreFrame = window.requestAnimationFrame(restorePan);
		};

		const handlePanSignal = () => {
			if (mode === 'modal' || mode === 'floating-page') restorePan();
			scheduleRestorePan();
		};

		const visualViewport = window.visualViewport;
		visualViewport?.addEventListener('resize', handlePanSignal);
		visualViewport?.addEventListener('scroll', handlePanSignal);
		scrollContainer?.addEventListener('scroll', handlePanSignal, { passive: true });
		window.addEventListener('scroll', handlePanSignal, { passive: true });
		handlePanSignal();

		return () => {
			visualViewport?.removeEventListener('resize', handlePanSignal);
			visualViewport?.removeEventListener('scroll', handlePanSignal);
			scrollContainer?.removeEventListener('scroll', handlePanSignal);
			window.removeEventListener('scroll', handlePanSignal);
			if (restoreFrame) window.cancelAnimationFrame(restoreFrame);
			restorePan();
			if (lockScrollContainer && scrollContainer) {
				scrollContainer.style.overflowY = savedScrollContainerOverflowY;
				scrollContainer.style.overscrollBehavior = savedScrollContainerOverscrollBehavior;
			}
			releaseFixedBodyLock?.();
			releaseDocumentLock();
		};
	}, [active, mode, scrollContainerRef]);
}
