export type TapDiagnosticsState = Record<string, unknown>;

export type TapDiagnosticsElement = {
  tag: string;
  id?: string;
  testId?: string;
  role?: string;
  ariaLabel?: string;
  text?: string;
  className?: string;
  pointerEvents?: string;
  display?: string;
  visibility?: string;
  position?: string;
  zIndex?: string;
};

export type TapDiagnosticsEventRecord = {
  kind: 'event';
  eventType: string;
  time: number;
  clientX: number | null;
  clientY: number | null;
  button: number | null;
  pointerType: string | null;
  target: TapDiagnosticsElement | null;
  hitTarget: TapDiagnosticsElement | null;
  path: TapDiagnosticsElement[];
  state: TapDiagnosticsState;
};

export type TapDiagnosticsActionRecord = {
  kind: 'action';
  label: string;
  phase: 'start' | 'success' | 'failure' | 'note';
  time: number;
  details?: Record<string, unknown>;
  state: TapDiagnosticsState;
};

export type TapDiagnosticsRecord = TapDiagnosticsEventRecord | TapDiagnosticsActionRecord;

type TapTelemetryElement = Omit<TapDiagnosticsElement, 'ariaLabel' | 'text'>;

type TapTelemetryRecord = {
  kind: TapDiagnosticsRecord['kind'];
  eventType?: string;
  label?: string;
  phase?: TapDiagnosticsActionRecord['phase'];
  time: number;
  clientX?: number | null;
  clientY?: number | null;
  button?: number | null;
  pointerType?: string | null;
  target?: TapTelemetryElement | null;
  hitTarget?: TapTelemetryElement | null;
  path?: TapTelemetryElement[];
  state: Record<string, unknown>;
};

type TapTelemetryPayload = {
  sessionId: string;
  capturedAtUtc: string;
  route?: string;
  viewport: {
    width: number;
    height: number;
    devicePixelRatio: number;
    visualViewportWidth?: number;
    visualViewportHeight?: number;
    visualViewportOffsetTop?: number;
  };
  events: TapTelemetryRecord[];
};

export type TapDiagnosticsTelemetryOptions = {
  enabled?: boolean;
  endpoint?: string;
  sessionId?: string;
  flushDelayMs?: number;
  maxBatchSize?: number;
  fetchRef?: typeof fetch;
};

export type TapDiagnosticsApi = {
  enabled: true;
  reset: () => void;
  dump: (limit?: number) => { state: TapDiagnosticsState; records: TapDiagnosticsRecord[] };
  getRecords: () => TapDiagnosticsRecord[];
  getState: () => TapDiagnosticsState;
  markAction: (label: string, phase: TapDiagnosticsActionRecord['phase'], details?: Record<string, unknown>) => void;
};

declare global {
  interface Window {
    __fstTapDiagnostics?: TapDiagnosticsApi;
    __fstInteractionTelemetry?: TapDiagnosticsApi;
  }
}

const MAX_RECORDS = 240;
const EVENT_TYPES = ['pointerdown', 'pointerup', 'click', 'touchstart', 'touchend'];
export const TAP_DIAGNOSTICS_STORAGE_KEY = 'fst.tapDiagnostics';
export const TAP_TELEMETRY_STORAGE_KEY = 'fst.tapTelemetry';
export const TAP_DIAGNOSTICS_SETTINGS_EVENT = 'fst:tap-diagnostics-settings-changed';
const ENABLED_STORAGE_VALUES = new Set(['1', 'true', 'yes', 'on']);

export type TapDiagnosticsPreference = 'diagnostics' | 'telemetry';

type TapDiagnosticsInstallOptions = {
  force?: boolean;
  documentRef?: Document;
  windowRef?: Window;
  telemetry?: TapDiagnosticsTelemetryOptions;
};

export function isTapDiagnosticsEnabled(windowRef: Window = window): boolean {
  const params = new URLSearchParams(windowRef.location.search);
  if (params.get('tapDiagnostics') === '1') return true;
  const validation = params.get('validation') ?? '';
  if (validation.split(/[,:;]/).includes('tap-diagnostics')) return true;
  return isStorageFlagEnabled(windowRef, TAP_DIAGNOSTICS_STORAGE_KEY);
}

export function isTapDiagnosticsUiAvailable(): boolean {
  return import.meta.env.DEV || import.meta.env.VITE_FST_INTERACTION_TELEMETRY === 'true';
}

export function isTapTelemetryEnabled(windowRef: Window = window): boolean {
  if (!isTapDiagnosticsUiAvailable()) return false;

  const params = new URLSearchParams(windowRef.location.search);
  if (params.get('tapTelemetry') === '1') return true;
  const validation = params.get('validation') ?? '';
  if (validation.split(/[,:;]/).includes('tap-telemetry')) return true;
  return isStorageFlagEnabled(windowRef, TAP_TELEMETRY_STORAGE_KEY);
}

export function getTapDiagnosticsPreference(preference: TapDiagnosticsPreference, windowRef?: Window): boolean {
  const targetWindow = windowRef ?? (typeof window !== 'undefined' ? window : undefined);
  if (!targetWindow) return false;
  return isStorageFlagEnabled(targetWindow, getTapDiagnosticsPreferenceKey(preference));
}

export function setTapDiagnosticsPreference(preference: TapDiagnosticsPreference, enabled: boolean, windowRef?: Window): void {
  const targetWindow = windowRef ?? (typeof window !== 'undefined' ? window : undefined);
  if (!targetWindow) return;

  try {
    const key = getTapDiagnosticsPreferenceKey(preference);
    if (enabled) targetWindow.localStorage.setItem(key, '1');
    else targetWindow.localStorage.removeItem(key);
  } catch {
    return;
  }

  targetWindow.dispatchEvent(new Event(TAP_DIAGNOSTICS_SETTINGS_EVENT));
}

export function markTapDiagnosticsAction(
  label: string,
  phase: TapDiagnosticsActionRecord['phase'],
  details?: Record<string, unknown>,
  windowRef?: Window,
): void {
  const targetWindow = windowRef ?? (typeof window !== 'undefined' ? window : undefined);
  targetWindow?.__fstTapDiagnostics?.markAction(label, phase, details);
}

function getTapDiagnosticsPreferenceKey(preference: TapDiagnosticsPreference): string {
  return preference === 'diagnostics' ? TAP_DIAGNOSTICS_STORAGE_KEY : TAP_TELEMETRY_STORAGE_KEY;
}

function isStorageFlagEnabled(windowRef: Window, key: string): boolean {
  try {
    const value = windowRef.localStorage?.getItem(key);
    return value ? ENABLED_STORAGE_VALUES.has(value.toLowerCase()) : false;
  } catch {
    return false;
  }
}

export function summarizeTapElement(element: EventTarget | null, documentRef: Document = document): TapDiagnosticsElement | null {
  if (!(element instanceof documentRef.defaultView!.Element)) return null;
  const htmlElement = element instanceof documentRef.defaultView!.HTMLElement ? element : null;
  const style = htmlElement ? documentRef.defaultView!.getComputedStyle(htmlElement) : null;
  const text = htmlElement?.innerText?.replace(/\s+/g, ' ').trim();
  return {
    tag: element.tagName.toLowerCase(),
    id: element.id || undefined,
    testId: element.getAttribute('data-testid') || undefined,
    role: element.getAttribute('role') || undefined,
    ariaLabel: element.getAttribute('aria-label') || undefined,
    text: text ? text.slice(0, 80) : undefined,
    className: typeof element.className === 'string' && element.className ? element.className : undefined,
    pointerEvents: style?.pointerEvents,
    display: style?.display,
    visibility: style?.visibility,
    position: style?.position,
    zIndex: style?.zIndex,
  };
}

export function createTapDiagnostics(
  getState: () => TapDiagnosticsState,
  options: TapDiagnosticsInstallOptions = {},
): { api: TapDiagnosticsApi; dispose: () => void } | null {
  if (typeof window === 'undefined' && !options.windowRef) return null;
  const windowRef = options.windowRef ?? window;
  const documentRef = options.documentRef ?? windowRef.document;
  if (!options.force && !isTapDiagnosticsEnabled(windowRef) && !isTapTelemetryEnabled(windowRef)) return null;

  const records: TapDiagnosticsRecord[] = [];
  const telemetry = createTapTelemetryTransport(windowRef, options.telemetry);
  const pushRecord = (record: TapDiagnosticsRecord) => {
    records.push(record);
    if (records.length > MAX_RECORDS) records.splice(0, records.length - MAX_RECORDS);
    telemetry?.enqueue(record);
  };

  const snapshotState = () => {
    try {
      return getState();
    } catch (error) {
      return { diagnosticStateError: error instanceof Error ? error.message : String(error) };
    }
  };

  const getPoint = (event: Event): { clientX: number | null; clientY: number | null } => {
    if ('clientX' in event && typeof event.clientX === 'number') {
      return { clientX: event.clientX, clientY: 'clientY' in event && typeof event.clientY === 'number' ? event.clientY : null };
    }
    const TouchEventCtor = windowRef.TouchEvent;
    if (typeof TouchEventCtor === 'function' && event instanceof TouchEventCtor) {
      const touch = event.changedTouches[0] ?? event.touches[0];
      return touch ? { clientX: touch.clientX, clientY: touch.clientY } : { clientX: null, clientY: null };
    }
    return { clientX: null, clientY: null };
  };

  const getPath = (event: Event) => {
    const path = typeof event.composedPath === 'function' ? event.composedPath() : [];
    return path
      .map(item => summarizeTapElement(item, documentRef))
      .filter((item): item is TapDiagnosticsElement => item !== null)
      .slice(0, 10);
  };

  const recordEvent = (event: Event) => {
    const { clientX, clientY } = getPoint(event);
    const hitTarget = clientX != null && clientY != null
      ? documentRef.elementFromPoint(clientX, clientY)
      : null;
    pushRecord({
      kind: 'event',
      eventType: event.type,
      time: windowRef.performance.now(),
      clientX,
      clientY,
      button: 'button' in event && typeof event.button === 'number' ? event.button : null,
      pointerType: 'pointerType' in event && typeof event.pointerType === 'string' ? event.pointerType : null,
      target: summarizeTapElement(event.target, documentRef),
      hitTarget: summarizeTapElement(hitTarget, documentRef),
      path: getPath(event),
      state: snapshotState(),
    });
  };

  const api: TapDiagnosticsApi = {
    enabled: true,
    reset: () => { records.splice(0, records.length); },
    dump: (limit = MAX_RECORDS) => ({ state: snapshotState(), records: records.slice(-limit) }),
    getRecords: () => records.slice(),
    getState: snapshotState,
    markAction: (label, phase, details) => pushRecord({
      kind: 'action',
      label,
      phase,
      details,
      time: windowRef.performance.now(),
      state: snapshotState(),
    }),
  };

  for (const eventType of EVENT_TYPES) {
    documentRef.addEventListener(eventType, recordEvent, { capture: true, passive: true });
  }
  windowRef.__fstTapDiagnostics = api;
  windowRef.__fstInteractionTelemetry = api;

  return {
    api,
    dispose: () => {
      telemetry?.dispose();
      for (const eventType of EVENT_TYPES) {
        documentRef.removeEventListener(eventType, recordEvent, { capture: true });
      }
      if (windowRef.__fstTapDiagnostics === api) delete windowRef.__fstTapDiagnostics;
      if (windowRef.__fstInteractionTelemetry === api) delete windowRef.__fstInteractionTelemetry;
    },
  };
}

function createTapTelemetryTransport(windowRef: Window, options?: TapDiagnosticsTelemetryOptions) {
  if (!options?.enabled) return null;

  const fetchRef = options.fetchRef ?? windowRef.fetch?.bind(windowRef);
  if (!fetchRef) return null;

  const endpoint = options.endpoint ?? '/api/debug/client-interactions';
  const sessionId = options.sessionId ?? createTapTelemetrySessionId(windowRef);
  const flushDelayMs = options.flushDelayMs ?? 400;
  const maxBatchSize = options.maxBatchSize ?? 20;
  const queue: TapDiagnosticsRecord[] = [];
  let flushTimer: number | null = null;
  let inFlight = false;

  const clearFlushTimer = () => {
    if (flushTimer == null) return;
    windowRef.clearTimeout(flushTimer);
    flushTimer = null;
  };

  const flush = () => {
    clearFlushTimer();
    if (inFlight || queue.length === 0) return;

    const records = queue.splice(0, maxBatchSize);
    const payload = buildTapTelemetryPayload(windowRef, sessionId, records);
    inFlight = true;
    void fetchRef(endpoint, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload),
      keepalive: true,
    }).catch(() => {
      // Telemetry is diagnostic-only; network failures must never affect UI input.
    }).finally(() => {
      inFlight = false;
      if (queue.length > 0) scheduleFlush();
    });
  };

  const scheduleFlush = () => {
    if (flushTimer != null) return;
    if (queue.length >= maxBatchSize) {
      flush();
      return;
    }
    flushTimer = windowRef.setTimeout(flush, flushDelayMs);
  };

  return {
    enqueue: (record: TapDiagnosticsRecord) => {
      queue.push(record);
      scheduleFlush();
    },
    dispose: () => {
      clearFlushTimer();
      flush();
    },
  };
}

function createTapTelemetrySessionId(windowRef: Window): string {
  const random = windowRef.crypto?.randomUUID?.() ?? Math.random().toString(36).slice(2);
  return `tap-${Date.now().toString(36)}-${random}`;
}

function buildTapTelemetryPayload(windowRef: Window, sessionId: string, records: TapDiagnosticsRecord[]): TapTelemetryPayload {
  const visualViewport = windowRef.visualViewport;
  const firstState = records[0]?.state;
  const route = typeof firstState?.pathname === 'string' ? firstState.pathname : undefined;

  return {
    sessionId,
    capturedAtUtc: new Date().toISOString(),
    route,
    viewport: {
      width: windowRef.innerWidth,
      height: windowRef.innerHeight,
      devicePixelRatio: windowRef.devicePixelRatio || 1,
      visualViewportWidth: visualViewport?.width,
      visualViewportHeight: visualViewport?.height,
      visualViewportOffsetTop: visualViewport?.offsetTop,
    },
    events: records.map(sanitizeTapTelemetryRecord),
  };
}

function sanitizeTapTelemetryRecord(record: TapDiagnosticsRecord): TapTelemetryRecord {
  if (record.kind === 'action') {
    return {
      kind: 'action',
      label: sanitizeString(record.label, 80),
      phase: record.phase,
      time: Math.round(record.time),
      state: sanitizeTapTelemetryState(record.state),
    };
  }

  return {
    kind: 'event',
    eventType: record.eventType,
    time: Math.round(record.time),
    clientX: record.clientX == null ? null : Math.round(record.clientX),
    clientY: record.clientY == null ? null : Math.round(record.clientY),
    button: record.button,
    pointerType: record.pointerType,
    target: sanitizeTapTelemetryElement(record.target),
    hitTarget: sanitizeTapTelemetryElement(record.hitTarget),
    path: record.path.map(sanitizeTapTelemetryElement).filter((item): item is TapTelemetryElement => item !== null),
    state: sanitizeTapTelemetryState(record.state),
  };
}

function sanitizeTapTelemetryElement(element: TapDiagnosticsElement | null): TapTelemetryElement | null {
  if (!element) return null;
  return {
    tag: sanitizeString(element.tag, 32) ?? '',
    id: sanitizeString(element.id, 80),
    testId: sanitizeString(element.testId, 120),
    role: sanitizeString(element.role, 40),
    className: sanitizeString(element.className, 160),
    pointerEvents: sanitizeString(element.pointerEvents, 24),
    display: sanitizeString(element.display, 32),
    visibility: sanitizeString(element.visibility, 32),
    position: sanitizeString(element.position, 32),
    zIndex: sanitizeString(element.zIndex, 32),
  };
}

function sanitizeTapTelemetryState(state: TapDiagnosticsState): Record<string, unknown> {
  const sanitized: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(state)) {
    if (key === 'search' || key === 'hash') continue;
    if (key === 'player') {
      sanitized.hasPlayer = value != null;
      continue;
    }
    if (key === 'selectedProfile' && value && typeof value === 'object' && 'type' in value) {
      sanitized.selectedProfile = { type: sanitizeString(String((value as { type?: unknown }).type), 24) };
      continue;
    }
    if (key === 'fabReady' && value && typeof value === 'object') {
      sanitized.fabReady = sanitizePrimitiveObject(value as Record<string, unknown>);
      continue;
    }
    if (typeof value === 'boolean' || typeof value === 'number') {
      sanitized[key] = value;
      continue;
    }
    if (typeof value === 'string') {
      sanitized[key] = sanitizeString(value, key === 'pathname' ? 160 : 80);
    }
  }
  return sanitized;
}

function sanitizePrimitiveObject(value: Record<string, unknown>): Record<string, unknown> {
  const sanitized: Record<string, unknown> = {};
  for (const [key, item] of Object.entries(value)) {
    if (typeof item === 'boolean' || typeof item === 'number') sanitized[key] = item;
    else if (typeof item === 'string') sanitized[key] = sanitizeString(item, 80);
  }
  return sanitized;
}

function sanitizeString(value: string | undefined, maxLength: number): string | undefined {
  if (!value) return undefined;
  return value.length > maxLength ? value.slice(0, maxLength) : value;
}
