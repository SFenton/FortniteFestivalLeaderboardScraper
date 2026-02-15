import type {HttpClient, HttpResponse} from '@festival/core';

const asRecord = (headers?: Record<string, string>): Record<string, string> => headers ?? {};

async function safeReadText(res: Response): Promise<string> {
  try {
    return await res.text();
  } catch {
    return '';
  }
}

async function safeReadBytes(res: Response): Promise<Uint8Array> {
  try {
    const buf = await res.arrayBuffer();
    return new Uint8Array(buf);
  } catch {
    return new Uint8Array();
  }
}

export function createFetchHttpClient(): HttpClient {
  return {
    async getText(url, opts): Promise<HttpResponse> {
      const res = await fetch(url, {
        method: 'GET',
        headers: asRecord(opts?.headers),
        signal: opts?.signal,
      });
      const text = await safeReadText(res);
      return {ok: res.ok, status: res.status, text};
    },

    async postForm(url, form, opts): Promise<HttpResponse> {
      const body = new URLSearchParams();
      for (const [k, v] of Object.entries(form)) body.append(k, v);

      const res = await fetch(url, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/x-www-form-urlencoded',
          ...asRecord(opts?.headers),
        },
        body: body.toString(),
        signal: opts?.signal,
      });
      const text = await safeReadText(res);
      return {ok: res.ok, status: res.status, text};
    },

    async getBytes(url, opts): Promise<{ok: boolean; status: number; bytes: Uint8Array}> {
      const res = await fetch(url, {
        method: 'GET',
        headers: asRecord(opts?.headers),
        signal: opts?.signal,
      });
      const bytes = await safeReadBytes(res);
      return {ok: res.ok, status: res.status, bytes};
    },
  };
}
