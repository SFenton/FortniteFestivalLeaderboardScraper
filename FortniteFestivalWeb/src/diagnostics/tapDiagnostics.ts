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
  }
}

const MAX_RECORDS = 240;
const EVENT_TYPES = ['pointerdown', 'pointerup', 'click', 'touchstart', 'touchend'];

type TapDiagnosticsInstallOptions = {
  force?: boolean;
  documentRef?: Document;
  windowRef?: Window;
};

export function isTapDiagnosticsEnabled(windowRef: Window = window): boolean {
  const params = new URLSearchParams(windowRef.location.search);
  if (params.get('tapDiagnostics') === '1') return true;
  const validation = params.get('validation') ?? '';
  return validation.split(/[,:;]/).includes('tap-diagnostics');
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
  if (!options.force && !isTapDiagnosticsEnabled(windowRef)) return null;

  const records: TapDiagnosticsRecord[] = [];
  const pushRecord = (record: TapDiagnosticsRecord) => {
    records.push(record);
    if (records.length > MAX_RECORDS) records.splice(0, records.length - MAX_RECORDS);
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

  return {
    api,
    dispose: () => {
      for (const eventType of EVENT_TYPES) {
        documentRef.removeEventListener(eventType, recordEvent, { capture: true });
      }
      if (windowRef.__fstTapDiagnostics === api) delete windowRef.__fstTapDiagnostics;
    },
  };
}
