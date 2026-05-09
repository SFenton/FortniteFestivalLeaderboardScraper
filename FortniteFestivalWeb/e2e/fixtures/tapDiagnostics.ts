import { expect, type Locator, type Page, type TestInfo } from '@playwright/test';

declare global {
  interface Window {
    __fstTapDiagnostics?: {
      reset: () => void;
      dump: (limit?: number) => TapDiagnosticsDump;
      markAction: (label: string, phase: 'start' | 'success' | 'failure' | 'note', details?: Record<string, unknown>) => void;
    };
  }
}

type TapDiagnosticsDump = {
  state: Record<string, unknown>;
  records: Record<string, unknown>[];
} | null;

type TapAndExpectOptions = {
  timeout?: number;
  retryOnFailure?: boolean;
};

export async function gotoWithTapDiagnostics(page: Page, route: string) {
  await page.goto(`/?tapDiagnostics=1#${route}`, { waitUntil: 'load' });
  await page.waitForFunction(() => Boolean(window.__fstTapDiagnostics), null, { timeout: 10_000 });
  await page.evaluate(() => window.__fstTapDiagnostics?.reset());
  await page.waitForTimeout(250);
}

export async function getTapDiagnosticsDump(page: Page, limit = 80): Promise<TapDiagnosticsDump> {
  return page.evaluate((recordLimit) => window.__fstTapDiagnostics?.dump(recordLimit) ?? null, limit);
}

export async function markTapAction(
  page: Page,
  label: string,
  phase: 'start' | 'success' | 'failure' | 'note',
  details?: Record<string, unknown>,
) {
  await page.evaluate(
    ({ actionLabel, actionPhase, actionDetails }) => window.__fstTapDiagnostics?.markAction(actionLabel, actionPhase, actionDetails),
    { actionLabel: label, actionPhase: phase, actionDetails: details },
  );
}

export async function tapAndExpect(
  page: Page,
  testInfo: TestInfo,
  label: string,
  locator: Locator,
  predicate: () => Promise<boolean>,
  options: TapAndExpectOptions = {},
) {
  const timeout = options.timeout ?? 2_000;
  await markTapAction(page, label, 'start', { url: page.url() });
  const firstClickError = await clickLocator(locator);
  if (firstClickError) {
    let retrySucceeded = false;
    let retryClickError: string | null = null;
    const firstDump = await getTapDiagnosticsDump(page);
    if (options.retryOnFailure) {
      await markTapAction(page, label, 'note', { retry: true, firstClickError });
      retryClickError = await clickLocator(locator);
      retrySucceeded = retryClickError ? false : await waitForPredicate(predicate, timeout);
    }
    const finalDump = await getTapDiagnosticsDump(page);
    const classification = `click action could not be dispatched: ${firstClickError}`;
    await markTapAction(page, label, 'failure', { classification, retrySucceeded, retryClickError });
    await attachTapFailure(testInfo, label, {
      label,
      url: page.url(),
      classification,
      retrySucceeded,
      retryClickError,
      firstDump,
      finalDump,
    });
    throw new Error(`Tap action could not be dispatched: ${label}. ${firstClickError}. Retry succeeded: ${retrySucceeded}`);
  }

  const firstTapSucceeded = await waitForPredicate(predicate, timeout);
  if (firstTapSucceeded) {
    await markTapAction(page, label, 'success');
    return;
  }

  const firstDump = await getTapDiagnosticsDump(page);
  let retrySucceeded = false;
  let retryClickError: string | null = null;
  if (options.retryOnFailure) {
    await markTapAction(page, label, 'note', { retry: true });
    retryClickError = await clickLocator(locator);
    retrySucceeded = retryClickError ? false : await waitForPredicate(predicate, timeout);
  }

  const finalDump = await getTapDiagnosticsDump(page);
  const failure = {
    label,
    url: page.url(),
    classification: classifyTapFailure(firstDump),
    retrySucceeded,
    retryClickError,
    firstDump,
    finalDump,
  };
  await markTapAction(page, label, 'failure', { classification: failure.classification, retrySucceeded });
  await attachTapFailure(testInfo, label, failure);
  throw new Error(`Tap action did not respond: ${label}. ${failure.classification}. Retry succeeded: ${retrySucceeded}`);
}

export async function expectTapPredicate(
  page: Page,
  testInfo: TestInfo,
  label: string,
  predicate: () => Promise<boolean>,
  timeout = 2_000,
) {
  const succeeded = await waitForPredicate(predicate, timeout);
  if (succeeded) return;
  const dump = await getTapDiagnosticsDump(page);
  await testInfo.attach(`${safeAttachmentName(label)}-tap-diagnostics.json`, {
    body: JSON.stringify({ label, classification: classifyTapFailure(dump), dump }, null, 2),
    contentType: 'application/json',
  });
  expect(succeeded, `Expected tap action state: ${label}`).toBe(true);
}

async function waitForPredicate(predicate: () => Promise<boolean>, timeout: number) {
  const started = Date.now();
  while (Date.now() - started < timeout) {
    if (await predicate()) return true;
    await new Promise(resolve => setTimeout(resolve, 50));
  }
  return predicate();
}

async function clickLocator(locator: Locator) {
  try {
    await locator.click({ timeout: 5_000 });
    return null;
  } catch (error) {
    return error instanceof Error ? error.message : String(error);
  }
}

async function attachTapFailure(testInfo: TestInfo, label: string, failure: Record<string, unknown>) {
  await testInfo.attach(`${safeAttachmentName(label)}-tap-diagnostics.json`, {
    body: JSON.stringify(failure, null, 2),
    contentType: 'application/json',
  });
}

function classifyTapFailure(dump: TapDiagnosticsDump) {
  const records = dump?.records ?? [];
  const events = records.filter(record => record.kind === 'event');
  const pointerDown = events.filter(record => record.eventType === 'pointerdown' || record.eventType === 'touchstart');
  const clicks = events.filter(record => record.eventType === 'click');
  if (pointerDown.length === 0 && clicks.length === 0) return 'no pointerdown/touchstart/click reached the document capture listener';
  if (pointerDown.length > 0 && clicks.length === 0) return 'pointer reached the document, but no click event fired';
  const lastClick = clicks.at(-1);
  const target = describeElement(lastClick?.target as Record<string, unknown> | undefined);
  const hitTarget = describeElement(lastClick?.hitTarget as Record<string, unknown> | undefined);
  if (target !== hitTarget) return `click target ${target}, hit-test target ${hitTarget}`;
  return `click reached ${target}, but expected app state did not change`;
}

function describeElement(element?: Record<string, unknown>) {
  if (!element) return 'none';
  const tag = String(element.tag ?? 'element');
  const testId = element.testId ? `[data-testid=${String(element.testId)}]` : '';
  const ariaLabel = element.ariaLabel ? `[aria-label=${String(element.ariaLabel)}]` : '';
  const text = element.text ? ` "${String(element.text)}"` : '';
  return `${tag}${testId}${ariaLabel}${text}`;
}

function safeAttachmentName(label: string) {
  return label.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '') || 'tap-action';
}
