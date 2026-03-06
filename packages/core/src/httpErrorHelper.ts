type ExtractedError = {errorCode?: string; errorMessage?: string};

// Lightweight in-memory counter (process-scoped) similar to the C# static dictionary.
const errorCodeCounts = new Map<string, number>();

export const __resetForTests = (): void => {
  errorCodeCounts.clear();
};

export const extractError = (json: string | null | undefined): ExtractedError => {
  if (!json) return {};
  const trimmed = json.trim();
  if (!trimmed.startsWith('{')) return {};
  try {
    const root = JSON.parse(trimmed) as Record<string, unknown>;
    const errorCode = typeof root.errorCode === 'string' ? root.errorCode : undefined;
    const errorMessage =
      typeof root.errorMessage === 'string'
        ? root.errorMessage
        : typeof root.message === 'string'
          ? root.message
          : undefined;
    if (errorCode) errorCodeCounts.set(errorCode, (errorCodeCounts.get(errorCode) ?? 0) + 1);
    return {errorCode, errorMessage};
  } catch {
    return {};
  }
};

const truncate = (s: string, len: number): string => (s.length <= len ? s : `${s.slice(0, len)}...`);

export const formatHttpError = (params: {
  op: string;
  status: number;
  statusText?: string;
  body?: string | null;
  errorCode?: string;
  errorMessage?: string;
}): string => {
  const snippetRaw = (params.body ?? '<no-body>').replace(/\r|\n/g, ' ');
  const snippet = truncate(snippetRaw, 180);
  const parts = [
    `[${params.op}] HTTP ${params.status}${params.statusText ? ` (${params.statusText})` : ''}`,
    `errorCode=${params.errorCode ?? '<none>'}`,
  ];
  if (params.errorMessage && params.errorMessage.trim().length > 0) {
    parts.push(`msg="${truncate(params.errorMessage, 120)}"`);
  }
  parts.push(`bodySnippet=${snippet}`);
  return parts.join(' ');
};

export const buildSummaryLine = (): string => {
  if (errorCodeCounts.size === 0) return 'ErrorCodeSummary: <none>';
  const parts = [...errorCodeCounts.entries()]
    .sort((a, b) => b[1] - a[1])
    .map(([k, v]) => `${k}=${v}`);
  return `ErrorCodeSummary: ${parts.join(', ')}`;
};

// Portable correlation id: 8 hex chars from an FNV-1a 32-bit hash.
export const computeCorrelationId = (err: unknown): string => {
  const input = (() => {
    if (!err) return '';
    if (err instanceof Error) return `${err.name}|${err.message}|${String(err.stack ?? '').slice(0, 200)}`;
    return String(err);
  })();

  let hash = 0x811c9dc5;
  for (let i = 0; i < input.length; i++) {
    hash ^= input.charCodeAt(i);
    hash = (hash * 0x01000193) >>> 0;
  }
  return hash.toString(16).toUpperCase().padStart(8, '0').slice(0, 8);
};
