import { expect } from 'vitest';

type MockWithCalls = {
  mock: {
    calls: unknown[][];
  };
};

function trimTrailingUndefined(args: unknown[]): unknown[] {
  const normalized = [...args];
  while (normalized.length > 0 && normalized[normalized.length - 1] === undefined) normalized.pop();
  return normalized;
}

function cancellableCalls(mock: MockWithCalls): unknown[][] {
  return mock.mock.calls.flatMap(call => {
    const options = call[call.length - 1];
    if (
      !options
      || typeof options !== 'object'
      || !('signal' in options)
      || !((options as { signal?: unknown }).signal instanceof AbortSignal)
    ) {
      return [];
    }
    return [trimTrailingUndefined(call.slice(0, -1))];
  });
}

export function expectCancellableCall(mock: MockWithCalls, ...args: unknown[]): void {
  expect(cancellableCalls(mock)).toContainEqual(trimTrailingUndefined(args));
}

export function expectNoCancellableCall(mock: MockWithCalls, ...args: unknown[]): void {
  expect(cancellableCalls(mock)).not.toContainEqual(trimTrailingUndefined(args));
}

export function expectCancellableNthCall(mock: MockWithCalls, callIndex: number, ...args: unknown[]): void {
  const call = mock.mock.calls[callIndex - 1];
  expect(call, `Missing call ${callIndex}`).toBeDefined();
  const options = call?.[call.length - 1];
  expect((options as { signal?: unknown } | undefined)?.signal).toBeInstanceOf(AbortSignal);
  expect(trimTrailingUndefined(call?.slice(0, -1) ?? [])).toEqual(trimTrailingUndefined(args));
}
